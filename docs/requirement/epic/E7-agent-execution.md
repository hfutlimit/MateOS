# E7 · Agent Execution

| 字段 | 值 |
| --- | --- |
| Epic ID | E7 |
| 标题 | Agent Execution |
| 阶段 | MVP（M8） |
| 上游 | PRD v0.4 §4.3 / SYSTEM_DESIGN v0.3 §6 Agent Runtime + Execution Domain / v0.3 旧 E7 升级 |
| 下游 | E4（Accept → 创建 Execution）、E8（WorkItem 可选引用 Execution）、E10（audit） |
| 状态 | Draft（v0.4 升级版：原 Connector 升级为真正 Execution Domain） |

## 1. 背景与动机

v0.3 的 E7 只定义了 **Connector 协议**（WSS / hello / heartbeat / dispatch / progress / result / error），把 Agent Execution 当作"WebSocket 连接"。v0.4 推倒重来：

**Agent Execution 不是一个 WebSocket 连接；是一个 durable 的执行记录。**

WebSocket 只是 transport / scheduler。**事实源是 `agent_executions` + `execution_attempts` + `execution_events` + `execution_artifacts`**。这层一加，retry / reconnect / crash recovery / timeout / cancel / streaming / artifact 全部有地方放。

同时 **BullMQ job 绝不能成为事实源**——它只是把任务推到 Runtime。

## 2. 范围

### 2.1 In Scope

- `agent_executions` 表（事实源：status / input / context_refs / work_item_ref / attempt_count）
- `execution_attempts`（重试 / 重连的 attempt）
- `execution_events`（流式事件：STDOUT / PROGRESS / TOOL_CALL / LLM_TICK / ARTIFACT / ERROR）
- `execution_artifacts`（产物：FILE / DIFF / LOG / SCREENSHOT，S3 存储）
- Connector 协议 v1（带 `execution_id` + `collaboration_request_id`）
- Runtime Gateway（出站连接 + dispatch + 流式回传）
- Agent token 签发与心跳保活
- Agent activity 上报（与 E2 协作，lifecycle=ACTIVE 才允许）
- ERROR 触发与恢复
- Sandbox 预占接口（V3 启用，V1 仅协议层）
- 消息流 AGENT_OUTPUT 投影（`execution_ref` 引到本 epic）

### 2.2 Out of Scope

- Sandbox 容器实际执行（V3，V1 仅协议层预留）
- 第三方 Agent 接入 SDK（V1 仅协议，V2 出 SDK）
- Multi-tenant 隔离运行时（V1 单租户）

## 3. 数据模型

```sql
-- agent_executions（事实源）
CREATE TABLE agent_executions (
  id                        UUID PRIMARY KEY,
  collaboration_request_id  UUID REFERENCES collaboration_requests(id),  -- 触发源（v0.4 新增）
  work_item_ref             JSONB,           -- {provider_key, work_item_id, external_ref} optional
  agent_id                  UUID NOT NULL REFERENCES agents(id),
  status                    TEXT NOT NULL DEFAULT 'PENDING'
                            CHECK (status IN ('PENDING','RUNNING','SUCCEEDED','FAILED','CANCELLED','TIMEOUT')),
  input                     JSONB NOT NULL,   -- 协作请求 / dispatch 的输入快照
  context_refs              JSONB NOT NULL DEFAULT '{}',
  attempt_count             INT NOT NULL DEFAULT 0,
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

-- execution_attempts：每次重试/重连
CREATE TABLE execution_attempts (
  id                  UUID PRIMARY KEY,
  execution_id        UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_no          INT NOT NULL,
  runtime_session_id  TEXT,
  status              TEXT NOT NULL,            -- 'STARTED' | 'RUNNING' | 'COMPLETED' | 'FAILED'
  started_at          TIMESTAMPTZ,
  completed_at        TIMESTAMPTZ,
  error               TEXT,
  UNIQUE (execution_id, attempt_no)
);
CREATE INDEX idx_attempts_runtime_session ON execution_attempts(runtime_session_id);

-- execution_events：流式事件
CREATE TABLE execution_events (
  id              BIGSERIAL PRIMARY KEY,
  execution_id    UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_id      UUID REFERENCES execution_attempts(id),
  event_type      TEXT NOT NULL
                  CHECK (event_type IN ('STDOUT','PROGRESS','TOOL_CALL','LLM_TICK','ARTIFACT','ERROR')),
  payload         JSONB NOT NULL,
  trace_id        TEXT,
  created_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_events_execution_time ON execution_events(execution_id, created_at);
CREATE INDEX idx_events_attempt ON execution_events(attempt_id, created_at) WHERE attempt_id IS NOT NULL;

-- execution_artifacts：产物
CREATE TABLE execution_artifacts (
  id            UUID PRIMARY KEY,
  execution_id  UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  kind          TEXT NOT NULL CHECK (kind IN ('FILE','DIFF','LOG','SCREENSHOT')),
  name          TEXT NOT NULL,
  s3_key        TEXT NOT NULL,
  size          BIGINT,
  mime          TEXT,
  created_at    TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_artifacts_execution ON execution_artifacts(execution_id);

-- agent_tokens（不变）
CREATE TABLE agent_tokens (
  id          UUID PRIMARY KEY,
  agent_id    UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  token_hash  TEXT NOT NULL,
  label       TEXT,
  last_seen_at TIMESTAMPTZ,
  expires_at  TIMESTAMPTZ,
  revoked     BOOLEAN NOT NULL DEFAULT false,
  created_at  TIMESTAMPTZ DEFAULT now()
);

-- llm_calls（E2 用量统计）
CREATE TABLE llm_calls (
  id          UUID PRIMARY KEY,
  agent_id    UUID NOT NULL REFERENCES agents(id),
  call_id     UUID,
  execution_id UUID REFERENCES agent_executions(id),  -- v0.4 新增：关联到 execution
  model       TEXT NOT NULL,
  tokens_in   INT NOT NULL,
  tokens_out  INT NOT NULL,
  cost_usd    NUMERIC(10,4),
  duration_ms INT,
  created_at  TIMESTAMPTZ DEFAULT now()
);
```

### 3.1 Execution 状态机

```
agent_executions.status
  PENDING ──► RUNNING ──┬─► SUCCEEDED
                        ├─► FAILED
                        ├─► CANCELLED
                        └─► TIMEOUT

execution_attempts.status
  STARTED ──► RUNNING ──┬─► COMPLETED
                        └─► FAILED
```

attempt_count 自动 +1（每次重试/重连都新开一个 attempt）。

## 4. Connector 协议（v1.1 — v0.4 加 `execution_id` / `collaboration_request_id`）

```jsonc
// 通用 envelope
{ "v": 1, "type": "hello|heartbeat|status|dispatch|progress|result|error", "id": "uuid", "ts": 0, "payload": {} }

// dispatch（v0.4 加 execution_id）
{ "type": "dispatch", "payload": {
    "execution_id": "...",                           // ← 新增
    "collaboration_request_id": "...",               // ← 新增
    "work_item_ref": {...}?,                         // ← optional
    "input": { "prompt": "...", "params": {...} },   // 必填快照
    "context": { "memory_refs": [...], "recent_messages": [...], "permissions": {...} },
    "deadline_s": 600, "idempotency_key": "..."
}}

// result（v0.4 必填 execution_id）
{ "type": "result", "payload": {
    "execution_id": "...",
    "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason": "...", "needs": [...],
    "analysis": { "capability": true, "context_score": 88, "permission": true },
    "output": { "markdown": "...", "code": "...", "tokens_in": 0, "tokens_out": 0, "latency_ms": 0 }
}}

// event（流式）
{ "type": "event", "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "event_type": "STDOUT|PROGRESS|TOOL_CALL|LLM_TICK|ARTIFACT|ERROR",
    "payload": { "content": "...", "meta": {...} }
}}
```

## 5. 关键流程

### 5.1 CollaborationRequest Accept → Execution 创建（v0.4 新增）

```
1. E4 Orchestrator 收到 decision=ACCEPT
2. 写 agent_executions:
   - collaboration_request_id = req.id
   - work_item_ref = req.work_item_ref（optional）
   - input = req 的快照
   - context_refs = req.context_refs
   - status = PENDING
3. Runtime Gateway 收到新 execution，dispatch 到对应 Agent
4. 创建 execution_attempts (attempt_no=1)
5. Agent 推 status=STARTED → RUNNING（activity 也更新为 WORKING）
6. Agent 推流式 events → execution_events
7. Agent 推 result → 写 execution_artifacts（如有）+ agent_executions.status=SUCCEEDED/FAILED
8. E4 收到 result 写 decision_records，collaboration_request.status=COMPLETED
9. 消息流写 AGENT_OUTPUT 投影（content.execution_ref = execution.id）
```

### 5.2 重试 / Crash Recovery

```
Runtime 检测到 dispatch 超时 / 失败：
  1. agent_executions.attempt_count + 1
  2. 新建 execution_attempts (attempt_no=N+1)
  3. 重 dispatch
  4. 上次 attempt.status = FAILED，记录 error

Agent 重连后：
  - Gateway 查 in-flight executions
  - 重 dispatch（idempotency_key 防重）
  - Agent 续写 attempt
```

### 5.3 超时 / Cancel

```
- deadline_s 倒计时到 → execution_attempts.status=FAILED + agent_executions.status=TIMEOUT
- lifecycle 变更（PAUSED/DISABLED，E2）→ 检查 in-flight executions，触发 cancel
- 用户主动 cancel → 写 CANCELLED + 通知 Runtime 停止
```

### 5.4 产物落库

```
Agent 推 ARTIFACT event:
  → execution_artifacts 行
  → s3_key + metadata
  → 消息流 AGENT_OUTPUT 投影可附 artifact_id
```

### 5.5 ERROR 触发（v0.4 拆：lifecycle 仍 ACTIVE，activity 转 ERROR）

同 v0.3 逻辑；E2 activity 维度更新。

## 6. UI

- **P3 Agent Card**「当前工作」区块：展示最近 1 条 RUNNING 的 execution 详情（v0.4 改）
- **Execution 详情页**（待做）：时间线 + 事件流 + 产物下载
- 顶栏 / 上下文面板：Agent activity WORKING 时蓝点 + 光标

## 7. 验收标准

### 7.1 功能

- **F1** Agent 出站 hello → 校验 token → agent_tokens.last_seen_at 更新
- **F2** dispatch 携带 execution_id，Runtime 据此写 execution_attempts
- **F3** 流式 events 落 execution_events，message 流式可见
- **F4** result 落 decision_records + agent_executions.status=SUCCEEDED
- **F5** **v0.4 新增** 重试：attempt_count + 1 + 新 attempt
- **F6** **v0.4 新增** Crash recovery：重连后 in-flight execution 续 dispatch
- **F7** **v0.4 新增** timeout → status=TIMEOUT + reason
- **F8** **v0.4 新增** Cancel：lifecycle=PAUSED → in-flight execution 收到 cancel
- **F9** 产物（artifact）落 S3 + execution_artifacts
- **F10** BullMQ job 重启后 execution 状态**不丢**（DB 是事实源）

### 7.2 E2E

- `e2e/E7-001-connect-hello`
- `e2e/E7-002-dispatch-result`
- `e2e/E7-003-retry-after-failure`（v0.4 新）
- `e2e/E7-004-crash-recovery`（v0.4 新）—— 模拟 Runtime 重启，execution 续
- `e2e/E7-005-timeout-handling`（v0.4 新）
- `e2e/E7-006-cancel-on-lifecycle-pause`（v0.4 新）
- `e2e/E7-007-artifact-s3`
- `e2e/E7-008-bullmq-restart-state-survive`（v0.4 新）
- `e2e/E7-009-error-trigger`

### 7.3 非功能

- 单 Runtime 实例支持 1000 并发 agent 连接
- 状态广播 P99 < 200ms
- 1000 agent × 30s 心跳无瓶颈

## 8. 与其他 Epic 的关系

- **被依赖**：E2（agent 实体 + activity 上报）/ E4（Accept → 创建 Execution）/ E8（WorkItem 可选引用 Execution）/ E10（audit）
- **依赖**：E2（agent + token）
- **冲突裁决**：Execution 模型与 SD v0.3 §6.3 / §6.5 对齐

## 9. 风险与开放问题

- **R1**：Sandbox 真实执行（V3）— V1 仅协议层，execution_artifacts.s3_key 由 Runtime 写
- **R2**：LLM token 用量统计挂 execution（v0.4 改）—— llm_calls.execution_id
- **R3**：流式 events 量大时（长任务）pg 写入压力 → 异步落库 + 短期 Redis 缓存
- **R4**：execution_attempts 重新跑时 idempotency_key 由谁生成？→ Runtime 侧生成 + 写 dispatch 协议

## 10. 实施顺序（M8 — 推到 M8 配合 E8 同时落地）

1. agent_executions / attempts / events / artifacts 表
2. Connector 协议 v1.1（加 execution_id / collaboration_request_id / event 类型）
3. Runtime Gateway 改造：dispatch → attempt + event 落库
4. **v0.4 新增** 重试 / Crash recovery / Timeout / Cancel
5. Activity 联动 E2
6. Execution 详情页（前端）
7. E2E 套件
