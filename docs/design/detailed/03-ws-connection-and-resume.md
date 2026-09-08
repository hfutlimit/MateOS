# Detailed Design · 03 · WebSocket Connection and Resume

> Agent ↔ Runtime 的 WebSocket 连接生命周期 + v0.4.2 resume 协议。
> 前置：[00-overview.md](./00-overview.md) / [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)

## 0. 范围

- **WS 连接生命周期**：hello / heartbeat / close
- **WSS 协议**：dispatch / status / collaboration.* / execution.*
- **断线 / 重连 / 恢复**：resume_request / resume_ack（v0.4.2 反向协议）
- **多 Agent 复用单连接**（V2+）

## 1. 连接生命周期

```
┌──────────────────────────────────────────────────────────┐
│                                                          │
│   Agent 进程                                              │
│   ┌──────────────┐                                        │
│   │ SDK 启动      │                                        │
│   └──────┬───────┘                                        │
│          │ TCP/TLS                                        │
│          ▼                                                │
│   ┌──────────────┐  WS  ┌────────────────────────────┐   │
│   │ wss://api/... │ ◄──► │ Runtime Gateway            │   │
│   │ (agent 端)   │      │ (server 端)                 │   │
│   └──────────────┘      │                              │   │
│          │               │ 1. hello 校验                │   │
│          │               │ 2. 注册 runtime_session_id   │   │
│          │               │ 3. 鉴权 agent_token          │   │
│          │               └────────────────────────────┘   │
│          ▼                                                  │
│   ┌──────────────┐                                        │
│   │ ready 状态    │  ← push 任何 envelope                  │
│   └──────┬───────┘                                        │
│          │                                                │
│          ▼                                                │
│   ┌──────────────┐                                        │
│   │ 心跳 30s     │  ← agent 推 heartbeat                  │
│   └──────┬───────┘     server 推 ping（如果需要）         │
│          │                                                │
│          ▼                                                │
│   ┌──────────────┐                                        │
│   │ close 帧     │  ← 正常退出 OR 进程崩溃                 │
│   └──────────────┘                                        │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

## 2. Hello 协议

### 2.1 Agent → Runtime

```jsonc
{ "type": "hello", "id": "uuid-1", "ts": ..., "payload": {
    "agent_id": "...",
    "agent_token": "jwt-v1...",          // 短期（V1: 30d）
    "runtime_version": "1.0.0",
    "runtime_session_id": "sess-...",    // V1: 每次 hello 重新生成 UUID
    "capabilities": ["coding", "debugging", "review"]
}}
```

### 2.2 Runtime → Agent

```jsonc
{ "type": "hello_ack", "id": "uuid-2", "ts": ..., "payload": {
    "session_id": "sess-...",            // Runtime 端 session id
    "server_version": "1.0.0",
    "config": {
        "heartbeat_interval_s": 30,
        "max_idle_s": 90,
        "max_payload_kb": 1024
    }
}}
```

### 2.3 校验失败

```jsonc
{ "type": "hello_nack", "id": "uuid-2", "ts": ..., "payload": {
    "code": "INVALID_TOKEN|UNKNOWN_AGENT|TOKEN_EXPIRED",
    "message": "..."
}}
```

→ Runtime 主动 close（code=4001）

## 3. Heartbeat

### 3.1 协议

```jsonc
{ "type": "heartbeat", "id": "uuid-hb-1", "ts": ..., "payload": {} }
```

双向（agent 推 / server 推），任一即可。V1 简化为 agent 单向推。

### 3.2 间隔

- `heartbeat_interval_s = 30`（来自 hello_ack.config）
- `max_idle_s = 90`（server 容忍 3 倍间隔才断）
- 实际：server 收心跳 → 更新 `agent_tokens.last_seen_at`

### 3.3 超时

```
Background worker 每 30s 扫：
  for each agent where active executions exist OR lifecycle='ACTIVE':
    if last_seen_at < now - 90s:
      - DB: agents.activity = 'OFFLINE'
      - Redis: presence {status: OFFLINE, last_heartbeat: now-90s}
      - WS 推 agent.activity_changed
      - 触发"长时断线"路径（详见 02-interrupt-and-cancel.md §2.5）
```

## 4. Envelope 协议（v0.4.2）

```jsonc
// 所有 envelope 必填 id（去重键）
{ "v": 1, "type": "<type>", "id": "<uuid>", "ts": <epoch_ms>, "payload": { ... } }
```

### 4.1 类型总览

| 方向 | type | 拥有方 | 说明 |
| --- | --- | --- | --- |
| 双向 | `hello` / `hello_ack` / `hello_nack` / `heartbeat` | 共同 | 连接管理 |
| Runtime → Agent | `dispatch` | E7 | 任务下发 |
| Runtime → Agent | `cancel` | E7 | 取消执行 |
| Agent → Runtime | `status` | E7（fact 写 agents.activity） | 6 态上报 |
| Agent → Runtime | `collaboration.decision` | E4 | 决策（v0.4.1 与 execution 拆开） |
| Agent → Runtime | `event` | E7 | 流式事件 |
| Agent → Runtime | `result` | E7 | 终态（不携带 decision） |
| Agent → Runtime | `error` | E7 | 错误 |
| Agent → Runtime | `resume_request` | E7（v0.4.2 反向） | 询问持久化位点 |
| Runtime → Agent | `resume_ack` | E7（v0.4.2 反向） | 回答持久化位点 + dispatch snapshot |

### 4.2 完整 message type

```jsonc
// ─── 连接管理 ───
{ "type": "hello",        "payload": { "agent_id", "agent_token", "runtime_session_id", "runtime_version", "capabilities" }}
{ "type": "hello_ack",    "payload": { "session_id", "server_version", "config": { heartbeat_interval_s, max_idle_s, max_payload_kb } }}
{ "type": "hello_nack",   "payload": { "code", "message" }}
{ "type": "heartbeat",    "payload": {} }

// ─── E7 派发 ───
{ "type": "dispatch",     "payload": {
    "execution_id", "collaboration_request_id"?, "work_item_ref"?,
    "input": { "prompt", "params" },
    "context": { "memory_refs", "recent_messages", "permissions" },
    "deadline_s", "idempotency_key"
}}

{ "type": "cancel",       "payload": {
    "execution_id", "attempt_no",
    "reason": "USER_CANCEL|LIFECYCLE_PAUSED|LIFECYCLE_DISABLED|DEADLINE_EXCEEDED|TIMEOUT_NO_RESUME"
}}

// ─── Agent 上报 ───
{ "type": "status",       "payload": {
    "status": "OFFLINE|AVAILABLE|THINKING|WORKING|WAITING_CONTEXT|ERROR",
    "reason"?, "since"
}}

{ "type": "collaboration.decision", "payload": {
    "collaboration_request_id",
    "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason"?, "needs"?, "analysis": { "capability", "context_score", "permission" }
}}

{ "type": "event",        "payload": {
    "execution_id", "attempt_no",
    "event_type": "STDOUT|PROGRESS|TOOL_CALL|LLM_TICK|ARTIFACT|ERROR",
    "provider_event_id", "seq",
    "payload": { "content", "meta" }
}}

{ "type": "result",       "payload": {
    "execution_id", "attempt_no",
    "status": "SUCCEEDED|FAILED|CANCELLED",
    "output": { "markdown", "code" },
    "usage": { "tokens_in", "tokens_out", "duration_ms" },
    "artifacts": [ { "kind", "name", "s3_key" } ]
}}

{ "type": "error",        "payload": {
    "execution_id", "attempt_no",
    "code": "PROVIDER_5XX|PROVIDER_401|RATE_LIMIT|SANDBOX_INIT_FAILED|DEADLINE_EXCEEDED",
    "message", "retry_after_s"?
}}

// ─── v0.4.2 反向 resume 协议 ───
{ "type": "resume_request", "payload": {
    "execution_id", "attempt_no"
}}

{ "type": "resume_ack",     "payload": {
    "execution_id", "attempt_no",
    "last_persisted_seq",     // 0 表示从 dispatch snapshot 重发
    "snapshot": {              // 重发 dispatch 内容（input + context）
        "execution_id", "input", "context", "deadline_s"
    }
}}
```

## 5. Resume 协议（v0.4.2 反向）

### 5.1 为什么反向

**v0.4.1 旧设计**（错的）：

```
Agent → resume { last_event_seq: 42 }   // Agent 告诉 Runtime 它收到 42
Runtime → 补发 seq 43+                  // Runtime 把 Agent 自己的 event 重发给 Agent
```

**逻辑错误**：
- event 是 Agent **产生**的（STDOUT、TOOL_CALL、LLM_TICK 都是 Agent 端的输出）
- Runtime 没产 event，Runtime 补发是错的
- Agent 收到自己刚发的事件会造成循环/重复

**v0.4.2 新设计**（反向）：

```
Agent → resume_request { execution_id, attempt_no }   // Agent 问：我应该从哪个 seq 开始？
Runtime → resume_ack { last_persisted_seq: 42, snapshot }  // Runtime 答：你从 seq 43 开始
Agent → 从 seq 43 续发 event                              // Agent 主动重发自己产的内容
```

**关键**：
- Runtime 持久化 event 后只告知位点
- Agent 自己续发（不是 Runtime 补发）
- `last_persisted_seq` 是 Runtime 端 max(seq) WHERE attempt_id=?
- `last_persisted_seq=0` 表示 Runtime 还没收到任何 event（极端情况：Agent 发了 hello 后就断）

### 5.2 完整时序

```
T0:  Agent 正常执行
T1:  WS 断开（Agent 端）
T2:  Runtime 检测 close frame:
     - 标记 in_flight=true
     - 不取消（等 resume）
T3:  Agent 重连，hello（新 runtime_session_id）
T4:  Runtime:
     a) 查 in-flight executions for this agent
     b) 对每个：UPDATE execution_attempts SET runtime_session_id=new_session_id
T5:  Agent 主动推 resume_request { execution_id, attempt_no: 1 }
T6:  Runtime:
     a) 查 attempt.last_persisted_seq = MAX(execution_events.seq) WHERE attempt_id=?
     b) 取 dispatch snapshot（input + context + permissions）
     c) 返回 resume_ack { last_persisted_seq, snapshot }
T7:  Agent 从 seq=last_persisted_seq+1 开始续发 event
T8:  Runtime 收到 event：
     a) UNIQUE(attempt_id, provider_event_id) 检查
     b) UNIQUE(attempt_id, seq) 检查
     c) 都通过 → 落 execution_events
```

### 5.3 边界情况

| 场景 | 行为 |
| --- | --- |
| `last_persisted_seq=0` | Agent 重新完整执行（不发 event，直接推 result 或重新跑） |
| Agent 重发 event with same provider_event_id | UNIQUE 冲突 → ignore（不抛错） |
| Agent 重发 event with new provider_event_id + same seq | UNIQUE(attempt_id, seq) 冲突 → 拒绝（agent bug） |
| Agent 重发 result with same envelope.id | terminal_envelope_id 已存在 → ignore |
| Agent 重发 result with new envelope.id + status='SUCCEEDED' | CAS 失败（已是 SUCCEEDED）→ ignore |
| 长时断线（> 5 分钟）| Runtime 主动 cancel（详见 02 §2.5） |

## 6. Idempotency 三层保护

### 6.1 协议级去重（envelope.id）

- 所有 envelope 必填 `id`（UUIDv4）
- Runtime 记录最近 1 小时 envelope.id 集合（Redis Set，TTL 1h）
- 重复 envelope.id 立即忽略
- DB 落 `terminal_envelope_id` 用于永久去重（result/error/cancel）

### 6.2 业务级去重（event provider_event_id）

```
DB UNIQUE: execution_events (attempt_id, provider_event_id)
  - Agent 推 same provider_event_id → 冲突 → 忽略
  - 用于 crash recovery / WS 重传 / 多 source 重复
```

### 6.3 业务级去重（event seq）

```
DB UNIQUE: execution_events (attempt_id, seq)
  - 单 attempt 内 seq 单调
  - 同 attempt 内重复 seq → 拒绝（agent bug）
```

### 6.4 业务级去重（result/error/cancel via terminal_envelope_id）

```sql
-- 表 schema
agent_executions.terminal_envelope_id TEXT

-- 写入 result 时
UPDATE agent_executions
SET status = $1, ..., terminal_envelope_id = $2
WHERE id = $3 AND status IN ('PENDING', 'RUNNING')
  AND (terminal_envelope_id IS NULL OR terminal_envelope_id = $2)
```

实际上 CAS 已经够用（status 已是终态后再次 UPDATE 不影响行数）。`terminal_envelope_id` 字段更多是审计目的。

## 7. WSS 实现细节

### 7.1 库

- **Server**: NestJS Gateway + `ws` package
- **Client**: V1 各语言官方 SDK（先出 Node.js / Python / Go）
- **V2 计划**: 支持多 Agent 复用单 connection（`agent_ids: [...]` 在 hello）

### 7.2 限速

```
WS 推 / 推限速（per session）：
  - 10 msgs / sec （防滥用）
  - 超过 → 推 backpressure 帧
  - 客户端必须等待 ack 才能推下一批

事件推送（server → agent）：
  - 实时 push（低延迟）
  - 高频事件（LLM_TICK）可降采样（每 100ms 聚合）
```

### 7.3 帧大小

```
max_payload_kb = 1024 (1MB)
超限 → 拆帧 OR reject（V1 reject，V2 拆帧）
Artifact 走 S3 预签名，不走 WS payload
```

## 8. Runtime Gateway 实现

### 8.1 进程模型

```
Runtime Gateway (Node.js / NestJS)
  ├─ WebSocketAdapter (ws)
  ├─ SessionManager (in-memory map: session_id → Connection)
  ├─ Dispatcher (per execution_id queue)
  ├─ EventRouter (per agent_id queue)
  └─ HealthCheck (heartbeat sweep)
```

### 8.2 启动流程

```
1. 加载配置 (env: REDIS_URL, PG_URL, JWT_PUBLIC_KEY)
2. PG 迁移检查
3. 加载 in-flight executions:
   - SELECT * FROM agent_executions WHERE status IN ('PENDING','RUNNING')
4. 对每个 in-flight:
   - 重新 dispatch（带 idempotency_key）
   - 如果 agent_token 过期 → 标 FAILED + 通知 owner
5. Heartbeat sweep 启动
6. 监听 WS 端口
```

### 8.3 优雅关闭

```
SIGTERM 收到：
  1. 停止接受新 WS 连接
  2. 等待在飞 WS 帧 flush
  3. 关闭所有 WS
  4. 关闭 DB / Redis 连接
  5. 退出
  6. K8s readiness 探针立即失败 → 流量切换
```

**V1 简化**：不做"in-flight dispatch 持久化"（靠 DB 重建）。

## 9. Agent SDK 设计（V1）

### 9.1 Node.js SDK

```ts
class AgentClient {
  constructor(opts: { agentId: string, agentToken: string, baseUrl: string })

  // 连接
  async connect(): Promise<void>  // hello + hello_ack
  async disconnect(): Promise<void>  // close

  // 监听
  on(type: 'dispatch' | 'cancel' | 'resume_ack', handler)

  // 推送
  async sendStatus(status, reason?)
  async sendDecision(decision, opts)
  async sendEvent(event_type, payload, opts)
  async sendResult(result, opts)
  async sendError(code, message, opts)
  async sendResumeRequest(execution_id, attempt_no)

  // 内部
  startHeartbeat()
  handleCancel(cancelEnvelope)  // abort LLM, cleanup, send result=CANCELLED
  handleResumeAck(ackEnvelope)  // 续发 event from last_persisted_seq + 1
}
```

### 9.2 必须实现的 cancel handler

```ts
// SDK 强制要求（V1 是 convention，V2 是 spec）
client.on('cancel', async (cancel) => {
  // 1. 立即 abort 所有 in-flight LLM 调用
  if (currentLLMRequest?.abortController) {
    currentLLMRequest.abortController.abort();
  }
  // 2. 清理 sandbox
  await cleanupSandbox();
  // 3. 推 result=CANCELLED
  await client.sendResult({
    execution_id: cancel.payload.execution_id,
    attempt_no: cancel.payload.attempt_no,
    status: 'CANCELLED',
    output: { summary: 'cancelled by ' + cancel.payload.reason }
  });
});
```

**这是 Agent 端的核心责任**。不实现 cancel handler = 不响应打断 = 用户体验差。

## 10. E2E 验收点

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

  test_006_restart_resumes_inflight.json
    Given 3 in-flight executions
    When Runtime restarts
    Then all 3 re-dispatched, no duplicate result
```

## 11. 与其他设计的关系

- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（完整时序）
- 详见 [02-interrupt-and-cancel.md](./02-interrupt-and-cancel.md)（Cancel 路径）
- 详见 [08-error-and-retry.md](./08-error-and-retry.md)（错误处理）
