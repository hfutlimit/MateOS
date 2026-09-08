# E4 · Collaboration & Routing

| 字段 | 值 |
| --- | --- |
| Epic ID | E4 |
| 标题 | Collaboration & Routing |
| 阶段 | MVP（M4） |
| 上游 | PRD v0.4 §5 FR-4 / FR-5 / SYSTEM_DESIGN v0.3 §4.1 / §4.2 / §5.2 collaboration_requests / UI DS v0.5 §5.1 |
| 下游 | E3（消息载体 + Trigger 提取）、E7（Accept → 创建 Execution）、E10（audit） |
| 状态 | Draft（v0.4 架构修订版） |

## 1. 背景与动机

v0.4 的核心架构变化：**CollaborationRequest 是一等实体**，不再把 Mention 当 collaboration request。

```
v0.3 旧：Message → Mention → Decision → Task placeholder
v0.4 新：Trigger → CollaborationRequest → Decision → Execution
                └─ Mention
                └─ WorkItem 状态变化
                └─ API
                └─ Automation
```

**为什么改**：
- Mention 是 trigger 类型之一，不应该代表整个协作流程
- WorkItem 状态变化也能产生协作（如"@backend 修复 assignee=你"）
- 未来 API / Automation 也都能触发协作
- 把 CollaborationRequest 拉成独立实体后，三大 lifecycle（Collaboration / WorkItem / Execution）才能真正解耦

## 2. 范围

### 2.1 In Scope

- **v0.4 新增** 4 种 Trigger：MENTION / WORK_ITEM / API / AUTOMATION
- **v0.4 新增** CollaborationRequest 一等实体
- Mention Resolver（lifecycle=ACTIVE ∩ activity ≠ OFFLINE/ERROR/WORKING/WAITING_CONTEXT）
- Capability ranking
- Decision 状态机（ACCEPT / REJECT / NEED_CONTEXT；DELEGATE 留 V2）
- Analysis 三件套（capability / context_score / permission）
- 超时重路由（60s 默认）
- @all 投递（带成本预警）
- 决策卡片 projection（**只引 decision_ref**）

### 2.2 Out of Scope

- Delegate 实际路由（V2）
- @all 仲裁阈值动态调整（M6 之后）
- 决策回放（V2+）

## 3. 数据模型

```sql
-- triggers：原始触发记录（事实源）
CREATE TABLE triggers (
  id              UUID PRIMARY KEY,
  trigger_type    TEXT NOT NULL CHECK (trigger_type IN ('MENTION','WORK_ITEM','API','AUTOMATION')),
  trigger_ref     JSONB NOT NULL,        -- {channel_id, message_seq, work_item_id, api_call_id, ...}
  from_actor_type TEXT NOT NULL,
  from_actor_id   UUID NOT NULL,
  captured_at     TIMESTAMPTZ DEFAULT now()
);

-- collaboration_requests（v0.4 一等实体）
CREATE TABLE collaboration_requests (
  id                      UUID PRIMARY KEY,
  trigger_id              UUID NOT NULL REFERENCES triggers(id),
  trigger_type            TEXT NOT NULL,   -- 冗余便于 query
  trigger_ref             JSONB NOT NULL,
  request_kind            TEXT NOT NULL
                          CHECK (request_kind IN ('MESSAGE_RESPONSE','WORK_ITEM_EXECUTION','API_CALL','AUTOMATION_RUN')),
  from_actor_type         TEXT NOT NULL,
  from_actor_id           UUID NOT NULL,
  target_agent_id         UUID REFERENCES agents(id),   -- 显式指定时填
  required_capabilities   JSONB NOT NULL DEFAULT '[]',
  context_refs            JSONB NOT NULL DEFAULT '{}',  -- {channel_id, message_seq, memory_refs, work_item_ref}
  status                  TEXT NOT NULL DEFAULT 'PENDING'
                          CHECK (status IN ('PENDING','ACCEPTED','REJECTED','NEED_CONTEXT','EXECUTING','COMPLETED','FAILED','UNRESOLVED')),
  target_execution_id     UUID,            -- ACCEPTED 后回填
  work_item_ref           JSONB,           -- 可选关联 WorkItem
  deadline_s              INT NOT NULL DEFAULT 600,
  idempotency_key         TEXT UNIQUE,
  created_at              TIMESTAMPTZ DEFAULT now(),
  resolved_at             TIMESTAMPTZ
);

-- decision_records（事实源）
CREATE TABLE decision_records (
  id                UUID PRIMARY KEY,
  collaboration_request_id UUID NOT NULL REFERENCES collaboration_requests(id),
  agent_id          UUID NOT NULL REFERENCES agents(id),
  decision          TEXT NOT NULL CHECK (decision IN ('ACCEPT','REJECT','NEED_CONTEXT','DELEGATE')),
  reason            TEXT,
  needs             JSONB,                -- NEED_CONTEXT 时：["近 7 天失败率", "当前配置"]
  analysis          JSONB NOT NULL,       -- {capability: bool, context_score: 0-100, permission: bool}
  delegate_to       UUID,                 -- DELEGATE 时
  expires_at        TIMESTAMPTZ NOT NULL,
  decided_at        TIMESTAMPTZ,
  created_at        TIMESTAMPTZ DEFAULT now()
);
```

### 3.1 状态机（v0.4 修订）

```
triggers INSERT
  ↓
collaboration_requests.status = PENDING
  ↓
Resolver（如未指定 target_agent_id）
  ① 硬过滤：lifecycle=ACTIVE ∩ activity ∈ {AVAILABLE, THINKING} ∩ permission 允许
  ② Capability ranking
  ③ 产出 Top-N 候选
  ↓
派发到 target_agent
  ① 写 decision_records
  ② WS 推 collaboration_request.resolved
  ③ decision:
     ├─ ACCEPT → 创建 agent_executions（E7），status=ACCEPTED → EXECUTING → COMPLETED
     ├─ REJECT → 通知发起人
     └─ NEED_CONTEXT → 等待补齐
PENDING 超 60s → 取下一候选；全部超时 → status=UNRESOLVED + 通知
```

## 4. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET | `/collaboration-requests/:id` | 查询 | 关联成员 |
| GET | `/collaboration-requests?status=&trigger_type=&from_actor=` | 列表 | login |
| POST | `/collaboration-requests`（内部） | 创建 | service |
| POST | `/decisions`（内部） | Agent 上报 | agent token |
| GET | `/triggers?from_actor=` | 列表 | login |

### 4.2 WebSocket

- `collaboration_request.created`（v0.4 新命名）
- `collaboration_request.resolved`（v0.4 新命名）— 替代 v0.3 的 `mention.resolved`
- `decision.proposed`
- `collaboration_request.unresolved`

## 5. 关键流程

### 5.1 Trigger → CollaborationRequest 全链路

```
Trigger 来源：
1. MENTION：消息发送时 E3 提取 mention（@人 / @能力组 / @all），orchestrator 写 trigger → collaboration_request
2. WORK_ITEM：WorkItem 状态变化（assignee 变更、status 变更）触发 → v0.4 新增（V1+ 起启用，MVP 简化为仅 MENTION）
3. API：REST POST /collaboration-requests 携带 from_actor / required_capabilities
4. AUTOMATION：cron / 规则触发（V1 简化：MVP 暂不实现，E10 时加）

Resolver（lifecycle=ACTIVE ∩ activity ∈ {AVAILABLE, THINKING}）：
  score = 0.6 * capability_match + 0.25 * (1 - load) + 0.15 * accept_rate_30d
  Top-N = 3（可配）
```

### 5.2 Decision → Execution 联动（v0.4 改）

```
Agent 上报 decision=ACCEPT
  → decision_records 写
  → collaboration_request.status = ACCEPTED
  → 创建 agent_executions（E7）
    - input = collaboration_request 快照
    - context_refs = collaboration_request.context_refs
    - work_item_ref = collaboration_request.work_item_ref（optional）
  → collaboration_request.target_execution_id = execution.id
  → E4 推 EXECUTING → COMPLETED 状态
  → 消息流：写一条 DECISION 投影消息（content.decision_ref = decision_records.id）+ 一条 AGENT_OUTPUT 投影消息（content.execution_ref）
```

### 5.3 Decision 超时重路由

同 v0.3 逻辑。

### 5.4 @all 投递

仅 lifecycle=ACTIVE 的 Agent；6 态过滤排除；带成本预警。

## 6. UI

- 决策卡片从 `decision_records` 投影（v0.4 改）—— 包含三格 analysis
- Resolver 命中结果气泡（v0.4 保持 v0.3 设计）：消息流 mention 胶囊 hover 展开候选 + 分数
- @all 预警文案同 v0.3

## 7. 验收标准

### 7.1 功能

- **F1** Mention 触发 → collaboration_request 写入（带 trigger_id + trigger_type=MENTION）
- **F2** Resolver 过滤 lifecycle=ACTIVE ∩ activity ∈ {AVAILABLE, THINKING}
- **F3** Decision Accept → 创建 agent_executions（E7）— collaboration_request.target_execution_id 回填
- **F4** Decision 分析三件套必填（capability / context_score / permission）
- **F5** Need Context 决策必须带 needs[]
- **F6** PENDING 60s 后未响应 → 重路由
- **F7** 全部超时 → status=UNRESOLVED + 通知
- **F8** 消息流 DECISION 形态的 content 只引 `decision_ref`，不复制 analysis（v0.4 改）
- **F9** **v0.4 新增** WorkItem 状态变化能触发 collaboration_request（V1+ 启用，MVP 留接口）
- **F10** **v0.4 新增** API 端点能创建 collaboration_request

### 7.2 E2E

- `e2e/E4-001-resolver-rank`
- `e2e/E4-002-decision-state-machine`
- `e2e/E4-003-timeout-reroute`
- `e2e/E4-004-all-timeout`
- `e2e/E4-005-mention-visibility`
- `e2e/E4-006-decision-acceptance-creates-execution`（v0.4 新）
- `e2e/E4-007-trigger-from-api`（v0.4 新）

### 7.3 非功能

- Resolver P95 < 2s
- collaboration_request.resolved WS 广播 P95 < 500ms

## 8. 与其他 Epic 的关系

- **被依赖**：E7（Accept → 创建 Execution）/ E5（Need Context 触发记忆搜索）/ E8（WorkItem 状态变化 → Trigger 路径 V1+ 启用）/ E10（audit）
- **依赖**：E2（lifecycle+activity）/ E3（消息载体）/ E6（permission）
- **冲突裁决**：CollaborationRequest 一等实体（PRD v0.4 §5 FR-4 / SD v0.3 §4.1 / §5.2）

## 9. 风险与开放问题

- **R1**：WorkItem → Trigger 的实现时机（V1 简化为仅 MENTION；V1+ 启用）
- **R2**：API 创建 collaboration_request 的鉴权（V1 限定 user / agent；V2 加 service account）
- **R3**：Automation 触发路径（M6 之后实现）
- **R4**：Mention Trigger 在写消息时同步 vs 异步？→ 写消息事务内同步写 trigger；Resolver 异步（消息发送永远同步成功）

## 10. 实施顺序（M4）

1. triggers / collaboration_requests / decision_records 表
2. 消息流 mention 提取 → trigger 写入 → collaboration_request 同步
3. Resolver Worker（lifecycle × activity 过滤）
4. 决策上报端点 + 状态机
5. **v0.4 新增** Accept → agent_executions 联动
6. 超时扫描 + 重路由
7. **v0.4 新增** API 创建 collaboration_request 端点
8. P5 决策卡 projection 改造
9. E2E 套件
