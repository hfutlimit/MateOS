# E7 · Agent Execution

| 字段 | 值 |
| --- | --- |
| Epic ID | E7 |
| 标题 | Agent Execution |
| 阶段 | MVP（M8） |
| 上游 | PRD v0.4 / SYSTEM_DESIGN v0.3 §6 / v0.4.1 协议拆分 |
| 下游 | E4（Accept → E4 调 E7 API 创建 Execution）、E8（work_item_ref optional）、E10（audit） |
| 状态 | Draft（v0.4.1 协议边界收口版） |

## 1. 背景与动机

v0.4 把 E7 从 "WebSocket Connector" 升级为真正的 Agent Execution Domain。**v0.4.1 关键收口**：

1. **协议彻底拆开**——E4 拥有 `collaboration.*` 消息；E7 拥有 `execution.*` 消息。`execution.result` **不携带** decision / reason / analysis / needs
2. **execution_id 由 E4 Orchestrator 调用 E7 API 产生**——不是 Agent 在 `collaboration.decision` 中返回（这避免了不可能时序）
3. **Attempt ≠ WebSocket session**——WS reconnect 不一定 = new attempt；Agent resume 同一 attempt
4. **Event 协议级幂等**——`provider_event_id` + UNIQUE(attempt_id, provider_event_id)

## 2. 范围

### 2.1 In Scope

- `agent_executions` 事实源
- `execution_attempts`（logical attempt，与 WS session 解耦）
- `execution_events`（带 `provider_event_id` 幂等键）
- `execution_artifacts`
- **v0.4.1 协议**：`execution.dispatch` / `execution.event` / `execution.result` / `execution.error`
- E4 → E7 API（创建 Execution）
- Runtime Gateway（出站连接 + dispatch + 流式回传 + heartbeat）
- Agent activity 上报（lifecycle=ACTIVE 才允许）
- ERROR 触发与恢复（activity 维度）
- Cancel：lifecycle=PAUSED / 发起人 cancel / 超时
- 消息流 AGENT_OUTPUT 投影（`execution_ref`）
- 产物落 S3

### 2.2 Out of Scope

- Sandbox 实际执行（V3）
- 第三方 Agent 接入 SDK（V2）

## 3. 数据模型

```sql
-- agent_executions（事实源）
CREATE TABLE agent_executions (
  id                        UUID PRIMARY KEY,
  collaboration_request_id  UUID REFERENCES collaboration_requests(id),  -- 可空（API / Automation 也可触发无协作的执行）
  work_item_ref             JSONB,           -- {provider_key, work_item_id, external_ref} optional
  agent_id                  UUID NOT NULL REFERENCES agents(id),
  status                    TEXT NOT NULL DEFAULT 'PENDING'
                            CHECK (status IN ('PENDING','RUNNING','SUCCEEDED','FAILED','CANCELLED','TIMEOUT')),
  input                     JSONB NOT NULL,
  context_refs              JSONB NOT NULL DEFAULT '{}',
  -- v0.4.1 改：attempt_count + active attempt 跟踪
  attempt_count             INT NOT NULL DEFAULT 0,
  active_attempt_no         INT,             -- 当前活跃 attempt（null 表示无）
  started_at                TIMESTAMPTZ,
  completed_at              TIMESTAMPTZ,
  failure_code              TEXT,
  failure_message           TEXT,
  created_at                TIMESTAMPTZ DEFAULT now(),
  updated_at                TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_executions_agent ON agent_executions(agent_id, created_at DESC);
CREATE INDEX idx_executions_status ON agent_executions(status, created_at DESC);
CREATE INDEX idx_executions_collab_req ON agent_executions(collaboration_request_id);

-- execution_attempts：logical attempt（v0.4.1 改：与 WS session 解耦）
CREATE TABLE execution_attempts (
  id                  UUID PRIMARY KEY,
  execution_id        UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_no          INT NOT NULL,
  -- attempt 是 logical 重试单位；runtime_session 是物理 WS 连接
  runtime_session_id  TEXT,                          -- 当前绑定 WS session（可换）
  status              TEXT NOT NULL
                      CHECK (status IN ('STARTED','RUNNING','COMPLETED','FAILED')),
  started_at          TIMESTAMPTZ,
  completed_at        TIMESTAMPTZ,
  error               TEXT,
  UNIQUE (execution_id, attempt_no)
);
CREATE INDEX idx_attempts_runtime_session ON execution_attempts(runtime_session_id) WHERE runtime_session_id IS NOT NULL;

-- execution_events：带协议级幂等键（v0.4.1 新增）
CREATE TABLE execution_events (
  id                BIGSERIAL PRIMARY KEY,
  execution_id      UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_id        UUID REFERENCES execution_attempts(id),
  event_type        TEXT NOT NULL
                    CHECK (event_type IN ('STDOUT','PROGRESS','TOOL_CALL','LLM_TICK','ARTIFACT','ERROR')),
  -- v0.4.1 新增：协议级幂等键（crash recovery / Agent resend / Gateway resend / WS reconnect 都靠这个去重）
  provider_event_id TEXT NOT NULL,
  seq               BIGINT NOT NULL,                -- 单 attempt 内单调递增
  payload           JSONB NOT NULL,
  trace_id          TEXT,
  created_at        TIMESTAMPTZ DEFAULT now(),
  UNIQUE (attempt_id, provider_event_id),
  UNIQUE (attempt_id, seq)
);
CREATE INDEX idx_events_execution_time ON execution_events(execution_id, created_at);
CREATE INDEX idx_events_attempt_seq ON execution_events(attempt_id, seq);

-- execution_artifacts
CREATE TABLE execution_artifacts (
  id            UUID PRIMARY KEY,
  execution_id  UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_id    UUID REFERENCES execution_attempts(id),
  kind          TEXT NOT NULL CHECK (kind IN ('FILE','DIFF','LOG','SCREENSHOT')),
  name          TEXT NOT NULL,
  s3_key        TEXT NOT NULL,
  size          BIGINT,
  mime          TEXT,
  created_at    TIMESTAMPTZ DEFAULT now()
);

-- agents 表新增（v0.4.1 与 E4 共享）：调度相关
--   max_concurrency  INT NOT NULL DEFAULT 1
--   active_slots     INT NOT NULL DEFAULT 0
```

### 3.1 Attempt × Runtime Session 解耦（v0.4.1）

```
Attempt = logical 重试单位
  - 新 attempt_no = previous + 1
  - 旧 attempt.status 标 FAILED
  - 不创建新的 agent_executions

Runtime Session = 物理 WS 连接
  - Agent 重连时换 runtime_session_id
  - **同一 attempt 可换多个 session**
  - 何时新建 attempt？
    - Agent 显式声明 attempt 完成 / 失败
    - deadline_s 到
    - E4 决策 DELAY / RETRY
```

### 3.2 Event 协议级幂等（v0.4.1）

```
场景：crash recovery / Agent resend / Gateway resend / WS reconnect
  - Agent 重发 `execution.event` 时带同一个 `provider_event_id`
  - INSERT UNIQUE(attempt_id, provider_event_id) → 重复事件被 DB 拒绝（不抛错，记一条 ignored 即可）
  - seq 单调递增，UNIQUE(attempt_id, seq) 保底
```

## 4. 协议（v0.4.1 拆分）

```jsonc
// 通用 envelope
{ "v": 1, "type": "hello|heartbeat|status|dispatch|event|result|error|resume", "id": "uuid", "ts": 0, "payload": {} }

// hello / heartbeat / status（E7 拥有）—— 同 v0.4
//   但 type 集合现在是 E7 专属，E4 消息名不重叠

// execution.dispatch（E7 拥有，Runtime → Agent）
{ "type": "dispatch", "payload": {
    "execution_id": "...",
    "collaboration_request_id": "..."?,    // 可空
    "work_item_ref": {...}?,                // optional
    "input": { "prompt": "...", "params": {} },
    "context": { "memory_refs": [], "recent_messages": [], "permissions": {} },
    "deadline_s": 600, "idempotency_key": "..."
}}

// execution.event
{ "type": "event", "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "event_type": "STDOUT|PROGRESS|TOOL_CALL|LLM_TICK|ARTIFACT|ERROR",
    "provider_event_id": "evt-uuid-123",   // 协议级幂等键
    "seq": 42,                              // 单 attempt 内单调
    "payload": { "content": "...", "meta": {} }
}}

// execution.result（v0.4.1 改：不携带 decision）
{ "type": "result", "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "status": "SUCCEEDED|FAILED|CANCELLED",
    "output": { "markdown": "...", "code": "..." },
    "usage": { "tokens_in": 0, "tokens_out": 0, "duration_ms": 0 },
    "artifacts": [ { "kind": "FILE", "name": "...", "s3_key": "..." } ]
}}

// execution.error
{ "type": "error", "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "code": "PROVIDER_5XX|PROVIDER_401|RATE_LIMIT|SANDBOX_INIT_FAILED|DEADLINE_EXCEEDED",
    "message": "...",
    "retry_after_s": 60
}}

// execution.resume（v0.4.1 新增：Agent 重连后请求恢复 in-flight dispatch）
{ "type": "resume", "payload": {
    "execution_id": "...",
    "last_event_seq": 42                   // Agent 已确认收到的最后一条 event
}}
// Runtime 返回：从 last_event_seq+1 起的所有 events（避免重发）
```

### 4.1 协议边界（v0.4.1 强制）

| E4 拥有 | E7 拥有 |
| --- | --- |
| `collaboration.request` | `execution.dispatch` |
| `collaboration.decision` | `execution.event` |
| `collaboration.cancelled` | `execution.result` |
| `collaboration.resolved` | `execution.error` |
| | `execution.resume`（v0.4.1 新） |
| | `hello` / `heartbeat` / `status` |

**禁止**：
- E4 消息中携带 `execution_id`（除了 collaboration.decision 携带 `collaboration_request_id`）
- E7 消息中携带 `decision` / `reason` / `analysis` / `needs`

## 5. 关键流程

### 5.1 E4 → E7 创建 Execution

```
E4 Orchestrator 收到 collaboration.decision { decision: ACCEPT }
  → 写 decision_records
  → 写 collaboration_requests.status = ACCEPTED
  → E4 → E7 内部 API：
     POST /internal/agent-executions
     { collaboration_request_id, work_item_ref, input, context_refs }
  → E7:
     1. 校验 Agent.lifecycle = ACTIVE
     2. INSERT agent_executions (PENDING)
     3. active_slots++ (Redis)
     4. 写 execution_attempts(1, STARTED) + active_attempt_no=1
     5. dispatch 给 Agent
  → E4 回写 collaboration_requests.target_execution_id = execution.id
  → E4 WS 推 collaboration.resolved + message 流 DECISION 投影
```

### 5.2 Agent Cancel（v0.4.1 改）

```
来源：
- lifecycle=PAUSED / DISABLED（E2）→ E7 检查 in-flight executions → cancel
- 发起人 REST POST /collaboration-requests/:id/cancel → E4 → E7 cancel
- 协作 timeout → E4 cancel
- E2 写 lifecycle=PAUSED → E7 WS 推 cancel 命令给 Agent

E7 cancel：
  → agent_executions.status = CANCELLED
  → execution_attempts.active.status = FAILED (cancel_reason)
  → active_slots-- (Redis)
  → 写 audit
```

### 5.3 Crash Recovery（v0.4.1 改：resume 协议）

```
Agent 重连：
  → hello (新 runtime_session_id)
  → Runtime 检查 in-flight executions where runtime_session_id = OLD session
  → 对每个 in-flight：
     Runtime → Agent: execution.dispatch (新 attempt? 不，只换 session)
       execution.resume 协议：Agent 推送 last_event_seq
       Runtime 补发 seq > last_event_seq 的 events
  → Agent 续写同一 attempt
```

**关键**：WS reconnect ≠ new attempt。Network blip 不应该算 retry。

### 5.4 Event 协议级幂等（v0.4.1 新增）

```
Agent 推 execution.event { provider_event_id: "evt-abc", seq: 42 }
  → INSERT (attempt_id, provider_event_id, seq, payload)
  → 重复 provider_event_id：UNIQUE 冲突 → ignore（不抛错，记 audit "event_dedup"）
  → 重复 seq：UNIQUE 冲突 → 严重错误（agent 协议 bug）
```

### 5.5 Retry 触发条件（v0.4.1 明确）

何时新建 `execution_attempts.attempt_no = N+1`？
- Agent result 推 `status=FAILED` 且 `retryable=true`
- E4 决策更新（V2+ 启用 DELAY/RETRY 决策）
- 管理员手动重试
- **不**由 WS reconnect 触发

## 6. UI

- **P3 Agent Card**「当前工作」区块：显示最近 1 条 RUNNING 的 execution（v0.4 不变）
- **Execution 详情页**（待做）：时间线 + 事件流 + 产物下载
- 顶栏 / 上下文面板：Agent `activity=WORKING` 蓝点 + 光标（v0.4 不变；activity 现在是 E7 推的）

## 7. 验收标准

### 7.1 功能

- **F1** Agent hello → token 校验通过
- **F2** execution.dispatch 携带 execution_id，Runtime 写 execution_attempts
- **F3** **v0.4.1 改** execution.result **不**携带 decision / reason / analysis / needs（schema 校验）
- **F4** **v0.4.1 改** execution_id 由 E4 → E7 API 产生，**不**在 collaboration.decision 中
- **F5** 流式 events 落 execution_events，message 流式可见
- **F6** **v0.4.1 改** Attempt ≠ WS session：reconnect 不新建 attempt
- **F7** **v0.4.1 改** Event 协议级幂等：同一 provider_event_id 重复事件 DB 拒绝（不抛错）
- **F8** Cancel：lifecycle=PAUSED → in-flight execution 收到 cancel
- **F9** active_slots 准确（dispatch 时 +1，结束时 -1）
- **F10** BullMQ job 重启后 execution 状态不丢（DB 是事实源）

### 7.2 E2E

- `e2e/E7-001-connect-hello`
- `e2e/E7-002-dispatch-result`
- `e2e/E7-003-protocol-boundary`（v0.4.1 新）—— 验证 execution.result 不含 decision
- `e2e/E7-004-event-idempotency`（v0.4.1 新）—— 重复 provider_event_id DB 拒绝
- `e2e/E7-005-attempt-not-reconnect`（v0.4.1 新）—— WS 断 5 次 reconnect，attempt_no 仍 = 1
- `e2e/E7-006-resume-protocol`（v0.4.1 新）—— 模拟网络断，Agent 推 last_event_seq=42，Runtime 补发 43+
- `e2e/E7-007-cancel-on-lifecycle-pause`
- `e2e/E7-008-active-slots-tracking`
- `e2e/E7-009-bullmq-restart-state-survive`
- `e2e/E7-010-error-trigger`

### 7.3 非功能

- 单 Runtime 实例支持 1000 并发 agent 连接
- 状态广播 P99 < 200ms

## 8. 与其他 Epic 的关系

- **被依赖**：E2（agent + activity 上报）/ E4（Accept → 调用 E7 API）/ E8（work_item_ref optional）/ E10（audit）
- **依赖**：E2（agent + token）
- **冲突裁决**：Execution 状态完全独立于 CollaborationRequest 状态（v0.4.1）；协议边界 `collaboration.*` vs `execution.*` 严格分离

## 9. 风险与开放问题

- **R1**：active_slots 跨实例 → Redis INCR/DECR
- **R2**：resume 协议补发时如果 Agent 已 ack 但 Runtime 端丢 → 重新发（Agent 应幂等处理）
- **R3**：event 流式量大 → 短期 Redis 缓存 + 异步落 PG
- **R4**：retry 决策由谁做？→ V1：Agent result 报 retryable=true → E7 新建 attempt；E4 决策更新（V2）

## 10. 实施顺序（M8）

1. **v0.4.1 改** agents 表加 `max_concurrency` / `active_slots` + Redis
2. **v0.4.1 改** execution_events 加 `provider_event_id` + UNIQUE
3. **v0.4.1 改** execution_attempts 与 runtime_session_id 解耦
4. **v0.4.1 改** 协议拆分 + E4 → E7 内部 API
5. **v0.4.1 新增** resume 协议
6. **v0.4.1 新增** active_slots 跟踪
7. Cancel 路径（lifecycle / 发起人 / timeout）
8. Execution 详情页
9. E2E 套件
