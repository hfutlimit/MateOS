# Detailed Design · 01 · Single Agent Task Lifecycle

> **v0.4.3 修正**：Execution **必须**在 Agent 推 `collaboration.decision: ACCEPT` 之后由 E4 调 E7 内部 API 创建。
> 详细设计原版（P0-1 错误）：Resolver 选中后立即 create_execution → 把 execution lifecycle 提前到 decision 之前。
> 修复：Runtime Gateway 推 `collaboration.request`（transport 消息）→ Agent 推 `collaboration.decision` → ACCEPT 后 **E4 事务外同步直调 E7 创建 Execution**，同时写 `collaboration.accepted` outbox 事件**仅作兜底重试**（**D9 冻结口径**，与 04 §2.1 / 187 §1.1 一致）。
> 前置：[00-overview.md](./00-overview.md) / [04-resolver-and-routing.md](./04-resolver-and-routing.md) / [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)
>
> **Review (minimax m3 · 2026-09-12) · [P1-1]**:本节 :85 写 `execution.dispatch_ack { execution_id, attempt_no, accepted: true }`,沿用 v0.5 字段名。SYSTEM_DESIGN v0.7 changelog(行 1080)已冻结 `dispatch_ack` 语义定型为 transport 收据,`detailed/03:111-115` 与 `detailed/10:27` 已改 `received: true` + 可选 `protocol_error?: "UNKNOWN_EXECUTION" | "STALE_ATTEMPT"`。建议改 :85 字段名以与 03/10/schema 对齐,避免 SDK 解析两份不同字段名。详见 [2026-09-12-design-review.md P1-1](../review/2026-09-12-design-review.md#p1-1--executiondispatch_ack-字段名01-沿用-v05-旧字段未跟进-v07-修订)。
>
> **Review (minimax m3 · 2026-09-12) · [P1-2]**:本节 attempt 状态机内部读写口径不一致 — :79 INSERT 写 `status='STARTED'`,:99 收尾按 `status='RUNNING'` CAS,:408 文档自述 5 态含 STARTED。SYSTEM_DESIGN §6.3:738 状态机 `RUNNING → COMPLETED / FAILED` 不含 STARTED 迁移,但 §5.2:497 schema CHECK 含 STARTED。三选一:(a) 删 STARTED,统一从 RUNNING 起步;(b) 保留 STARTED,补 `STARTED → RUNNING` 迁移,01:99 改按 STARTED 找;(c) 在 SD §6.3 状态机加 STARTED 节点。详见 [2026-09-12-design-review.md P1-2](../review/2026-09-12-design-review.md#p1-2--execution_attempt-状态机01-内部读写口径不一致-sd-525263-分离)。

## 0. 范围

本文档描述从"用户发消息"到"Agent 输出回到消息流"的**完整端到端流程**，单 Agent 视角。

- **不**覆盖：多 Agent 协作（V2 Delegate）、Sandbox 执行（V3）
- **覆盖**：单 Agent 接收 dispatch → THINKING → ACCEPT → WORKING → events → result → 消息流投影
- **关键边界**（v0.4.3 修正）：
  - `collaboration.request`（E4 拥有，Runtime transport）= 触发 Agent 决策
  - `collaboration.decision`（E4 拥有）= Agent 决策结果
  - `execution.dispatch` / `event` / `result`（E7 拥有）= Execution 实际执行
  - **Execution 在 ACCEPT 之后才创建**（v0.4.3 修复）

## 1. 完整时序图（v0.4.3 修正）

```
T+0   User 在 P5 Channel 发消息 "@Backend Agent 帮我看看这段代码"
T+1   P5 客户端 POST /channels/:id/messages
T+2   E3 Channel Module 事务：
        - 写 messages (content_type=HUMAN, mentions=[{raw:'@Backend Agent', type:AGENT}])
        - 写 triggers (trigger_type='MENTION', ref={channel_id, message_seq})
        - 写 collaboration_requests (status=PENDING, target_agent_id=NULL,
                                     required_capabilities=[],
                                     context_refs={channel_id, ...})
T+3   E3 WS 广播 message.created 给 channel 在线 member
T+4   客户端渲染：消息泡 + mention 胶囊占位「⏳ 解析中」
T+5   E4 Orchestrator 异步 Resolver 启动（outbox relay 消费 mention.extracted，原 BullMQ mention.resolve）
T+6   Resolver:
        a) 硬过滤：lifecycle=ACTIVE ∩ channel member ∩ permission ALLOW
        b) Capability ranking: 0.6 * match + 0.25 * (1-load) + 0.15 * accept_rate_30d
        c) 选 Backend Agent (top-1)
        d) Redis Lua tryAcquireSlot(agent_id, lease_id=PENDING_DECISION)  # v0.4.3: lease 类型=pending_decision
        e) 写 collaboration_requests.target_agent_id + slot_lease_id
T+7   E4 → Runtime Gateway (WebSocket): 推 collaboration.request     # v0.4.3 新增
        { type: 'collaboration.request', payload: {
            collaboration_request_id, from_actor, context, required_capabilities, deadline_s
        }}
T+8   Agent 收到 collaboration.request:
        - 校验 collaboration_request_id
        - 推 status envelope: lifecycle=ACTIVE, activity=THINKING
T+9   E7 收到 status → agents.activity='THINKING' + WS 推前端
T+10  Agent 评估：
        - capability / context_score / permission 三件套
        - 推 execution.event { event_type: 'PROGRESS', payload: {content: '正在分析代码...'} }  # ⚠️ v0.4.3: 这是 thinking progress（不写 execution_events）— v0.4.3 改进：thinking 阶段用 collaboration.* 事件流（不进 execution_events 表）

T+11  Agent 决定 ACCEPT → 推 collaboration.decision  # v0.4.3 关键：Execution 还没创建
        { type: 'collaboration.decision', payload: {
            collaboration_request_id,
            decision: 'ACCEPT',
            reason: '...',
            analysis: { capability: true, context_score: 88, permission: true }
        }}
T+12  E4 收到 collaboration.decision:
        a) BEGIN transaction:
           - 写 decision_records (事实源，仅 E4 写)
           - UPDATE collaboration_requests SET status='ACCEPTED', resolved_at=now()
           - INSERT outbox_events (event_type='collaboration.accepted', payload={collab_id, agent_id, context_refs, ...})
        b) COMMIT
        c) E4 promotion：把 pending_decision lease 升级为 execution lease（详见 04 §3.4）
T+13  E4 事务外**同步**调 E7 创建 Execution（**D9 冻结口径：E4 直调 + outbox 兜底**）
        E7 内部 API: POST /internal/agent-executions
          { collaboration_request_id, work_item_ref?, input, context_refs, idempotency_key }
        - execution_id 由 E7 生成，E7 回写 collaboration_requests.target_execution_id
        - 调用失败**不**回滚 CR（CR 已是 ACCEPTED 终态），由 T+14b 兜底
T+14  E4 WS 推 collab.resolved 给发起人 + channel
T+14b Outbox Worker 拾取 'collaboration.accepted'（**仅兜底**）：
        a) 查 agent_executions WHERE collaboration_request_id=? → 已存在则 no-op（幂等）
        b) 不存在（T+13 失败/进程崩溃）才重发 POST /internal/agent-executions，退避重试直至成功或 DEAD
T+15  E7:
        a) 校验 Agent.lifecycle=ACTIVE
        b) BEGIN transaction:
           - INSERT agent_executions (status=PENDING, UNIQUE(collaboration_request_id))  # v0.4.3 修复 SQL 顺序
           - INSERT execution_attempts (attempt_no=1, status=STARTED, runtime_session_id=null)
           - UPDATE agent_executions SET active_attempt_no=1, attempt_count=1
           - INSERT outbox_events (event_type='execution.created', payload={execution_id, ...})
        c) COMMIT
        d) dispatch 给 Agent WS
T+16  Agent 收到 execution.dispatch:
        - 立即回 execution.dispatch_ack { execution_id, attempt_no, accepted: true }  # v0.5：ACK 是独立协议消息
        - 推 status envelope: activity=WORKING                                # activity 仅供 UI，不承载 ACK
        - 写 execution_attempts.status='RUNNING', started_at=now()             # STARTED → RUNNING 只在收到 dispatch 后
        - renewExecutionLease(execution_id)  # v0.4.3: lease 类型升级为 execution，定期续期
T+17  Agent 调用 LLM:
        - 推 execution.event { event_type: 'LLM_TICK', provider_event_id, seq=N }
        - 推 execution.event { event_type: 'TOOL_CALL', ... }
T+18  Agent 写出代码:
        - 推 execution.event { event_type: 'ARTIFACT', payload: {kind: 'FILE', s3_key} }
T+19  Agent 推 execution.result { status: 'SUCCEEDED', output, usage, artifacts }
T+20  E7 收到 result:
        a) BEGIN transaction:
           - CAS UPDATE agent_executions SET status='SUCCEEDED', completed_at=now(),
                 terminal_envelope_id=..., active_attempt_no=NULL
             WHERE id=? AND status IN ('PENDING','RUNNING')
           - UPDATE execution_attempts SET status='COMPLETED', completed_at=now()
             WHERE execution_id=? AND attempt_no=? AND status='RUNNING'   # v0.5：必须带 attempt 条件（见 §4.4）
           - INSERT llm_calls
           - INSERT outbox_events (event_type='execution.completed', payload={execution_id, status})
        b) COMMIT
T+21  E7 推 status envelope: activity=AVAILABLE
T+22  Outbox Worker 拾取 'execution.completed':
        a) 释放 E4 持有的 execution lease
        b) 通知 E4
T+23  E4 收到 execution.completed:
        - 消息流写 AGENT_OUTPUT 投影消息（content.execution_ref=execution.id）
T+24  E3 写 messages + seq 分配
T+25  E3 WS 广播 message.created 给 channel 在线 member
T+26  客户端渲染: 决策卡（v0.4.1 projection）+ Agent 输出卡
```

### 1.1 v0.4.3 关键修正

| 错误（v0.4.2） | 修正（v0.4.3） |
| --- | --- |
| Resolver 选中后立即 create_execution（Execution 在 ACCEPT 前） | Resolver 选中后只 tryAcquireSlot(pending_decision lease) + 推 collaboration.request；Execution 在 ACCEPT 后才创建 |
| E4 / E7 都写 decision_records（双事实源） | 仅 E4 写 decision_records（事实源） |
| Agent 在 thinking 阶段就推 execution.event PROGRESS（写入 execution_events 表） | Thinking 阶段用 collaboration.* 事件流（不进 execution_events，避免和 attempt_id 强耦合） |
| 状态变更后直接发 WS broadcast（半成功风险） | 状态变更 + outbox INSERT 同事务；outbox worker 投递下游 |
| execution_attempts INSERT 在 agent_executions 之前（FK 错误） | agent_executions 先，execution_attempts 后 |
| `MAX(seq)` resume cursor（gap 风险） | 改用 last_persisted_seq 严格 cursor（详见 03） |

## 2. Agent 内部状态机（v0.4.3 修正）

```
                      ┌────────────────────────────┐
                      │   activity 字段（UI only）  │
                      └────────────────────────────┘
                                      │
   ┌──────┬──────┬──────────┬──────────┼──────────┬──────────┐
   │      │      │          │          │          │          │
   ▼      ▼      ▼          ▼          ▼          ▼          ▼
 OFFLINE AVAILABLE THINKING  WORKING  WAITING_  ERROR    (no activity)
                                    CONTEXT
   │      │      │          │          │          │
   │      │      │          │          │          └─► owner 处理 → AVAILABLE
   │      │      │          │          └─► 人类补齐 → 重新 dispatch
   │      │      │          └─► result SUCCEEDED/FAILED → AVAILABLE
   │      │      └─► collaboration.decision → ACCEPT/REJECT/NEED_CONTEXT
   │      │                ├─ ACCEPT → WORKING
   │      │                ├─ REJECT → AVAILABLE
   │      │                └─ NEED_CONTEXT → WAITING_CONTEXT
   │      └─► hello / heartbeat → AVAILABLE
   └─► 心跳 > 90s 丢失 → OFFLINE
```

**关键**：
- activity 转换由 Runtime Gateway（E7）写入 DB + Redis presence
- slot 占用在 Redis **per-lease ZSET**（`agent-capacity:{agent_id}:leases`，v0.7；无 `used` 计数）
- lifecycle 由 owner REST 控制，与 activity 解耦

## 3. WS 协议（v0.4.3 修正）

### 3.1 完整 message type 清单

```jsonc
// ─── 连接管理 ───
{ "type": "hello",         "payload": { agent_id, agent_token, runtime_session_id, runtime_version, capabilities }}
{ "type": "hello_ack",     "payload": { session_id, server_version, config }}
{ "type": "hello_nack",    "payload": { code, message }}
{ "type": "heartbeat",     "payload": {} }

// ─── E4 拥有（v0.4.3 新增 collaboration.request）───
{ "type": "collaboration.request",  "payload": {  // ★ v0.4.3 新增
    "collaboration_request_id",
    "from_actor": { type, id },
    "context_refs": { channel_id, message_seq, memory_refs, work_item_ref? },
    "required_capabilities": ["coding"],
    "deadline_s"
}}

{ "type": "collaboration.decision", "payload": {
    "collaboration_request_id",
    "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason"?, "needs"?, "analysis": { capability, context_score, permission }
}}

{ "type": "collaboration.cancelled", "payload": {
    "collaboration_request_id", "reason"
}}

{ "type": "collaboration.resolved",   "payload": {   // server → client WS
    "collaboration_request_id", "status", "target_execution_id"?, "scores"?, "resolved_at"
}}

// ─── E7 拥有（Execution 域）───
{ "type": "execution.dispatch",   "payload": {
    "execution_id", "collaboration_request_id"?, "work_item_ref"?,
    "input": { prompt, params },
    "context": { memory_refs, recent_messages, permissions },
    "deadline_s", "idempotency_key"
}}

{ "type": "execution.event",        "payload": {
    "execution_id", "attempt_no",
    "event_type": "STDOUT|PROGRESS|TOOL_CALL|LLM_TICK|ARTIFACT|ERROR",
    "provider_event_id",   // 协议级幂等键
    "seq",                  // contiguous cursor（v0.4.3 修复 MAX(seq) gap）
    "payload": { content, meta }
}}

{ "type": "execution.result",       "payload": {
    "execution_id", "attempt_no",
    "status": "SUCCEEDED|FAILED|CANCELLED",
    "output": { markdown, code },
    "usage": { tokens_in, tokens_out, duration_ms },
    "artifacts": [{ kind, name, s3_key }]
}}

{ "type": "execution.error",        "payload": {
    "execution_id", "attempt_no",
    "code": "PROVIDER_5XX|PROVIDER_401|RATE_LIMIT|SANDBOX_INIT_FAILED|DEADLINE_EXCEEDED",
    "message", "retry_after_s"?
}}

{ "type": "execution.cancel",       "payload": {  // Runtime → Agent
    "execution_id", "attempt_no",
    "reason": "USER_CANCEL|LIFECYCLE_PAUSED|LIFECYCLE_DISABLED|DEADLINE_EXCEEDED|TIMEOUT_NO_RESUME"
}}

// ─── v0.4.3 反向 resume 协议（contiguous cursor）───
{ "type": "execution.resume_request", "payload": { execution_id, attempt_no }}

{ "type": "execution.resume_ack",     "payload": {
    "execution_id", "attempt_no",
    "last_persisted_seq",      // last_contiguous_seq（不是 MAX）
    "snapshot": { execution_id, input, context, deadline_s }
}}

// ─── Agent 上报 lifecycle/activity ───
{ "type": "status",       "payload": {
    "status": "OFFLINE|AVAILABLE|THINKING|WORKING|WAITING_CONTEXT|ERROR",
    "reason"?, "since"
}}
```

### 3.2 协议边界（v0.4.3 强制）

| 消息 | 拥有方 | 不允许携带 |
| --- | --- | --- |
| `collaboration.request` | E4（transport 经 E7 Gateway） | `execution_id` |
| `collaboration.decision` | E4 | `execution_id` |
| `collaboration.cancelled` | E4 | `execution_id` |
| `execution.dispatch` | E7 | `decision` / `analysis` / `needs` |
| `execution.event` | E7 | `decision` / `analysis` |
| `execution.result` | E7 | `decision` / `reason` / `analysis` / `needs` |
| `execution.error` | E7 | `decision` |
| `execution.cancel` | E7 | — |
| `execution.resume_request` | E7 | — |
| `execution.resume_ack` | E7 | — |

## 4. Transactional Outbox（P0-3 新增）

### 4.1 模式

任何"状态变更"+"下游事件"的组合必须**同事务**写入：

```python
# 伪代码
async def emit_event(aggregate_type, aggregate_id, event_type, payload):
    async with db.transaction() as tx:
        # 1. 状态变更
        ...  # UPDATE / INSERT 业务表

        # 2. 写 outbox
        await tx.execute("""
            INSERT INTO outbox_events
            (aggregate_type, aggregate_id, event_type, payload, created_at)
            VALUES ($1, $2, $3, $4, NOW())
        """, aggregate_type, aggregate_id, event_type, payload)

    # 事务外：worker 异步投递
    # 但 PG 提供持久化保证
```

### 4.2 outbox_events 表

> **权威 DDL 在 `SYSTEM_DESIGN` §5.2**（v0.5 收口，含 `status ∈ {PENDING, PUBLISHED, DEAD}` 与 relay 索引）。本节仅列本流程用到的字段。

```sql
-- 摘录（完整定义见 SYSTEM_DESIGN §5.2）
CREATE TABLE outbox_events (
  id              UUID PRIMARY KEY,
  aggregate_type  TEXT NOT NULL,        -- 'collaboration' | 'execution' | 'work_item' | ...
  aggregate_id    UUID NOT NULL,
  event_type      TEXT NOT NULL,        -- 'collaboration.accepted' | 'execution.completed' | ...
  payload         JSONB NOT NULL,
  -- 幂等：同 (aggregate, event) 不能投递两次
  idempotency_key TEXT UNIQUE,          -- 消费者侧去重
  -- 投递状态（v0.5：DEAD 需要落表，否则超阈值无法表达）
  status          TEXT NOT NULL DEFAULT 'PENDING'
                  CHECK (status IN ('PENDING','PUBLISHED','DEAD')),
  attempt_count   INT NOT NULL DEFAULT 0,
  next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_error      TEXT,
  published_at    TIMESTAMPTZ,
  created_at      TIMESTAMPTZ DEFAULT now()
);
-- relay 领取：WHERE status='PENDING' AND next_attempt_at <= now() ORDER BY ... FOR UPDATE SKIP LOCKED
CREATE INDEX idx_outbox_ready ON outbox_events(next_attempt_at) WHERE status = 'PENDING';
```

### 4.3 outbox worker

```python
async def outbox_worker():
    while True:
        events = await db.query("""
            SELECT * FROM outbox_events
            WHERE published_at IS NULL
              AND next_attempt_at <= NOW()
            ORDER BY created_at
            LIMIT 100
        """)
        for event in events:
            try:
                await dispatch_event(event)  # 按 aggregate_type 路由
                await db.update("""
                    UPDATE outbox_events SET published_at = NOW() WHERE id = $1
                """, event.id)
            except Exception as e:
                # 指数退避
                backoff = 2 ** event.attempt_count
                await db.update("""
                    UPDATE outbox_events
                    SET attempt_count = attempt_count + 1,
                        next_attempt_at = NOW() + ($1 || ' seconds')::INTERVAL,
                        last_error = $2
                    WHERE id = $3
                """, str(backoff), str(e), event.id)
```

### 4.4 关键路径（v0.4.3）

**ACCEPT 路径**：

```python
async def handle_collab_decision_ACCEPT(collab_id, agent_id, analysis):
    async with db.transaction() as tx:
        # 1. 写 decision_records（仅 E4 写，事实源）
        await tx.execute("""
            INSERT INTO decision_records
            (collaboration_request_id, agent_id, decision, reason, analysis, decided_at)
            VALUES ($1, $2, 'ACCEPT', $3, $4, NOW())
        """, collab_id, agent_id, ..., analysis)

        # 2. CAS collab status
        affected = await tx.execute("""
            UPDATE collaboration_requests
            SET status='ACCEPTED', resolved_at=NOW(),
                target_agent_id=$2
            WHERE id=$1 AND status='PENDING'
            RETURNING id
        """, collab_id, agent_id)

        if not affected:
            raise StaleStateError()

        # 3. 写 outbox
        await tx.execute("""
            INSERT INTO outbox_events
            (aggregate_type, aggregate_id, event_type, payload, idempotency_key)
            VALUES ('collaboration', $1, 'collaboration.accepted', $2, $3)
        """, collab_id, {...}, f"collab-accepted-{collab_id}")

    # 4. 事务外：upgrade pending_decision lease → execution lease
    await promote_lease(agent_id, lease_id)

    # 5. outbox worker 异步拾取
```

**Execution 完成路径**：

```python
async def handle_execution_result(execution_id, attempt_no, envelope_id, status):
    """
    v0.4.4：attempt_no 必须由调用方显式带入（来自 result envelope / 内部计时器），
    禁止再从 agent_executions 反查——终态 CAS 会先把 active_attempt_no 置 NULL，
    反查必得 NULL，attempt 永远收不了尾。
    """
    async with db.transaction() as tx:
        # 0. 锁定并校验当前 attempt（隔离旧 attempt 的迟到结果）
        row = await tx.fetchrow("""
            SELECT active_attempt_no FROM agent_executions
            WHERE id = $1 FOR UPDATE
        """, execution_id)
        if row is None or row['active_attempt_no'] != attempt_no:
            return  # 旧 attempt 的迟到结果 / 未知 execution：直接丢弃

        # 1. CAS 终态（带 active_attempt_no 条件，杜绝跨 attempt 收尾）
        affected = await tx.execute("""
            UPDATE agent_executions
            SET status = $2, completed_at = NOW(), active_attempt_no = NULL,
                terminal_envelope_id = $3
            WHERE id = $1 AND status IN ('PENDING', 'RUNNING')
              AND active_attempt_no = $4
            RETURNING id
        """, execution_id, status, envelope_id, attempt_no)

        if not affected:
            return  # 重复 / stale / attempt 已切换

        # 2. 结束该 attempt（用入参 attempt_no，不再反查）
        # v0.5：attempt 枚举只有 STARTED/RUNNING/COMPLETED/FAILED/INTERRUPTED（SYSTEM_DESIGN §5.2），
        # 不能把 Execution 的 TIMEOUT / CANCELLED 直接写进 attempt.status
        attempt_status = {
            'SUCCEEDED': 'COMPLETED',
            'FAILED':    'FAILED',
            'CANCELLED': 'INTERRUPTED',
            'TIMEOUT':   'INTERRUPTED',
        }[status]
        await tx.execute("""
            UPDATE execution_attempts
            SET status = $3, completed_at = NOW()
            WHERE execution_id = $1 AND attempt_no = $2 AND status = 'RUNNING'
        """, execution_id, attempt_no, attempt_status)

        # 3. 写 outbox
        await tx.execute("""
            INSERT INTO outbox_events
            (aggregate_type, aggregate_id, event_type, payload, idempotency_key)
            VALUES ('execution', $1, 'execution.completed', $2, $3)
        """, execution_id, {...}, f"exec-completed-{envelope_id}")

    # 事务外：outbox worker → 释放 lease + 写 AGENT_OUTPUT 投影
```

**v0.4.4 修正（attempt 收尾 + 旧 attempt 隔离）**：

- 原写法先 `SET active_attempt_no = NULL`，下一条 SQL 又用 `active_attempt_no` 反查要结束的 attempt —— 必然匹配 0 行，**attempt 永远停在 RUNNING**。
- 现写法：入参显式带 `attempt_no`；事务内先 `SELECT ... FOR UPDATE` 锁定 Execution 并比对 `active_attempt_no`；CAS 增加 `AND active_attempt_no = $attempt_no`；attempt 只收尾 `status='RUNNING'` 的那一条。
- 迟到结果隔离：attempt 1 的 result 在 attempt 2 已启动后到达 → `active_attempt_no != attempt_no` → 丢弃，不会把正在运行的 Execution 判成终态。
- 调用方：`execution.result` envelope 必须携带 `execution_id + attempt_no`（与 `03-ws` 的 `execution.cancel` / `resume_request` 同一套契约）。
- 同样的问题在 `detailed/08` §3 `terminal_fail()` / `terminal_timeout()` 里存在，需按同一模式补 `attempt_no`（见 08 的 v0.4.4 注）。

### 4.5 解决的具体问题

| 场景 | 旧（v0.4.2） | 新（v0.4.3 outbox） |
| --- | --- | --- |
| CAS SUCCEEDED 成功，进程 crash，emit 没执行 | 状态变 SUCCEEDED，slot 永远不释放 | 状态变 + outbox 已落；outbox worker 重启后投递 |
| ACCEPT DB 写成功，HTTP 调用 E7 失败 | collab=ACCEPTED，execution 不存在 | collab=ACCEPTED + outbox 已落；worker 重发 HTTP，E7 幂等（UNIQUE(collaboration_request_id)） |
| 消息广播丢失 | 用户看不到 | outbox worker 持续 retry，pub/sub 兜底 |
| network partition 时 | 部分状态变更 | 全部 or 全部不（事务性） |

## 5. Idempotency 关键约束

```sql
```sql
-- E4 → E7 create execution 幂等
-- 见 §4.5：UNIQUE(collaboration_request_id) 保证一个 CR 至多一个 Execution
-- 创建路径有两条，但只有一条会真正生效（T+13 直调优先；T+14b 兜底仅在前者失败/崩溃时执行）
```
ALTER TABLE agent_executions
  ADD CONSTRAINT uq_executions_collab UNIQUE (collaboration_request_id);

-- Memory approval 幂等
ALTER TABLE memory_items
  ADD CONSTRAINT uq_memory_items_proposal UNIQUE (proposal_id);

-- CollaborationRequest 同一 active binding 唯一
-- 已在 v0.4.1 / v0.4.2 处理
```

## 6. 时序约束（关键 SLA）

| 步骤 | P95 目标 | 关键路径 |
| --- | --- | --- |
| 消息 → 写 triggers + collab_request | < 50ms | PG 写 |
| Resolver 选 Agent + tryAcquireSlot | < 500ms | Redis Lua + capability 算分 |
| E4 推 `collaboration.request` WS | < 200ms | WS push |
| Agent 推 `collaboration.decision` | 由 Agent 决定 | LLM 三件套判断 |
| **ACCEPT → E4 写 decision + CAS CR + outbox + E4 直调 E7 建 Execution** | **< 300ms** | **PG 事务 + outbox 投递 + 内部 API** |
| E7 dispatch 到 Agent | < 200ms | WS push |
| Agent 推 result | 由 Agent 决定 | LLM 主导 |
| E7 CAS + outbox | < 200ms | PG 事务 |
| **outbox worker 投递 → 释放 lease + 消息流投影** | **< 500ms** | **outbox + 内部 API + WS 广播** |
| **端到端** | **由 LLM 决定** | **大头在 LLM latency** |

## 7. 关键不变量

1. **execution_id 唯一**：一个 `collaboration.accepted` outbox 事件对应一个 execution（UNIQUE(collaboration_request_id) + outbox 幂等）
2. **attempt_no 单调**：每个 execution 的 attempt_no 从 1 开始，严格 +1
3. **provider_event_id 单 attempt 内唯一**：UNIQUE(attempt_id, provider_event_id) DB 强制
4. **envelope.id 全局唯一**：Runtime 给每个 envelope 分配 UUID，DB 落 `terminal_envelope_id` 去重
5. **CAS 状态转换**：所有终态变更都带 `WHERE status IN ('PENDING','RUNNING')`
6. **outbox 事务性**：状态变更 + outbox INSERT 同事务（要么都有要么都无）
7. **Decision-before-Execution**（v0.4.3 关键）：Execution 只在 collab.status=ACCEPTED 后创建 —— **由 E4 事务外同步直调 E7**（D9 冻结口径），outbox worker 只在直调失败/进程崩溃时兜底补建
8. **Decision 事实源唯一**：decision_records 仅 E4 写，E7 绝不允许写

## 8. 失败处理

| 失败点 | 行为 | 重试 |
| --- | --- | --- |
| E4 → E7 内部 API 失败（T+13 直调） | CR 已是 ACCEPTED 不回滚；由 T+14b outbox worker 兜底补建 + 指数退避 | 自动 |
| E7 dispatch 失败（Agent 离线） | collab.timeout_s（默认 600s）触发 E4 取消 + 释放 lease | 超时 |
| Agent 推 result 失败（WS 断） | 落 `agent_executions.status='RUNNING'`，等 Agent 重连 | resume_request |
| Agent 推 status 失败 | 落 `agents.activity=OFFLINE`（90s 后由 heartbeat 扫） | 重新 dispatch |
| E7 CAS 失败（status 已是终态） | 忽略，audit 记 "stale_terminal" | 不会重复释放 lease |
| outbox worker 兜底投递失败 | attempt_count + 1，next_attempt_at 退避 | 自动 retry |
| DB crash | outbox_events 在 PG → 事务保证；DB 恢复后 outbox worker 继续 | 启动时扫未投递 |

## 9. E2E 验收点

```
e2e/01-single-agent-lifecycle/
  test_001_message_mention_to_result.json
    Given Agent B 在 #webhook-retry 频道
    When User 发 "@Backend Agent 看下重试逻辑"
    Then T+200ms 看到 DECISION 投影（ACCEPT）
    And  T+1-30s 看到 AGENT_OUTPUT 投影
    And  Agent activity 依次：AVAILABLE → THINKING → WORKING → AVAILABLE
    And  agent_executions.status 最终为 SUCCEEDED
    And  decision_records 仅 E4 写（E7 绝不能写）
    And  outbox_events 全量投递

  test_002_execution_after_accept.json     # v0.4.3 新增
    Given Resolver 选中 Agent
    When Agent 推 collaboration.decision=REJECT
    Then 不创建 execution（agent_executions 无新行）
    And  lease 立即释放

  test_003_outbox_fallback_durability.json   # v0.4.3 新增（v0.9 校准注释）
    Given E4 写完 decision + outbox
    When T+13 直调 E7 之前进程 crash（Execution 尚未创建）
    Then 重启后 outbox worker **兜底**调 E7 创建 execution
    And  collab_request.status=ACCEPTED（不变）
    # 注意：正常路径下 Execution 由 T+13 E4 直调创建，此用例只覆盖兜底分支

  test_004_idempotency.json
    When 重复推同一个 result envelope.id
    Then DB 状态不变，第二次 ignored

  test_005_cas_protection.json
    When cancel + result 同时到
    Then 只一个 CAS 成功（affected_rows=1），另一个 ignored
```

## 10. 与其他设计的关系

- 详见 [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)（`collaboration.request` 协议细节）
- 详见 [04-resolver-and-routing.md](./04-resolver-and-routing.md)（Slot reservation 不创建 execution）
- 详见 [02-interrupt-and-cancel.md](./02-interrupt-and-cancel.md)（ACCEPTED 独立 lifecycle）
- 详见 [08-error-and-retry.md](./08-error-and-retry.md)（retry 与永久失败降级）
