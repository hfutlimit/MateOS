# Detailed Design · 03 · WebSocket Connection and Resume

> **v0.4.3 修正**：
> 1. **P0-1 配合**：新增 `collaboration.request` 协议（E4 拥有，transport 经 Runtime Gateway）
> 2. **P1-1**：Runtime restart 区分 resume vs re-dispatch（dispatch_acked_at 字段）
> 3. **P1-2**：`MAX(seq)` 改 contiguous cursor（`last_persisted_seq`）
> 前置：[00-overview.md](./00-overview.md) / [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)

## 0. 范围

- WS 连接生命周期
- 完整 message type 清单（**v0.4.3 新增 `collaboration.request`**）
- 断线 / 重连 / 恢复：resume_request / resume_ack
- 多 Agent 复用单连接（V2+）
- v0.4.3 新增：`dispatch_acked_at` 跟踪 Runtime restart 行为
- v0.4.3 新增：contiguous cursor（`last_persisted_seq`）

## 1. 连接生命周期

（同 v0.4.2，详见 01 §1.1）

```
Agent 进程
  → TCP/TLS 连接到 wss://api/runtime
  → hello
  → hello_ack
  → ready 状态（推任何 envelope）
  → heartbeat 30s
  → close 帧
```

## 2. 完整 message type（v0.4.3）

```jsonc
// ─── 连接管理 ───
{ "type": "hello",        "id": "uuid", "ts": 1736380800000, "payload": { ... }}
{ "type": "hello_ack",    "id": "uuid", "ts": ..., "payload": { ... }}
{ "type": "hello_nack",   "id": "uuid", "ts": ..., "payload": { code, message }}
{ "type": "heartbeat",    "id": "uuid", "ts": ..., "payload": {} }

// ─── E4 拥有（v0.4.3 新增 collaboration.request）───
{ "type": "collaboration.request",     "payload": {
    "collaboration_request_id",
    "from_actor": { type, id },
    "context_refs": { channel_id, message_seq, memory_refs, work_item_ref? },
    "required_capabilities": ["coding"],
    "deadline_s"
}}

{ "type": "collaboration.decision",    "payload": {
    "collaboration_request_id",
    "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason"?, "needs"?, "analysis": { capability, context_score, permission }
}}

{ "type": "collaboration.cancelled",   "payload": {
    "collaboration_request_id", "reason"
}}

{ "type": "collaboration.resolved",     "payload": {
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
    "provider_event_id",
    "seq",                           // contiguous cursor
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

// ─── v0.4.3 反向 resume（contiguous cursor）───
{ "type": "execution.resume_request", "payload": { execution_id, attempt_no }}

{ "type": "execution.resume_ack",     "payload": {
    "execution_id", "attempt_no",
    "last_persisted_seq",           // last contiguous seq（不是 MAX）
    "snapshot": { execution_id, input, context, deadline_s }
}}

// ─── v0.7 dispatch ACK = transport receipt（收据，不是第二次业务 acceptance）───
{ "type": "execution.dispatch_ack", "payload": {
    "execution_id", "attempt_no",
    "received": true,
    "protocol_error"?: "UNKNOWN_EXECUTION|STALE_ATTEMPT"   // 仅协议层异常；不表达业务接受与否
}}

// ─── Agent 上报 lifecycle/activity（纯 UI 语义，不承载协议状态）───
{ "type": "status",       "payload": {
    "status": "OFFLINE|AVAILABLE|THINKING|WORKING|WAITING_CONTEXT|ERROR",
    "reason"?, "since"
}}
```

> **v0.7 语义定型 —— 两件事必须分开**：
> - `collaboration.decision = ACCEPT` = **我要不要接这个工作**（业务接受；发生在 Resolver 阶段，Agent 承诺后 CR 进入终态 `ACCEPTED` 且 E7 创建唯一 Execution）
> - `execution.dispatch_ack` = **我有没有收到这次执行指令**（transport 收据；只用于回填 `dispatch_acked_at` 与恢复判定）
>
> 因此 ACK **没有** `accepted=false` 分支，也不会出现「`ACCEPTED` 之后要求重路由」这种在 CR 状态机里**没有合法迁移**、且被 `UNIQUE(collaboration_request_id)` 挡住的情况。容量在 Resolver 阶段已由 lease 预占（SYSTEM_DESIGN §4.2.1）。
>
> `status` 只表达 **Agent activity**（给 UI 看）。用 `status=WORKING + ids` 反推 ACK 的做法已废弃：Agent 的真实路径可能是 `THINKING → WAITING_CONTEXT → WORKING`，会漏掉"已收到但还没 WORKING"的窗口。

## 3. Resume 协议（v0.4.3 反向 + contiguous cursor）

### 3.1 为什么反向

**v0.4.2 旧设计**（错的）：

```
Agent → resume { last_event_seq: 42 }
Runtime → 补发 seq 43+  // event 是 Agent 产，Runtime 补发是错的
```

**v0.4.3 新设计**：

```
Agent → resume_request { execution_id, attempt_no }
Runtime → resume_ack { last_persisted_seq, snapshot }
Agent → 从 seq+1 续发
```

### 3.2 contiguous cursor（v0.4.3 修复 P1-2）

**v0.4.2 bug**：

```
SQL: last_persisted_seq = MAX(seq) WHERE attempt_id=?
  - seq 40 persisted
  - seq 41 lost (network blip)
  - seq 42 persisted
  - MAX = 42
  - Agent 从 43 开始
  - seq 41 永久丢失
```

**v0.4.3 修复**：

```sql
-- execution_attempts 加 last_persisted_seq 字段
ALTER TABLE execution_attempts
  ADD COLUMN last_persisted_seq BIGINT NOT NULL DEFAULT 0;
```

Event insert 严格 cursor：

```python
async def insert_event(attempt_id, event):
    async with db.transaction() as tx:
        # 1. 读 expected
        row = await tx.execute("""
            SELECT last_persisted_seq FROM execution_attempts
            WHERE id = $1 FOR UPDATE
        """, attempt_id)
        expected = row.last_persisted_seq + 1

        # 2. 三态判断
        if event.seq < expected:
            # 重复 / stale
            log.info(f'duplicate event seq={event.seq}, expected={expected}')
            return  # 忽略，不抛错

        if event.seq > expected:
            # gap：拒收，请求 resend
            log.warning(f'event seq={event.seq} > expected={expected}, gap detected')
            # 推 WS resume_request 让 Agent 重新发从 expected 开始
            await runtime.push_to_agent(agent_id, {
                'type': 'execution.resume_request',
                'payload': { 'execution_id': ..., 'attempt_no': ..., 'expected_seq': expected }
            })
            raise GapDetected()

        # 3. seq == expected → 写入
        await tx.execute("""
            INSERT INTO execution_events (attempt_id, event_type, provider_event_id, seq, payload)
            VALUES ($1, $2, $3, $4, $5)
        """, attempt_id, event.event_type, event.provider_event_id, event.seq, event.payload)

        # 4. 更新 cursor
        await tx.execute("""
            UPDATE execution_attempts SET last_persisted_seq = $1 WHERE id = $2
        """, event.seq, attempt_id)
```

**关键**：
- `last_persisted_seq` 严格 contiguous（v0.4.2 的 `MAX(seq)` 会跳过 gap）
- seq < expected → 重复，忽略
- seq > expected → gap，拒收 + 主动 resume_request
- seq == expected → 写入

### 3.3 resume_ack 用 last_persisted_seq

```python
async def handle_resume_request(agent_id, execution_id, attempt_no):
    # 1. 校验 attempt
    attempt = await get_attempt(execution_id, attempt_no)
    if not attempt:
        return  # 已不存在

    # 2. 读 contiguous cursor
    last_seq = attempt.last_persisted_seq

    # 3. 返回 resume_ack + dispatch snapshot
    execution = await get_execution(execution_id)
    await runtime.push_to_agent(agent_id, {
        'type': 'execution.resume_ack',
        'payload': {
            'execution_id': execution_id,
            'attempt_no': attempt_no,
            'last_persisted_seq': last_seq,
            'snapshot': {
                'execution_id': execution_id,
                'input': execution.input,
                'context': execution.context_refs,
                'deadline_s': ...
            }
        }
    })
```

### 3.4 完整时序

```
T0  Agent 正常执行（seq=1, 2, 3, 4, 5）
T1  WS 断开
T2  Runtime: 不取消 attempt，等 resume
T3  Agent 重连，hello（新 runtime_session_id）
T4  Runtime: 更新 attempt.runtime_session_id
T5  Agent 推 resume_request { execution_id, attempt_no }
T6  Runtime: 读 attempt.last_persisted_seq = 5
T7  Runtime: 返回 resume_ack { last_persisted_seq: 5, snapshot }
T8  Agent: 从 seq=6 开始续发 event
T9  Runtime: seq=6 == 5+1 → INSERT + UPDATE last_persisted_seq=6
T10 Agent: seq=7 → INSERT + UPDATE
T11 Agent: 推 result
T12 Runtime: CAS terminal
```

### 3.5 边界情况

| 场景 | 行为 |
| --- | --- |
| `last_persisted_seq=0` | Agent 重新完整执行（不发 event，直接推 result 或重跑） |
| Agent 重发 event with same provider_event_id | UNIQUE 冲突 → 忽略 |
| Agent 重发 event with new provider_event_id + seq < expected | cursor 拒收 |
| Agent 重发 event with seq > expected | gap detected → 推 resume_request 让 Agent 从 expected 重发 |
| Agent 重发 result with same envelope.id | terminal_envelope_id 已存在 → 忽略 |
| Agent 重发 result with new envelope.id + status='SUCCEEDED' | CAS 失败（已是 SUCCEEDED）→ 忽略 |
| 长时断线（> 5 分钟）| Runtime 主动 cancel（详见 02） |

## 4. Runtime Restart 行为（v0.4.3 修复 P1-1）

### 4.1 v0.4.2 矛盾

**v0.4.2 同时说**：
- 短断线：Runtime 不发新 dispatch，等 resume
- Runtime restart：加载 in-flight，重新 dispatch

**矛盾**：
- 如果 Execution 已 RUNNING，Agent 正在写文件
- Runtime 重启，**重发 dispatch** → Agent 重新跑任务 → 重复写文件

### 4.2 v0.4.3 修复：dispatch_acked_at 跟踪

```sql
ALTER TABLE execution_attempts
  ADD COLUMN dispatch_sent_at TIMESTAMPTZ,
  ADD COLUMN dispatch_acked_at  TIMESTAMPTZ;  -- Agent 收到 dispatch 后推 status=WORKING 时回填
```

```python
async def on_runtime_startup():
    """Runtime 启动时"""
    inflight = await db.query("""
        SELECT * FROM agent_executions
        WHERE status IN ('PENDING', 'RUNNING')
    """)

    for execution in inflight:
        attempt = await get_active_attempt(execution)
        if not attempt:
            continue

        # v0.4.3 关键：区分 PENDING 还是 RUNNING
        if attempt.dispatch_acked_at is None:
            # Agent 还没 ACK（可能没收到 dispatch）
            # 安全：重新 dispatch
            await runtime.dispatch(attempt)
            log.info(f'Re-dispatched PENDING execution {execution.id}')
        else:
            # Agent 已 ACK，正在执行
            # **绝不** re-dispatch
            # 等 Agent reconnect + resume_request
            log.info(f'Execution {execution.id} is RUNNING, waiting for resume')

    # 短断线：等 resume
    # 长断线（> 5min）：cancel + 通知
```

**关键规则（v0.4.3 冻结）**：

| Attempt 状态 | Runtime restart 行为 |
| --- | --- |
| PENDING（dispatch_acked_at=null） | 重发 dispatch |
| RUNNING（dispatch_acked_at 已 set） | **不**重发，等 resume_request |
| 无 attempt（异常） | cancel + 通知 owner |

### 4.3 完整启动流程

```python
async def on_runtime_startup():
    # 1. 加载配置
    # 2. PG 迁移检查
    # 3. 加载 in-flight executions
    inflight = await load_inflight_executions()

    # 4. 分类处理
    for execution in inflight:
        attempt = execution.active_attempt
        if not attempt:
            await cancel_execution(execution, 'NO_ACTIVE_ATTEMPT')
            continue

        if attempt.dispatch_acked_at is None:
            # Agent 可能没收到 dispatch（connection lost）
            await re_dispatch(execution, attempt)
        else:
            # Agent 正在执行中
            # 不动，等 Agent reconnect + resume_request
            # 设置长断线 watchdog
            schedule_long_disconnect_check(execution.id, timeout_minutes=5)

    # 5. 启动 heartbeat sweeper
    # 6. 启动 outbox worker
    # 7. 启动 WS 端口
```

## 5. Idempotency 三层保护

### 5.1 协议级去重（envelope.id）

- 所有 envelope 必填 `id`（UUIDv4）
- Runtime 记录最近 1 小时 envelope.id 集合（Redis Set，TTL 1h）
- 重复 envelope.id 立即忽略

### 5.2 业务级去重（event provider_event_id + seq cursor）

- DB UNIQUE(attempt_id, provider_event_id)：重复 event 忽略
- DB UNIQUE(attempt_id, seq)：同 attempt 内 seq 唯一
- v0.4.3 新增：cursor 检查（last_persisted_seq 严格 contiguous）

### 5.3 业务级去重（result/error/cancel via terminal_envelope_id）

```sql
agent_executions.terminal_envelope_id TEXT
```

CAS 已足够；terminal_envelope_id 用于审计。

## 6. WSS 实现细节

（同 v0.4.2）

```
Server: ASP.NET Core WS Gateway + outbox relay（v0.5）
Client SDK: Node.js / Python / Go
限速: 10 msgs/sec per session
帧大小: max_payload_kb = 1024
Artifact 走 S3 预签名
```

## 7. Runtime Gateway 实现

### 7.1 进程模型

（同 v0.4.2）

### 7.2 v0.4.3 新增：dispatch_acked_at 跟踪

```python
async def dispatch_to_agent(execution_id, attempt_no):
    """Runtime → Agent 推 execution.dispatch"""
    execution = await get_execution(execution_id)
    attempt = await get_attempt(execution_id, attempt_no)
    
    # 1. 写 dispatch_sent_at
    await db.update("""
        UPDATE execution_attempts
        SET dispatch_sent_at = NOW()
        WHERE id = $1
    """, attempt.id)
    
    # 2. 推 dispatch
    await runtime.push_to_agent(execution.agent_id, {
        'type': 'execution.dispatch',
        'payload': {
            'execution_id': execution_id,
            'attempt_no': attempt_no,
            'input': execution.input,
            ...
        }
    })
    # 注意：不在这里写 dispatch_acked_at
    # dispatch_acked_at 由 Agent 回 execution.dispatch_ack 时回填（§7）
```

```python
async def on_dispatch_ack(agent_id, ack):
    """
    v0.7：ACK = transport 收据，只做一件事——记下"这条 dispatch 已送达并被接收"。
    不释放 slot、不重路由（业务接受已在 collaboration.decision 阶段完成，
    CR=ACCEPTED 是终态，且一个 CR 只有一个 Execution）。
    """
    if ack.protocol_error:
        # 协议层异常：只记录，不改变任何状态（重试/恢复走 §7.1）
        metrics.incr(f'execution.dispatch_ack.protocol_error.{ack.protocol_error}')
        log.warn('dispatch_ack protocol_error', execution_id=ack.execution_id,
                 attempt_no=ack.attempt_no, err=ack.protocol_error)
        return

    # 幂等：只回填一次，迟到 / 重复 ACK 不覆盖首次时间
    affected = await db.update("""
        UPDATE execution_attempts
        SET dispatch_acked_at = NOW()
        WHERE execution_id = $1 AND attempt_no = $2
          AND dispatch_acked_at IS NULL
    """, ack.execution_id, ack.attempt_no)

    if not affected and not await attempt_exists(ack.execution_id, ack.attempt_no):
        metrics.incr('execution.dispatch_ack.stale')
        log.warn('dispatch_ack for unknown attempt',
                 execution_id=ack.execution_id, attempt_no=ack.attempt_no)
```

```python
async def on_agent_status(agent_id, status, reason=None, since=None):
    """纯 activity 更新：只写 presence / UI 派生，不参与任何协议状态判定"""
    await presence.set(agent_id, activity=status, reason=reason, since=since)
```

### 7.1 ACK 补洞（v0.5 · Runtime / Redis 重启后禁止盲目重派）

| 场景 | 现象 | 处理 |
| --- | --- | --- |
| 正常收到 `execution.dispatch_ack` | 回填 `dispatch_acked_at` | §7 主路径 |
| 一直没收到 ACK（`accepted` 也没有） | `dispatch_acked_at IS NULL` | 记 `execution.dispatch_ack.timeout` 并告警；**不猜 attempt** |
| Agent 已启动，ACK 在网络 / Runtime 重启中丢失 | execution=`RUNNING` 但 `dispatch_acked_at IS NULL` | 重启后先发 `execution.resume_request`，等 `resume_ack` 或 `execution.event`；`ack_wait_s`（默认 15s）内无响应才重派**同一 attempt_no** |
| 重派同一 attempt | Agent 收到重复 dispatch | 客户端**必须按 `(execution_id, attempt_no)` 去重**：已有该 attempt 上下文时改走 resume，不重新起跑 |

要点：重启恢复能安全工作的前提只有一个——**客户端按 `(execution_id, attempt_no)` 幂等**。SDK `on('dispatch')` 首次收到即建 attempt 上下文并立即回 `execution.dispatch_ack`；重复收到同一 `(execution_id, attempt_no)` 时只重新绑定 WS 会话并再次 ACK，不得重复执行。缺了这条，任何重派都会造成重复执行与重复计费。

### 7.2 ACK 语义与 capacity invariant（v0.7 重写）

**ACK 只有一个含义：dispatch 已送达并被接收。** 它不表达业务接受（那是 `collaboration.decision`），因此**没有拒收分支，也不会触发重路由**。

| 情形 | Agent 行为 | Runtime 行为 |
| --- | --- | --- |
| 正常收到 dispatch | 立即回 `dispatch_ack{received:true}`（先按 `(execution_id, attempt_no)` 幂等去重） | 回填 `dispatch_acked_at`；Execution 进入 RUNNING 判定路径 |
| 收到未知 execution / 过期 attempt | 回 `dispatch_ack{received:true, protocol_error:UNKNOWN_EXECUTION/STALE_ATTEMPT}` | 记 `protocol_error` 指标并丢弃，**不改状态、不重派** |
| Agent 崩溃 / 网络断，没来得及回 | 无 ACK | 走 §7.1 的 `ack_wait_s` 超时路径：先 `execution.resume_request`，仍无响应才重派**同一 `(execution_id, attempt_no)`**，超阈值 → `FAILED` + 进 Inbox（`execution.failed` 分类） |
| Agent 本地容量已满 | **不应发生** | 见下 |

**capacity invariant（v0.7 冻结）**：容量在 **Resolver 阶段**已由 lease 预占（`tryAcquireSlot(pending_decision) → ACCEPT → promoteLease(execution)`，SYSTEM_DESIGN §4.2.1）。因此 dispatch 阶段再出现「本地已满」= **invariant 被破坏**，处理方式是：

- 记 `capacity.invariant_violation` 指标 + 告警（这是 bug 信号，不是正常路径）；
- 仍然**不回到 Resolver**：CR 已 `ACCEPTED`（终态），且 `UNIQUE(collaboration_request_id)` 保证一个 CR 只创建一个 Execution；
- 由 E7 按「执行不可达」收尾：重试同一 attempt → 超阈值 `FAILED` + 进 Inbox。

> 结论：`dispatch_ack` 从不需要"换下一个候选"，因为 Resolver 的职责在 ACCEPT 那一刻就结束了。

## 8. Agent SDK 设计

（同 v0.4.2）

```ts
class AgentClient {
  on('cancel', handler)  // 必须实现
  on('resume_ack', handler)  // v0.4.3
  on('dispatch', handler)  // 必须实现：收到即回 sendDispatchAck（v0.5）
  sendDispatchAck(execution_id, attempt_no, accepted = true, reason?)  // v0.5
  sendStatus(activity, reason?)  // v0.5：纯 activity，不再承载 ACK
  sendDecision(...)  // collab.decision
  sendEvent(...)  // execution.event with seq
  sendResult(...)  // execution.result
  sendResumeRequest(...)
}
```

## 9. E2E 验收点

```
e2e/03-ws-resume/
  test_001_hello_auth.json
    Given valid agent_token
    When hello
    Then hello_ack within 100ms

  test_002_heartbeat_liveness.json
    Given connected
    When 90s no heartbeat
    Then activity=OFFLINE broadcast

  test_003_resume_after_reconnect.json
    Given execution in progress, WS dropped
    When agent reconnects + resume_request
    Then resume_ack with last_persisted_seq; agent resends from seq+1; no data loss

  test_004_duplicate_event_idempotent.json
    When agent sends same provider_event_id twice
    Then second is silently dropped (UNIQUE conflict)

  test_005_cancel_aborts_llm.json
    Given execution mid-LLM-call
    When Runtime sends cancel
    Then Agent aborts LLM, sends result=CANCELLED within 1s

  test_006_restart_resumes_inflight.json              # v0.4.3 修复
    Given 3 in-flight executions, dispatch_acked_at all set
    When Runtime restarts
    Then no re-dispatch, all 3 wait for resume

  test_007_restart_redispatch_pending.json             # v0.4.3 新增
    Given 2 in-flight executions, dispatch_acked_at both null
    When Runtime restarts
    Then both re-dispatched

  test_008_seq_gap_detection.json                     # v0.4.3 修复 P1-2
    Given attempt.last_persisted_seq=5
    When agent sends seq=7 (skipped 6)
    Then event rejected, resume_request pushed to agent

  test_009_dispatch_ack_tracking.json                 # v0.4.3 新增
    When Runtime pushes execution.dispatch
    Then dispatch_sent_at is set
    And  when agent pushes status=WORKING
    And  then dispatch_acked_at is set
    And  restart logic uses these fields correctly
```

## 10. 与其他设计的关系

- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（完整时序）
- 详见 [02-interrupt-and-cancel.md](./02-interrupt-and-cancel.md)（Cancel 路径）
- 详见 [08-error-and-retry.md](./08-error-and-retry.md)（错误处理）
