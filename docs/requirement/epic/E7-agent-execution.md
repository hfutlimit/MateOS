# E7 · Agent Execution

| 字段 | 值 |
| --- | --- |
| Epic ID | E7 |
| 标题 | Agent Execution |
| 阶段 | MVP（M8） |
| 上游 | PRD v0.4 / SYSTEM_DESIGN v0.3.2 / v0.4.1 协议边界 / v0.4.2 幂等收口 |
| 下游 | E4（Accept → 调 E7 API 创建 Execution）、E8（work_item_ref optional）、E10 |
| 状态 | Draft（v0.4.2 幂等收口） |

## 1. 背景与动机

v0.4.1 完成 E4/E7 协议边界。**v0.4.2 关键收口**：

1. **`execution.resume` 协议反向**——Agent 主动 `resume_request` 问 Runtime 持久化位点；Runtime 答 `last_persisted_seq`；Agent 续发。**不是** Agent 推 `last_event_seq` 让 Runtime 补发（Runtime 没产 event，event 是 Agent 自己的）
2. **Terminal result 幂等保护**——compare-and-set 状态转换；envelope `id` 协议级去重
3. **`execution_events.attempt_id` 改为 NOT NULL**——`UNIQUE(attempt_id, ...)` 在 NULL 下不触发唯一约束
4. **execution_artifacts.attempt_id 同样 NOT NULL**

## 2. 范围

### 2.1 In Scope

- `agent_executions` 事实源
- `execution_attempts`（logical attempt）
- `execution_events`（attempt_id NOT NULL + provider_event_id 幂等键）
- `execution_artifacts`（attempt_id NOT NULL）
- 协议：`execution.dispatch` / `execution.event` / `execution.result` / `execution.error` / **v0.4.2 改** `execution.resume_request` / `execution.resume_ack`
- E4 → E7 API
- Runtime Gateway
- **v0.4.2 改** Terminal CAS 状态转换
- **v0.4.2 改** 协议级 envelope `id` 用于 result/error 幂等
- Cancel 路径
- 消息流 AGENT_OUTPUT 投影

## 3. 数据模型

```sql
CREATE TABLE agent_executions (
  id                        UUID PRIMARY KEY,
  collaboration_request_id  UUID REFERENCES collaboration_requests(id),
  work_item_ref             JSONB,
  agent_id                  UUID NOT NULL REFERENCES agents(id),
  status                    TEXT NOT NULL DEFAULT 'PENDING'
                            CHECK (status IN ('PENDING','RUNNING','SUCCEEDED','FAILED','CANCELLED','TIMEOUT')),
  -- v0.4.2 新增：协议级 envelope id 去重（terminal result / cancel 用）
  terminal_envelope_id      TEXT,                        -- 最近一次 result/error 的 envelope.id
  input                     JSONB NOT NULL,
  context_refs              JSONB NOT NULL DEFAULT '{}',
  attempt_count             INT NOT NULL DEFAULT 0,
  active_attempt_no         INT,
  started_at                TIMESTAMPTZ,
  completed_at              TIMESTAMPTZ,
  failure_code              TEXT,
  failure_message           TEXT,
  created_at                TIMESTAMPTZ DEFAULT now(),
  updated_at                TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE execution_attempts (
  id                  UUID PRIMARY KEY,
  execution_id        UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_no          INT NOT NULL,
  runtime_session_id  TEXT,
  status              TEXT NOT NULL
                      CHECK (status IN ('STARTED','RUNNING','COMPLETED','FAILED')),
  started_at          TIMESTAMPTZ,
  completed_at        TIMESTAMPTZ,
  error               TEXT,
  UNIQUE (execution_id, attempt_no)
);

-- v0.4.2 改：attempt_id NOT NULL（UNIQUE 在 NULL 下不触发）
CREATE TABLE execution_events (
  id                BIGSERIAL PRIMARY KEY,
  execution_id      UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_id        UUID NOT NULL REFERENCES execution_attempts(id),   -- v0.4.2 NOT NULL
  event_type        TEXT NOT NULL
                    CHECK (event_type IN ('STDOUT','PROGRESS','TOOL_CALL','LLM_TICK','ARTIFACT','ERROR')),
  provider_event_id TEXT NOT NULL,
  seq               BIGINT NOT NULL,
  payload           JSONB NOT NULL,
  trace_id          TEXT,
  created_at        TIMESTAMPTZ DEFAULT now(),
  UNIQUE (attempt_id, provider_event_id),
  UNIQUE (attempt_id, seq)
);

-- v0.4.2 改：attempt_id NOT NULL
CREATE TABLE execution_artifacts (
  id            UUID PRIMARY KEY,
  execution_id  UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_id    UUID NOT NULL REFERENCES execution_attempts(id),       -- v0.4.2 NOT NULL
  kind          TEXT NOT NULL CHECK (kind IN ('FILE','DIFF','LOG','SCREENSHOT')),
  name          TEXT NOT NULL,
  s3_key        TEXT NOT NULL,
  size          BIGINT,
  mime          TEXT,
  created_at    TIMESTAMPTZ DEFAULT now()
);
```

## 4. 协议（v0.4.2 resume 反向 + terminal 幂等）

```jsonc
// 通用 envelope（v0.4.2 新增：id 必填，terminal message 用于去重）
{ "v": 1, "type": "<type>", "id": "uuid", "ts": 0, "payload": {} }

// hello / heartbeat / status
{ "type": "hello", "payload": { "agent_id": "...", "agent_token": "...", "runtime_version": "1.0.0" }}
{ "type": "heartbeat", "payload": {} }
{ "type": "status", "payload": { "status": "...", "reason": "...", "since": 0 }}

// dispatch
{ "type": "dispatch", "payload": {
    "execution_id": "...",
    "collaboration_request_id": "..."?,
    "work_item_ref": {...}?,
    "input": { "prompt": "...", "params": {} },
    "context": { "memory_refs": [], "recent_messages": [], "permissions": {} },
    "deadline_s": 600, "idempotency_key": "..."
}}

// event
{ "type": "event", "payload": {
    "execution_id": "...", "attempt_no": 1,
    "event_type": "STDOUT|PROGRESS|TOOL_CALL|LLM_TICK|ARTIFACT|ERROR",
    "provider_event_id": "evt-uuid-123",
    "seq": 42,
    "payload": { "content": "...", "meta": {} }
}}

// result
{ "type": "result", "payload": {
    "execution_id": "...", "attempt_no": 1,
    "status": "SUCCEEDED|FAILED|CANCELLED",
    "output": { "markdown": "...", "code": "..." },
    "usage": { "tokens_in": 0, "tokens_out": 0, "duration_ms": 0 },
    "artifacts": [ { "kind": "FILE", "name": "...", "s3_key": "..." } ]
}}

// error
{ "type": "error", "payload": {
    "execution_id": "...", "attempt_no": 1,
    "code": "PROVIDER_5XX|PROVIDER_401|RATE_LIMIT|SANDBOX_INIT_FAILED|DEADLINE_EXCEEDED",
    "message": "...",
    "retry_after_s": 60
}}

// v0.4.2 改：resume 协议反向
{ "type": "resume_request", "payload": {
    "execution_id": "...",
    "attempt_no": 1
}}

{ "type": "resume_ack", "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "last_persisted_seq": 42,
    "snapshot": { /* execution.dispatch 重新发一遍，包括 input/context/permissions */ }
}}
```

## 5. 关键流程

### 5.1 E4 → E7 创建 Execution

```
E4 Orchestrator: collab.decision ACCEPT 收到
  → 写 decision_records
  → 写 collaboration_requests.status=ACCEPTED + slot_lease_id（E4 已 reservation）
  → 内部 API：POST /internal/agent-executions
     { collaboration_request_id, work_item_ref, input, context_refs }
  → E7:
     1. 校验 Agent.lifecycle=ACTIVE
     2. active_attempt_no=1, INSERT execution_attempts
     3. INSERT agent_executions (PENDING)
     4. dispatch 给 Agent
  → E4 回写 target_execution_id
```

### 5.2 v0.4.2 Terminal CAS 状态转换（核心修复）

```sql
-- ACCEPT 后的 SUCCEEDED 转换
UPDATE agent_executions
SET status = 'SUCCEEDED',
    completed_at = now(),
    active_attempt_no = NULL,
    terminal_envelope_id = $1         -- v0.4.2 新增：envelope id 落库
WHERE id = $2
  AND status IN ('PENDING', 'RUNNING');  -- 只允许从非终态转换
```

```
收到 result envelope：
  1. 查 agent_executions.terminal_envelope_id
     - 相同 → 重复 terminal，忽略（idempotency）
     - 不同 → 走 CAS
  2. affected_rows = 1 → 真正完成 → 写 outbox `execution.terminal` → E4 releaseLease（slot 释放）
  3. affected_rows = 0 → stale / 重复 / 并发竞争 → 忽略
```

### 5.3 v0.4.2 Resume 协议（反向）

```
Agent 重连：
  → hello (新 runtime_session_id)
  → Agent 立即推：execution.resume_request { execution_id, attempt_no: 1 }
  → Runtime：
     a) 查 active executions where active_attempt_no = $attempt_no
     b) 查 execution_events 最大 seq
     c) 返回 execution.resume_ack { last_persisted_seq: 42, snapshot: <重发 dispatch> }
  → Agent 从 seq=43 续发 event
```

**关键修正**（v0.4.2）：Runtime 不主动补发 event——event 是 Agent 产的，Runtime 持久化后只告知位点。Agent 自己重发。

### 5.4 Attempt 生命周期

```
新建 attempt 条件：
- Agent result 报 retryable=true
- E4 决策更新（V2+）
- 管理员手动重试

不新建 attempt 的场景：
- WS reconnect → resume_request，不新建 attempt
- 网络瞬断 → Agent 续发 event（UNIQUE idempotency）
- 重复 result → terminal_envelope_id 去重
```

### 5.5 Cancel 路径

```
来源：
- lifecycle=PAUSED / DISABLED（E2）→ 检查 in-flight executions → cancel
- 发起人 REST → E4 → E7 cancel
- 协作 timeout → E4 cancel
- 管理员手动

E7 cancel:
  → UPDATE agent_executions SET status='CANCELLED' WHERE id=? AND status IN ('PENDING','RUNNING')  -- CAS
  → active_attempt_no = NULL
  → 写 attempt.status=FAILED (reason=cancelled)
  → 释放 E4 slot lease
  → 写 audit
  → Agent 收到 WS cancel 命令
```

## 6. 验收标准

### 6.1 功能

- **F1** Agent hello / heartbeat / status 正常
- **F2** execution.dispatch 携带 execution_id，Runtime 写 execution_attempts
- **F3** execution.result **不**携带 decision
- **F4** **v0.4.2 改** Agent 推 `execution.resume_request`，Runtime 答 `execution.resume_ack`（含 `last_persisted_seq` + dispatch snapshot）
- **F5** **v0.4.2 改** Terminal CAS：result 转换只在 status ∈ (PENDING, RUNNING) 时成功
- **F6** **v0.4.2 改** 重复 result（envelope `id` 相同）忽略
- **F7** **v0.4.2 改** `execution_events.attempt_id` NOT NULL
- **F8** **v0.4.2 改** `execution_artifacts.attempt_id` NOT NULL
- **F9** Cancel CAS：只在非终态成功
- **F10** E7 完成时通过 E4 释放 slot lease

### 6.2 E2E

- `e2e/E7-001-connect-hello`
- `e2e/E7-002-dispatch-result`
- `e2e/E7-003-protocol-boundary`
- `e2e/E7-004-event-idempotency`
- `e2e/E7-005-attempt-not-reconnect`
- `e2e/E7-006-resume-request-direction`（v0.4.2 改）—— Agent 推 resume_request，Runtime 答 last_persisted_seq
- `e2e/E7-007-cancel-on-lifecycle-pause`
- `e2e/E7-008-active-slots-tracking`
- `e2e/E7-009-relay-restart-state-survive`（v0.5 改名：BullMQ → outbox relay）
- `e2e/E7-010-error-trigger`
- `e2e/E7-011-terminal-result-cas`（v0.4.2 新）—— 重复 result 第二次执行 UPDATE 返回 affected_rows=0
- `e2e/E7-012-cancel-result-race`（v0.4.2 新）—— cancel 和 result 同到，结果一致
- `e2e/E7-013-envelope-id-idempotency`（v0.4.2 新）—— 同 envelope.id 第二次写入忽略

## 7. 与其他 Epic 的关系

- **被依赖**：E2（agent + activity 上报）/ E4（Accept → 调 E7 API）/ E8（work_item_ref optional）/ E10
- **依赖**：E2
- **冲突裁决**：Execution 状态完全独立；协议边界 `collaboration.*` vs `execution.*` 严格分离

## 8. 风险与开放问题

- **R1**：cancel 与 result 同时到达时序（POST 各自带 envelope `id`）—— 通过 CAS 解决，但 envelope `id` 必须全局唯一
- **R2**：resume_request 收到时 Execution 已被 cancel → Runtime 返回 cancel 状态，Agent 走 cancel 路径
- **R3**：execution.dispatch 重发（retry 时）→ E4 不再 reservation 新 slot，E7 接管；envelope `id` 区分新旧 dispatch
- **R4**：execution_events 写入量大 → 短期 Redis 缓存 + 异步落 PG

## 9. 实施顺序（M8）

1. **v0.4.2 改** execution_events / execution_artifacts.attempt_id NOT NULL（migration）
2. **v0.4.2 改** agent_executions.terminal_envelope_id 字段
3. **v0.4.2 改** Terminal CAS 状态转换
4. **v0.4.2 改** resume 协议反向
5. envelope `id` 全局去重
6. Cancel CAS 路径
7. Slot lease 释放联动
8. Execution 详情页
9. E2E 套件
