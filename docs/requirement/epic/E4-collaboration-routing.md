# E4 · Collaboration & Routing

| 字段 | 值 |
| --- | --- |
| Epic ID | E4 |
| 标题 | Collaboration & Routing |
| 阶段 | MVP（M4） |
| 上游 | PRD v0.4 §5 FR-4 / FR-5 / SYSTEM_DESIGN v0.3 §4.1 / §4.2 / §5.2 collaboration_requests / UI DS v0.5 §5.1 |
| 下游 | E3（消息载体 + Trigger 提取）、E7（Accept → 创建 Execution）、E10（audit） |
| 状态 | Draft（v0.4.1 协议边界收口） |

## 1. 背景与动机

v0.4 引入 CollaborationRequest 一等实体。**v0.4.1 关键收口**：

1. **E4 / E7 协议彻底拆开**——`collaboration.decision`（E4）与 `execution.dispatch` / `execution.event` / `execution.result`（E7）属于不同 message type，不能混淆
2. **CollaborationRequest status 不再镜像 Execution status**——三条 lifecycle 真正独立
3. **Resolver 调度用 lifecycle + active_slots（max_concurrency）**——不再用 activity（activity 仅做 UI derived）

## 2. 范围

### 2.1 In Scope

- 4 种 Trigger：MENTION / WORK_ITEM / API / AUTOMATION
- **v0.4.1** CollaborationRequest status 收敛为 `PENDING | ACCEPTED | REJECTED | NEED_CONTEXT | UNRESOLVED | CANCELLED`
- Mention Resolver（lifecycle=ACTIVE + active_slots < max_concurrency）
- Capability ranking
- Decision 状态机（**不再**含 EXECUTING/COMPLETED/FAILED）
- Analysis 三件套（capability / context_score / permission）
- 超时重路由（60s 默认）
- @all 投递
- **v0.4.1 协议边界**：E4 拥有 `collaboration.decision`（agent → orchestrator），E7 拥有 `execution.*`（agent → runtime）

### 2.2 Out of Scope

- Delegate 实际路由（V2）
- @all 仲裁阈值动态调整（M6 之后）

## 3. 数据模型

```sql
-- triggers
CREATE TABLE triggers (
  id              UUID PRIMARY KEY,
  trigger_type    TEXT NOT NULL CHECK (trigger_type IN ('MENTION','WORK_ITEM','API','AUTOMATION')),
  trigger_ref     JSONB NOT NULL,
  from_actor_type TEXT NOT NULL,
  from_actor_id   UUID NOT NULL,
  captured_at     TIMESTAMPTZ DEFAULT now()
);

-- collaboration_requests（v0.4.1 状态收敛：去掉 EXECUTING/COMPLETED/FAILED）
CREATE TABLE collaboration_requests (
  id                      UUID PRIMARY KEY,
  trigger_id              UUID NOT NULL REFERENCES triggers(id),
  trigger_type            TEXT NOT NULL,
  trigger_ref             JSONB NOT NULL,
  request_kind            TEXT NOT NULL
                          CHECK (request_kind IN ('MESSAGE_RESPONSE','WORK_ITEM_EXECUTION','API_CALL','AUTOMATION_RUN')),
  from_actor_type         TEXT NOT NULL,
  from_actor_id           UUID NOT NULL,
  target_agent_id         UUID REFERENCES agents(id),
  required_capabilities   JSONB NOT NULL DEFAULT '[]',
  context_refs            JSONB NOT NULL DEFAULT '{}',
  -- v0.4.1 收敛：只到 Decision 结果；Execution 状态由 E7 维护
  status                  TEXT NOT NULL DEFAULT 'PENDING'
                          CHECK (status IN (
                            'PENDING',         -- Resolver 未决
                            'ACCEPTED',        -- Agent 决策接受（E4 视角）
                            'REJECTED',        -- Agent 决策拒绝
                            'NEED_CONTEXT',    -- Agent 等人类补齐
                            'UNRESOLVED',      -- 全部候选超时
                            'CANCELLED'        -- 发起人 / 管理员取消
                          )),
  -- 关联到 Execution（事实源在 agent_executions 表）
  target_execution_id     UUID,            -- 接受后由 E7 写入
  deadline_s              INT NOT NULL DEFAULT 600,
  idempotency_key         TEXT UNIQUE,
  created_at              TIMESTAMPTZ DEFAULT now(),
  resolved_at             TIMESTAMPTZ
);

-- decision_records（事实源）
CREATE TABLE decision_records (
  id                       UUID PRIMARY KEY,
  collaboration_request_id UUID NOT NULL REFERENCES collaboration_requests(id),
  agent_id                 UUID NOT NULL REFERENCES agents(id),
  decision                 TEXT NOT NULL CHECK (decision IN ('ACCEPT','REJECT','NEED_CONTEXT','DELEGATE')),
  reason                   TEXT,
  needs                    JSONB,
  analysis                 JSONB NOT NULL,         -- {capability, context_score, permission}
  delegate_to              UUID,
  decided_at               TIMESTAMPTZ,
  created_at               TIMESTAMPTZ DEFAULT now()
);

-- agents 表新增：调度相关字段（v0.4.1）
--   max_concurrency  INT NOT NULL DEFAULT 1
--   active_slots     INT NOT NULL DEFAULT 0
-- E4 Resolver 调度依据
```

### 3.1 v0.4.1 状态机收敛

```
PENDING ─┬─► ACCEPTED  ─► E4 不再跟踪 Execution 状态
         ├─► REJECTED  ─► 通知发起人
         ├─► NEED_CONTEXT
         ├─► UNRESOLVED（全部候选超时）
         └─► CANCELLED（发起人 / 管理员 / lifecycle 变更触发）

Execution 状态完全在 E7：
  agent_executions.status ∈ {PENDING, RUNNING, SUCCEEDED, FAILED, CANCELLED, TIMEOUT}

UI 渲染时如需「执行中」状态，由前端从 Execution 拉取后做 projection（display_state）
```

## 4. 协议（v0.4.1 拆分）

### 4.1 协议边界（关键）

| Message type | 方向 | 拥有方 | 用途 |
| --- | --- | --- | --- |
| `collaboration.request` | Runtime → Orchestrator | E4 | （内部）创建 collaboration_request |
| **`collaboration.decision`** | **Agent → Orchestrator** | **E4** | **Agent 对 collaboration_request 的回应（ACCEPT/REJECT/NEED_CONTEXT/DELEGATE）** |
| `collaboration.cancelled` | Orchestrator / 发起人 | E4 | 取消协作 |
| `collaboration.resolved` | Orchestrator → Client（WS） | E4 | Resolver 命中结果 |
| **`execution.dispatch`** | **Runtime → Agent** | **E7** | **执行任务下发（execution_id 已存在）** |
| `execution.event` | Agent → Runtime | E7 | 流式事件（STDOUT/PROGRESS/TOOL_CALL/LLM_TICK/ARTIFACT/ERROR） |
| **`execution.result`** | **Agent → Runtime** | **E7** | **执行结果（不包含 decision）** |
| `execution.error` | Agent → Runtime | E7 | 执行失败 |

**关键**：`execution.result` 不能再携带 decision / reason / analysis / needs。这些是 `collaboration.decision` 的字段。

### 4.2 协议 Envelope

```jsonc
// 通用
{ "v": 1, "type": "<type>", "id": "uuid", "ts": 0, "payload": {} }

// collaboration.decision（E4 拥有）
{ "type": "collaboration.decision", "payload": {
    "collaboration_request_id": "...",
    "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason": "...",
    "needs": ["api spec", "db design"],
    "analysis": { "capability": true, "context_score": 88, "permission": true },
    "delegate_to": "agent:..."
}}

// execution.dispatch（E7 拥有）
{ "type": "execution.dispatch", "payload": {
    "execution_id": "...",
    "collaboration_request_id": "...",     // optional，可无 WorkItem 触发
    "work_item_ref": {"provider_key":"builtin", "work_item_id":"..."}?,   // optional
    "input": { "prompt": "...", "params": {} },
    "context": { "memory_refs": [], "recent_messages": [], "permissions": {} },
    "deadline_s": 600, "idempotency_key": "..."
}}

// execution.event
{ "type": "execution.event", "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "event_type": "STDOUT|PROGRESS|TOOL_CALL|LLM_TICK|ARTIFACT|ERROR",
    "provider_event_id": "evt-uuid-123",   // 协议级幂等键（v0.4.1 新增）
    "payload": { "content": "...", "meta": {} }
}}

// execution.result
{ "type": "execution.result", "payload": {
    "execution_id": "...",
    "status": "SUCCEEDED|FAILED|CANCELLED",
    "output": { "markdown": "...", "code": "..." },
    "usage": { "tokens_in": 0, "tokens_out": 0, "duration_ms": 0 },
    "artifacts": [ { "kind": "FILE", "name": "...", "s3_key": "..." } ]
}}
```

## 5. 关键流程

### 5.1 Resolver 调度（v0.4.1 改）

```
SELECT target_agent
  FROM agents a
  WHERE a.lifecycle = 'ACTIVE'
    AND a.active_slots < a.max_concurrency
    AND check(a, 'write_message', channel_scope) = 'ALLOW'
    AND a.id IN (
        SELECT member_id FROM channel_members
        WHERE channel_id = $1 AND member_type = 'AGENT'
    )
  ORDER BY score DESC
  LIMIT 3;
```

- **不再依赖 activity**（`AVAILABLE/THINKING/...`）做调度——activity 只做 UI derived
- `active_slots` 由 E7 Execution 启动时 +1，结束时 -1
- 调度写 decision_records → UI 通过 lifecycle 字段继续展示 Agent 状态

### 5.2 ACCEPT → Execution 跨 epic 联动

```
1. Agent 上报 `collaboration.decision` { decision: ACCEPT }
2. E4 Orchestrator 写 decision_records + collaboration_requests.status=ACCEPTED
3. E4 Orchestrator **调用 E7 API** 创建 agent_executions（不是 Agent 自己创建）：
   POST /internal/agent-executions
   { collaboration_request_id, work_item_ref, input, context_refs }
4. E7 写 agent_executions（PENDING）+ execution_attempts(1, STARTED) + active_slots++
5. E7 派发 `execution.dispatch` 给 Agent
6. Agent 推 `execution.event` 流式 / `execution.result` 最终结果
7. E7 写 agent_executions.status=SUCCEEDED/FAILED，active_slots--
8. UI 从 execution 拉取状态，projection display_state="EXECUTING"/"COMPLETED"
```

**关键**：`execution_id` 由 E4 Orchestrator → E7 API 调用产生，**不是** Agent 在 `collaboration.decision` 中返回的。这避免了 v0.4 之前"decision 与 execution_id 同时存在"的不可能时序。

### 5.3 超时重路由

```
collaboration_requests.status = PENDING 时 BullMQ 60s 延迟任务
  → 取下一个候选重投
  → 全部超时 → status=UNRESOLVED
  → 写 audit + 通知发起人
```

## 6. UI

- 决策卡片从 `decision_records` 投影（v0.4 不变）
- **v0.4.1** 决策卡展示 + Execution 状态从 E7 拉取（projection display_state）
- @all 预警文案不变
- mention 胶囊命中结果气泡不变

## 7. 验收标准

### 7.1 功能

- **F1** MENTION 触发 → triggers 写 → collaboration_requests 同步
- **F2** **v0.4.1 改** Resolver 过滤：`lifecycle=ACTIVE ∩ active_slots < max_concurrency`，**不再**用 activity
- **F3** **v0.4.1 改** `collaboration.decision` 消息体不携带 execution_id（execution_id 由 E4 调用 E7 API 产生）
- **F4** **v0.4.1 改** `execution.result` 消息体不携带 decision / reason / analysis / needs
- **F5** Decision Accept → E4 调用 E7 API 创建 agent_executions
- **F6** Decision Need Context 必带 needs[]
- **F7** PENDING 60s 后未响应 → 重路由
- **F8** 全部超时 → status=UNRESOLVED + 通知
- **F9** 消息流 DECISION 形态的 content 只引 `decision_ref`
- **F10** **v0.4.1 改** collaboration_requests.status 不含 EXECUTING/COMPLETED/FAILED
- **F11** **v0.4.1 改** UI 渲染「执行中」从 agent_executions 投影（display_state）

### 7.2 E2E

- `e2e/E4-001-resolver-rank`
- `e2e/E4-002-decision-state-machine`
- `e2e/E4-003-timeout-reroute`
- `e2e/E4-004-all-timeout`
- `e2e/E4-005-mention-visibility`
- `e2e/E4-006-accept-creates-execution`（v0.4.1 改：E4 → E7 API 链）
- `e2e/E4-007-protocol-boundary`（v0.4.1 新）—— 验证 execution.result 不含 decision；collaboration.decision 不含 execution_id
- `e2e/E4-008-resolver-uses-concurrency`（v0.4.1 新）—— max_concurrency=1 时第二个请求不命中
- `e2e/E4-009-cancel-from-actor`（v0.4.1 新）—— 发起人 CANCELLED

## 8. 与其他 Epic 的关系

- **被依赖**：E7（Accept → Execution）/ E5（Need Context 触发搜索）/ E10（audit）
- **依赖**：E2（lifecycle + active_slots）/ E3（消息载体 + Trigger 提取）/ E6（permission）
- **冲突裁决**：CollaborationRequest status 与 Execution status 完全解耦（v0.4.1）；协议边界 `collaboration.*` vs `execution.*` 严格分离

## 9. 风险与开放问题

- **R1**：active_slots 跨实例一致性 → Redis INCR/DECR（与 presence 同一基础设施）
- **R2**：CANCELLED 触发条件（发起人 / admin / lifecycle 变更）→ V1 仅发起人；lifecycle=PAUSED 时已在 E2 触发 in-flight cancel
- **R3**：Execution display_state projection 延迟 → UI 短期内存缓存 + WS 实时推

## 10. 实施顺序（M4）

1. **v0.4.1 改** agents 表加 `max_concurrency` / `active_slots`
2. **v0.4.1 改** collaboration_requests.status 收敛（CANCELLED 新增 + EXECUTING/COMPLETED/FAILED 删除）
3. **v0.4.1 改** Resolver 调度改 lifecycle + active_slots
4. **v0.4.1 改** 协议拆分：collaboration.decision / execution.dispatch 分开
5. E5 / E7 联动 API（Orchestrator → Runtime）
6. CANCELLED REST 端点
7. P5 决策卡 projection 改造（v0.4.1）
8. E2E 套件
