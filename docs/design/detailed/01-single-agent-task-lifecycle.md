# Detailed Design · 01 · Single Agent Task Lifecycle

> **这是用户点名的重点**：单个 Agent 接受任务 → 处理 → 回复一整套。
> 前置：[00-overview.md](./00-overview.md) / [04-resolver-and-routing.md](./04-resolver-and-routing.md) / [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)
> 中断场景：[02-interrupt-and-cancel.md](./02-interrupt-and-cancel.md)

## 0. 范围

本文档描述从"用户发消息"到"Agent 输出回到消息流"的**完整端到端流程**，单 Agent 视角。

- **不**覆盖：多 Agent 协作（V2 Delegate）、Sandbox 执行（V3）
- **覆盖**：单 Agent 接收 dispatch → THINKING → ACCEPT → WORKING → events → result → 消息流投影

## 1. 完整时序图

```
T+0     User 在 P5 Channel 发消息 "@Backend Agent 帮我看看这段代码"
T+1     P5 客户端 POST /channels/:id/messages { text: "...@Backend Agent..." }
T+2     E3 Channel Module 事务：
          - 写 messages (content_type=HUMAN, mentions=[{raw:'@Backend Agent', type:AGENT}])
          - 写 triggers (trigger_type='MENTION', ref={channel_id, message_seq})
          - 同步写 collaboration_requests (status=PENDING, target_agent_id=NULL, required_capabilities=[])
T+3     E3 WS 广播 message.created 给 channel 在线 member
T+4     客户端渲染：消息泡 + mention 胶囊占位「⏳ 解析中」
T+5     E4 Orchestrator 异步 Resolver 启动（BullMQ mention.resolve）
T+6     Resolver 跑：
          a) 硬过滤：agent.lifecycle=ACTIVE ∩ channel member ∩ permission ALLOW ∩ Redis tryAcquireSlot(agent_id, lease_id) 成功
          b) Capability ranking: 0.6 * coding_match + 0.25 * (1 - load) + 0.15 * accept_rate_30d
          c) 选 Backend Agent (top-1)
          d) 写 collaboration_requests.target_agent_id + slot_lease_id
T+7     E4 → E7 内部 API: POST /internal/agent-executions
          { collaboration_request_id, input, context_refs }
T+8     E7 Runtime Gateway:
          - 查 agents.lifecycle = 'ACTIVE' (double check)
          - INSERT agent_executions (status=PENDING)
          - INSERT execution_attempts (attempt_no=1, status=STARTED, runtime_session_id=null)
          - UPDATE agent_executions SET active_attempt_no=1
          - active_slots++ (Redis, 已在 Resolver 步骤占用)
          - 通过 WS push execution.dispatch 到 Agent
T+9     Agent 收到 dispatch:
          - 校验 execution_id / idempotency_key
          - 推 status envelope: lifecycle=ACTIVE, activity=THINKING
T+10    E7 收到 status envelope → Redis presence 更新 + DB agents.activity='THINKING'
T+11    E7 WS 推 agent.activity_changed 给所有相关 client（前端更新状态点紫+呼吸）
T+12    Agent 决定:
          a) 校验 capability / context_score / permission 三件套
          b) 推 execution.event { event_type: 'PROGRESS', content: '正在分析代码...' }
T+13    E7 写 execution_events (attempt_id, provider_event_id, seq=1)
T+14    Agent 决定 ACCEPT → 推 collaboration.decision { decision: ACCEPT, analysis: {...} }
T+15    E4 收到 collaboration.decision:
          - 写 decision_records
          - UPDATE collaboration_requests SET status='ACCEPTED', resolved_at=now()
          - WS 推 collaboration.resolved (给发起人 channel 客户端)
          - E4 → E7: 转交 decision 上下文
T+16    E7 写 decision_records（事实源）
T+17    Agent 推 status envelope: activity=WORKING
T+18    E7 更新 agents.activity='WORKING', execution_attempts.status='RUNNING', agent_executions.status='RUNNING', started_at=now()
T+19    Agent 调用 LLM:
          - 推 execution.event { event_type: 'LLM_TICK', payload: {tokens_in: 1500, tokens_out: 300} }
          - 推 execution.event { event_type: 'TOOL_CALL', payload: {tool: 'read_file', args: {...}} }
T+20    Agent 写出代码:
          - 推 execution.event { event_type: 'ARTIFACT', payload: {kind: 'FILE', name: 'patch.diff', s3_key: '...'} }
          - 服务端写 execution_artifacts + 上传 S3
T+21    Agent 推 execution.result { status: 'SUCCEEDED', output: {markdown: '...', code: '...'}, usage: {...}, artifacts: [...] }
T+22    E7 收到 result:
          - CAS UPDATE: SET status='SUCCEEDED', completed_at=now(), terminal_envelope_id=...
            WHERE id=? AND status IN ('PENDING','RUNNING')
          - affected_rows = 1 → 真正完成
          - active_attempt_no=NULL
          - 写 execution_attempts.status='COMPLETED', completed_at=now()
          - active_slots-- (Redis release)
          - 写 audit
          - 写 llm_calls
T+23    Agent 推 status envelope: activity=AVAILABLE
T+24    E7 更新 agents.activity='AVAILABLE'
T+25    E4 / E3 联动: 消息流写 AGENT_OUTPUT 投影消息:
          - content_type='AGENT_OUTPUT'
          - content.execution_ref=execution.id
          - content.artifact_id=<S3 ref>
T+26    E3 写 messages + seq 分配
T+27    E3 WS 广播 message.created 给 channel 在线 member
T+28    客户端渲染: 决策卡（v0.4.1 projection）+ Agent 输出卡
```

## 2. Agent 内部状态机（v0.4.2）

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
- active_slots 是 Redis Lua atomic 持有（v0.4.2 修复 race）
- lifecycle 由 owner REST 控制，与 activity 解耦

## 3. WS 协议（v0.4.2）

### 3.1 Envelope

```jsonc
// 通用 envelope（v0.4.2 id 必填，用于去重）
{ "v": 1, "type": "<type>", "id": "<uuid>", "ts": <epoch_ms>, "payload": {} }
```

### 3.2 E4 → E7 内部 API（不是 WS，是同步 HTTP）

```http
POST /internal/agent-executions
Headers:
  X-Internal-Token: <service token>
  X-Idempotency-Key: <collab_request_id>
Body:
{
  "collaboration_request_id": "...",
  "work_item_ref": null | { "provider_key": "builtin", "work_item_id": "..." },
  "input": {
    "prompt": "...",
    "params": {},
    "original_message": { "channel_id": "...", "message_seq": 42 }
  },
  "context_refs": {
    "channel_id": "...",
    "memory_refs": ["mem:..."],
    "recent_messages": ["msg:..."],
    "permissions": { "can_execute": false, "can_review": false }
  },
  "deadline_s": 600
}
```

**为什么内部 API 不是 WS**：E4 Orchestrator 是同步调用 E7 创建 Execution，不经过 Agent。Agent 收到 dispatch 是另一回事（通过 WS）。

### 3.3 Runtime Gateway → Agent（WS push）

```jsonc
{ "type": "dispatch", "id": "uuid-1", "ts": 1736380900000, "payload": {
    "execution_id": "...",
    "collaboration_request_id": "...",
    "work_item_ref": null,
    "input": { "prompt": "...", "params": {} },
    "context": { "memory_refs": [], "recent_messages": [], "permissions": {} },
    "deadline_s": 600,
    "idempotency_key": "..."
}}
```

### 3.4 Agent → Runtime（WS push）

```jsonc
// 状态上报
{ "type": "status", "id": "uuid-2", "ts": ..., "payload": {
    "status": "THINKING|WORKING|WAITING_CONTEXT|AVAILABLE|OFFLINE|ERROR",
    "reason": "rate_limit_exceeded",  // ERROR 时
    "since": 1736380900
}}

// 决策（v0.4.1 与 execution 拆开）
{ "type": "collaboration.decision", "id": "uuid-3", "ts": ..., "payload": {
    "collaboration_request_id": "...",
    "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason": "...",
    "needs": ["api spec"],
    "analysis": { "capability": true, "context_score": 88, "permission": true }
}}

// 流式事件
{ "type": "event", "id": "uuid-4", "ts": ..., "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "event_type": "STDOUT|PROGRESS|TOOL_CALL|LLM_TICK|ARTIFACT|ERROR",
    "provider_event_id": "evt-uuid-4",   // v0.4.1 协议级幂等键
    "seq": 1,
    "payload": { "content": "..." }
}}

// 最终结果（v0.4.1 不携带 decision）
{ "type": "result", "id": "uuid-5", "ts": ..., "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "status": "SUCCEEDED|FAILED|CANCELLED",
    "output": { "markdown": "...", "code": "..." },
    "usage": { "tokens_in": 1500, "tokens_out": 300, "duration_ms": 6200 },
    "artifacts": [ { "kind": "FILE", "name": "patch.diff", "s3_key": "..." } ]
}}

// 错误
{ "type": "error", "id": "uuid-6", "ts": ..., "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "code": "PROVIDER_5XX|PROVIDER_401|RATE_LIMIT|DEADLINE_EXCEEDED",
    "message": "...",
    "retry_after_s": 60
}}
```

### 3.5 关键 idempotency 规则

| 消息 | 去重键 | DB 唯一约束 | 行为 |
| --- | --- | --- | --- |
| `event` | `provider_event_id` | `UNIQUE(attempt_id, provider_event_id)` | 重复忽略，不抛错 |
| `result` / `error` | envelope `id` | `agent_executions.terminal_envelope_id` 字段 | 重复忽略 |
| `dispatch` | `idempotency_key` (Runtime 侧生成) | Runtime 内部记录 | 重复忽略 |

## 4. 数据流时序

### 4.1 E7 收到 `result` envelope 的处理

```python
async def handle_result(envelope):
    execution_id = envelope.payload.execution_id
    envelope_id = envelope.id

    # 1. CAS 状态转换（核心幂等）
    affected = await db.execute("""
        UPDATE agent_executions
        SET status = $1,
            completed_at = NOW(),
            active_attempt_no = NULL,
            terminal_envelope_id = $2
        WHERE id = $3
          AND status IN ('PENDING', 'RUNNING')
        RETURNING agent_id, attempt_count
    """, envelope.payload.status, envelope_id, execution_id)

    if not affected:
        # 重复或 stale（已 terminal）
        log.info(f"Duplicate/stale result for {execution_id}, envelope={envelope_id}")
        return  # 不抛错

    row = affected[0]
    agent_id = row.agent_id

    # 2. 写 audit
    await audit_log('execution.completed', execution_id, envelope.payload.status)

    # 3. 释放 slot（E4 持有的 lease，但 v0.4.2 用 tryAcquireSlot 的 lease_id 释放）
    #    注意：slot 在 Resolver 阶段 reservation，由 E4 持有 lease_id
    #    Execution 完成时 E4 收到 WS 推 → E4 releaseSlot
    #    这里 E7 只更新 DB 状态
    await emit('execution.completed', execution_id)  # WS fanout

    # 4. 写 llm_calls
    if envelope.payload.usage:
        await db.execute("""
            INSERT INTO llm_calls (agent_id, execution_id, model, tokens_in, tokens_out, duration_ms, cost_usd)
            VALUES (...)
        """)

    # 5. Activity 恢复（异步事件触发，Agent 推 status=AVAILABLE 才是真信号）
    #    这里不主动写 agents.activity
```

### 4.2 E4 收到 `result` 后的消息流投影

```python
async def on_execution_completed(execution_id):
    execution = await get_execution(execution_id)
    collab = await get_collaboration_request(execution.collaboration_request_id)

    if collab.context_refs.channel_id:
        # 写 AGENT_OUTPUT 投影消息
        await db.execute("""
            INSERT INTO messages (channel_id, seq, sender_type, sender_id, content_type, content, mentions)
            VALUES ($1, next_seq($1), 'AGENT', $2, 'AGENT_OUTPUT', $3, '[]')
        """, collab.context_refs.channel_id, execution.agent_id, {
            "execution_ref": execution.id,
            "artifact_ids": [...]  # 从 execution_artifacts 关联
        })

        # 写 DECISION 投影消息（如果是 decision-driven）
        # 注：collab.decision 已写过 DECISION 投影
        # Execution 完成是状态变更，不重复写

        # WS 广播
        await ws_broadcast(channel_id, 'message.created', {...})
```

## 5. 时序约束（关键 SLA）

| 步骤 | P95 目标 | 关键路径 |
| --- | --- | --- |
| 消息 → 写 triggers + collab_request | < 50ms | PG 写 |
| Resolver 选 Agent | < 500ms | Redis Lua atomic + capability 算分 |
| E4 → E7 API | < 100ms | 内部 HTTP |
| E7 dispatch 到 Agent | < 200ms | WS push |
| Agent 推 status THINKING | < 100ms | WS 反向 |
| Agent 推 collaboration.decision | < 1s | 业务逻辑（Capability 校验） |
| Agent 推 result | 由 Agent 决定 | LLM 调用主导 |
| E7 CAS + 消息流投影 | < 200ms | PG CAS + WS broadcast |
| **端到端（用户发消息 → 看到结果）** | 由 LLM 决定 | 大头在 LLM latency |

## 6. 关键不变量

1. **execution_id 唯一**：每个 E4 → E7 API 调用创建一个新 execution_id
2. **attempt_no 单调**：每个 execution 的 attempt_no 从 1 开始，严格 +1
3. **provider_event_id 单 attempt 内唯一**：UNIQUE(attempt_id, provider_event_id) DB 强制
4. **envelope.id 全局唯一**：Runtime 给每个 envelope 分配 UUID，DB 落 `terminal_envelope_id` 去重
5. **CAS 状态转换**：任何终态变更都带 `WHERE status IN ('PENDING','RUNNING')`
6. **active_slots 严格守恒**：`reservation_count - release_count = agents.active_slots`（Redis 持有）

## 7. 失败处理

| 失败点 | 行为 | 重试 |
| --- | --- | --- |
| E4 → E7 内部 API 失败 | 写 collab_request.status=UNRESOLVED + 通知发起人 | 业务层决定 |
| E7 dispatch 失败（Agent 离线） | PENDING 60s 超时 → 取下一个候选 | Resolver 路径 |
| Agent 推 result 失败（WS 断） | 落 `agent_executions.status='RUNNING'`，等 Agent 重连 | resume_request 协议 |
| Agent 推 status 失败 | 落 `agents.activity=OFFLINE`（90s 后由 heartbeat 扫） | 重新 dispatch |
| E7 CAS 失败（status 已是终态） | 忽略，audit 记 "stale_terminal" | 不会重复释放 slot |

## 8. E2E 验收点

```
e2e/01-single-agent-lifecycle/
  test_001_message_mention_to_result.json
    Given Agent B 在 #webhook-retry 频道
    When User 发 "@Backend Agent 看下重试逻辑"
    Then T+200ms 看到 DECISION 投影（ACCEPT）
    And  T+1-30s 看到 AGENT_OUTPUT 投影（与 LLM 同步）
    And  Agent activity 依次：AVAILABLE → THINKING → WORKING → AVAILABLE
    And  agent_executions.status 最终为 SUCCEEDED
    And  active_slots 恢复为 0

  test_002_reject.json
    Given Agent B 离线
    When User 发 "@Backend Agent 看下"
    Then 60s 后 status=UNRESOLVED + 通知

  test_003_idempotency.json
    When 重复推同一个 result envelope.id
    Then DB 状态不变，第二次 ignored

  test_004_cas_protection.json
    When cancel + result 同时到
    Then 只一个 CAS 成功（affected_rows=1），另一个 ignored
```

## 9. 与其他设计的关系

- 详见 [02-interrupt-and-cancel.md](./02-interrupt-and-cancel.md)（Cancel 路径）
- 详见 [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)（WS 协议细节）
- 详见 [04-resolver-and-routing.md](./04-resolver-and-routing.md)（Resolver + Slot）
- 详见 [08-error-and-retry.md](./08-error-and-retry.md)（ERROR 触发 + 重试）
