# Detailed Design · Autonomous Delivery · 00 · Integration Map & Invariants

> ⚠️ **本目录已于 2026-09-10 迁至 `docs/design/future/`：V2 愿景设计，保留但不作为 M1–M9 实现依据。**
> 模型冻结提案已裁决：**WorkItem 是唯一业务对象；Mission / WorkUnit 移出主设计**。
> MVP 路径为 `PO 选 WorkItems → Grill/Planner 分析 → 执行计划 → Execution → Review/NeedsYou → WorkItem 更新`，
> 即 **Grill 先作为能力，不急着成为新的 Domain Entity**（不为它引入 Mission + WorkUnit 两套新的持久化容器）。
> Current Design 只有 `docs/design/detailed/00–09`；实现 Agent 不应把本目录内容当 Current spec。

> **配套**：PRD v0.4 / SYSTEM_DESIGN v0.3.2 / UI DS v0.6 / E1-E10 epic docs / `00-overview.md` 起的 9 份 detailed design / **v0.1 Architecture Proposal（5 层 33 节）**
> **范围**：把 v0.1 「Autonomous Delivery Architecture」落到现有 E1-E10 bounded feature 模型上，**锁边界** + 9 条关键 Invariants，避免 #2-#5 撞现有域时重蹈 v0.4 推倒覆辙。
> **前置**（Current Design，位于 `docs/design/detailed/`）：[00-overview.md](../../detailed/00-overview.md) / [01-single-agent-task-lifecycle.md](../../detailed/01-single-agent-task-lifecycle.md) / [04-resolver-and-routing.md](../../detailed/04-resolver-and-routing.md) / [06-workitem-provider-sync.md](../../detailed/06-workitem-provider-sync.md)

## 0. 文档结构

本目录是 **v0.1 Autonomous Delivery** 的详细设计集合。**先读本文（00）** 再读 #1-#4。

| # | 文档 | 关键问题 |
| --- | --- | --- |
| [00-integration-map.md](./00-integration-map.md) | **边界对照 + 9 Invariants + 域事件清单** | 怎么不撞 E1-E10 / 哪些不能动 |
| [01-mission-and-grill.md](./01-mission-and-grill.md) | **PO 选多 Story → Mission Grill → Briefs** | Mission 状态机 / Grill 流程 / Brief 字段 |
| [02-workunit-and-delivery-graph.md](./02-workunit-and-delivery-graph.md) | **WorkUnit + DAG + Dynamic Replanning** | WU 表 / 依赖来源 / split/merge 协议 |
| [03-scheduler-autonomy-policy.md](./03-scheduler-autonomy-policy.md) | **Scheduler + Autonomy Level + Project Policy** | 评分公式 / Level 0-3 / Policy 实体 |
| [04-topic-needsyou-and-assignment.md](./04-topic-needsyou-and-assignment.md) | **Topic + Needs You + Assignment Mode 3 态 + Handoff 协议** | 复用 Channel / OPEN_CLAIM / Handoff 走向 |

## 1. 现有 E1-E10 现状回顾（精简）

| E 域 | 核心实体 | 关键事实源 | 已稳定的边界 |
| --- | --- | --- | --- |
| E1 Identity & Workspace | users / orgs / teams / projects / project_members | `projects` | Project = 协作/知识/工作边界；**无** integration_backend 字段 |
| E2 Agent Registry | agents / credentials / agent_project_membership | `agents`（`lifecycle` + `activity`） | lifecycle=ACTIVE∩activity∈{AVAILABLE,THINKING} 才进 Resolver；v0.4.3 capacity 走 per-lease ZSET |
| E3 Channel & Timeline | channels / messages / channel_seq_counters / attachments | `messages`（projection 5 形态） | **单层 reply**；DECISION/MEMORY_REQUEST 形态只引 `entity_ref` |
| E4 Collaboration | collaboration_requests / decision_records / outbox_events | `collaboration_requests` | 状态收敛 PENDING/ACCEPTED/REJECTED/NEED_CONTEXT/UNRESOLVED/CANCELLED；**Execution 状态不镜像** |
| E5 Memory | memory_proposals / memory_items / memory_chunks | `memory_items`（Source 三件套强约束） | write_memory 走审批门禁；APPROVAL 走 Guard 视为 403，业务层 `policy.evaluate()` |
| E6 Authorization | permissions | `permissions`（7 键 × 3 态） | scope_type×scope_id×subject×perm_key UNIQUE；Guard 只决 ALLOW/DENY |
| E7 Agent Execution | agent_executions / execution_attempts / execution_events / execution_artifacts / outbox_events | `agent_executions`（v0.4.3：Execution 必须在 ACCEPT 后才创建） | 状态 PENDING/RUNNING/SUCCEEDED/FAILED/CANCELLED/TIMEOUT；CAS terminal + outbox |
| E8 Work Management Core | work_items / work_item_bindings / work_relations | `work_items`（v0.3.1 删 projections 单表化） | Project 同一 active binding 唯一（DB UNIQUE 强制） |
| E9 Work Management Integration | work_management_connections / work_sync_audit | `work_management_connections`（org_id + owner_user_id） | webhook 30 天 refresh；listStatuses(binding) 动态拉 |
| E10 Observability | audit_logs / notifications / llm_calls | `audit_logs` | append-only |

**关键协议边界**（v0.3.1 冻结，v0.4.3 验证）：
- E4 与 E7 消息名空间严格分离：`collaboration.*` vs `execution.*`
- execution_id 由 E4 → E7 API 产生（**不**由 Agent 自报）
- Terminal 状态必须 CAS（`status IN (PENDING, RUNNING)`）+ outbox 写同事务
- enode.id 全局去重（`terminal_envelope_id` 落表）
- ProviderKey = string + Registry 模式（零硬编码）
- Connection Org/Owner 级（**不**绑 Project，多 Project 共享）
- Slot 提前 reservation：Resolver 选中后立即 `tryAcquireSlot(pending_decision)`，Execution 阶段 promote 到 `execution` lease
- Per-lease ZSET（不是 EXPIRE 整个 key）—— 长任务靠 `renewExecutionLease` 续期

## 2. v0.1 引入的 5 个新抽象

| 抽象 | 目的 | 落地位置 |
| --- | --- | --- |
| **Mission** | PO 选多 Story 形成的 Delivery 目标 | 新表 `missions` + `mission_work_item_refs`（存 WorkItemRef[]） |
| **Requirement Brief** | 一次 Grill 收敛出的版本化需求规约 | 新表 `briefs` + `brief_revisions`（独立于 `memory_items`，可被引用） |
| **WorkUnit** | Agent 团队内部 Delivery Planning 单位（≠ WorkItem） | 新表 `work_units` + `work_unit_dependencies` |
| **Project Policy** | Mission 级别策略（review / handoff / escalation / execution） | 新表 `project_policies`（一个 Project 一份 policy_yml JSONB） |
| **Topic + Needs You** | Layer 1 协作会话 + 只显示真正需要 Human 决策 | **复用 `channels` + `messages`**，加 `messages.topic_id` + `topic_root_seq` + `messages.needs_human` 字段 |

**不引入**：WorkUnit 不引 Trigger / Channel / Memory 抽象；Mission 不引独立 execution 抽象。

## 3. 边界对照表（v0.1 ↔ E1-E10）

### 3.1 Mission / WorkUnit / Brief vs 现有 E 域

| v0.1 概念 | 不允许做 | 必须复用 |
| --- | --- | --- |
| **Mission** | ❌ 拥有 Story（Story 仍归 Provider 或 E8 Built-in）<br>❌ 直接创建 Execution<br>❌ 改写 `collaboration_requests` 协议 | ✅ 引用 `work_items`（Built-in 或 Provider 投影）<br>✅ 产生 OUTBOX event `mission.created` / `mission.replan_requested` → E4 收 → 派 CollaborationRequest |
| **Mission Grill** | ❌ 跨 Mission 共享 grill 状态<br>❌ 在 Grill 阶段改 WorkItem.status | ✅ Grill 阶段写 `briefs`（status=GRILLING）<br>✅ PO 决策落 `decision_records`（E4 既有事实源） |
| **Requirement Brief** | ❌ 复制 `memory_items.content` 字段<br>❌ 取代 memory approval 门禁<br>❌ 写进 messages.content（仍走 memory_items 引用） | ✅ Brief = `briefs` 表（独立 entity）<br>✅ 引用 `memory_items`（provenanced sources）<br>✅ versioned by `brief_revisions` |
| **WorkUnit** | ❌ 1:1 替代 WorkItem<br>❌ 强制 promote 为 WorkItem（可选）<br>❌ 直接创建 Execution | ✅ `work_units.work_item_ref` optional 引用 `work_items`<br>✅ WU 完成时产生 OUTBOX event `work_unit.completed` → E4 → 派 CollaborationRequest（IMPLEMENTATION / REVIEW / FIX）<br>✅ Promote 时由 WU 创建 WorkItem（走 E8 work-management API，**不**走 E7） |
| **Project Policy** | ❌ 覆盖 `permissions` 表语义<br>❌ 改写 Permission effect 三态 | ✅ 独立 `project_policies` 表（policy_yml JSONB）<br>✅ Policy 评估走 `policy.evaluate()` 业务层（不通过 E6 Guard）<br>✅ Policy 改变产生 OUTBOX event `policy.changed` |
| **Topic** | ❌ 取代 `channels`<br>❌ 引入独立 Topic 表 | ✅ **复用** `channels` + `messages`，加字段<br>✅ Topic = `messages.topic_id` 聚类（同一 root message_seq）<br>✅ Topic close = 设 `messages.topic_closed_at`（不删消息） |
| **Needs You** | ❌ 取代 `notifications`<br>❌ 引入新一级权限 | ✅ `notifications` 加 `category='NEEDS_HUMAN'` + `urgency` 字段<br>✅ UI 「Needs You」页 = `notifications` 按 category=NEEDS_HUMAN 过滤的视图 |
| **Assignment Mode = OPEN_CLAIM** | ❌ 让 Agent 自创 CollaborationRequest<br>❌ 旁路 E4 Resolver | ✅ `work_offers` 表（Open Work 入口）<br>✅ Agent claim → E4 创建 `collaboration_requests`（target_agent_id=claimer, assignment_mode=OPEN_CLAIM）<br>✅ Resolver 不参与 OPEN_CLAIM（Agent 主动 claim） |
| **Handoff 协议** | ❌ Agent 直接写 `collaboration_requests`<br>❌ Agent 调 E7 API | ✅ Agent 写 OUTBOX event `handoff.requested`（aggregate_type='agent_execution' / 'work_unit'）<br>✅ E4 Outbox Worker 拾取 → 创建 `collaboration_requests`（assignment_mode=DIRECT, target_agent_id=resolved_agent） |

### 3.2 数据所有权

| 表 | 拥有方 | 写入规则 |
| --- | --- | --- |
| `missions` | Delivery (新域) | Mission Planner 服务独写 |
| `mission_work_item_refs` | Delivery | Mission Planner 独写（建/拆 Mission 时） |
| `briefs` | Delivery + E5 | Mission Grill 写；引用 `memory_items`；E5 memory_items 独立 lifecycle |
| `brief_revisions` | Delivery | Brief 每次 versioned 更新独写 |
| `work_units` | Delivery | Mission Planner 独写；状态机：PLANNED→READY→IN_PROGRESS→COMPLETED/BLOCKED/CANCELLED |
| `work_unit_dependencies` | Delivery | Mission Planner 独写；含 `source: EXPLICIT/AGENT_INFERRED/HUMAN_CONFIRMED` + `confidence` |
| `project_policies` | Delivery (新) | Project owner 经 REST PATCH；policy_yml JSONB schema 校验 |
| `work_offers` | E4 (扩展) | OPEN_CLAIM 模式下 E4 写；Agent claim 时 CAS |
| `outbox_events`（新增 event_type） | 各域 | 见 §6 域事件清单 |

**E1-E10 现有表不动**。新表全部加 `created_at` / `updated_at` / `id UUID PRIMARY KEY`。

### 3.3 API 路由命名（与现有 NestJS 风格一致）

```
# Delivery (新)
POST   /api/v1/missions
GET    /api/v1/missions/:id
POST   /api/v1/missions/:id/grill                  # 触发 Mission Grill
POST   /api/v1/missions/:id/start
POST   /api/v1/missions/:id/replan

# Brief
GET    /api/v1/briefs/:id
GET    /api/v1/briefs/:id/revisions

# WorkUnit
GET    /api/v1/missions/:id/work-units
POST   /api/v1/work-units/:id/split                # 拆
POST   /api/v1/work-units/:id/merge                # 合
POST   /api/v1/work-units/:id/dependency           # 增依赖
DELETE /api/v1/work-units/:id/dependency/:dep_id   # 删依赖

# Policy
GET    /api/v1/projects/:id/policy
PATCH  /api/v1/projects/:id/policy

# E3 扩展
PATCH  /api/v1/messages/:id   # 加 topic_id / topic_closed_at / needs_human 字段

# E4 扩展
POST   /api/v1/work-offers
POST   /api/v1/work-offers/:id/claim
```

## 4. 关键 Invariants（9 条）

> **所有 v0.1 详细设计必须遵守**。违反任一条 = 设计 bug。

### I-1 Mission ≠ WorkItem

**Mission 永远不** owns Story 字段；只通过 `mission_work_item_refs(work_item_id, ref_kind, ref_role)` 引用。

- 禁止：`missions.story_ids UUID[]` 直接外键
- 禁止：Mission Planner 直接 UPDATE `work_items.status`
- 正确：Mission 状态变更通过 OUTBOX event → E8 Work Management Core 处理 WorkItem.status

### I-2 WorkUnit ≠ WorkItem；WorkUnit 1:N WorkItem optional promote

**WorkUnit 是 Agent 团队内部 Delivery Planning 单位**。WorkItem 是 PO / Provider 视角的工作单位。

- WorkUnit 完成 ≠ WorkItem 完成（除非 promote）
- 禁止：1 个 WorkUnit 直接写 work_items 字段
- 正确：Promote WorkUnit → WorkItem 走 E8 work-management API（创建 WorkItem）；WU 引用 `promoted_work_item_id` optional

### I-3 WorkUnit != Execution

**WorkUnit 是 Delivery 单位；Execution 是 Runtime 事实源**。

- WU 状态变更**不**改 `agent_executions.status`
- 禁止：`work_units.status` 字段存 `RUNNING/SUCCEEDED` 等 Execution 状态
- 正确：WU 状态枚举 `PLANNED / READY / IN_PROGRESS / COMPLETED / BLOCKED / CANCELLED`；与 Execution 状态机独立

### I-4 CollaborationRequest != Execution

（**已存在 v0.3.1，v0.4.3 强化**）

- 禁止：`collaboration_requests.status` 包含 `EXECUTING / COMPLETED / FAILED` 等 Execution 状态
- 正确：UI projection 从 `collaboration_requests.status` + 关联 `agent_executions.status` 计算 `display_state`

### I-5 Execution status 不 mutate CollaborationRequest decision status

（**v0.4.3 P1-3**）

- ACCEPTED 后用户取消 Execution → collab 仍 ACCEPTED（抹除历史是 bug）
- 仅 `PENDING` 状态可标 `CANCELLED`

### I-6 Topic 是 Channel 内的协作会话，**不**新加 entity

- 禁止：`topics` 新表
- 正确：`messages.topic_id` + `messages.topic_root_seq`（同一 root 聚类）
- Topic 创建 = 任何 member 在 Channel 发 root message（topic_id=NULL → 自动生成 UUID）
- Topic close = 设 root message 的 `topic_closed_at`

### I-7 Open Claim 仍走 E4 CollaborationRequest

- 禁止：Agent claim `work_offers` 时直接创建 Execution
- 正确：Agent claim → CAS `work_offers.status` → 创建 `collaboration_requests`（assignment_mode=OPEN_CLAIM, target_agent_id=claimer）→ 走 E4 → E7 标准流程

### I-8 Handoff 协议 Agent 不直接创建 CollaborationRequest

- 禁止：Agent 进程写 `collaboration_requests` 表
- 正确：Agent 写 OUTBOX event `handoff.requested`（payload 含 `target_capabilities` / `target_agent_id?` / `context_refs`） → E4 Outbox Worker 拾取 → 创建 CollaborationRequest

### I-9 Memory approval 不走 Guard

（**v0.4.3 已存在**）

- `write_memory=REQUIRE_APPROVAL` 走 E6 Guard 视为 403
- 业务层显式 `policy.evaluate('write_memory', scope)` → 走 E5 memory_proposals

## 5. 域事件清单（v0.1 新增 event_type）

> 全部经 OUTBOX 投递。**不**直接调下游 API。

| aggregate_type | event_type | payload 关键字段 | 投递目标 |
| --- | --- | --- | --- |
| `mission` | `mission.created` | mission_id, work_item_refs[] | E4 监听 → 启动 Grill |
| `mission` | `mission.grill_completed` | mission_id, briefs[], needs_human_questions[] | E4 监听 → 通知 PO |
| `mission` | `mission.started` | mission_id, policy_snapshot | E4 监听 → 激活 Scheduler |
| `mission` | `mission.replan_requested` | mission_id, reason, affected_work_unit_ids[] | Delivery 监听 → 触发 Planner |
| `mission` | `mission.completed` | mission_id, stats | E4 监听 → 通知 PO |
| `work_unit` | `work_unit.ready` | work_unit_id, mission_id, required_capabilities | E4 监听 → 发布 work_offer（OPEN_CLAIM 模式）|
| `work_unit` | `work_unit.claimed` | work_unit_id, work_offer_id, agent_id | E4 监听 → 创建 CollaborationRequest |
| `work_unit` | `work_unit.completed` | work_unit_id, artifacts[], execution_id | E4 监听 → 释放 work_offer + 解锁依赖 |
| `work_unit` | `work_unit.blocked` | work_unit_id, reason, needs_human | E4 监听 → 创建 Needs You 通知 |
| `work_unit` | `work_unit.split` | parent_work_unit_id, child_work_unit_ids[] | Delivery 监听 → 更新 DAG |
| `work_unit` | `work_unit.merged` | source_work_unit_ids[], target_work_unit_id | Delivery 监听 → 更新 DAG |
| `handoff` | `handoff.requested` | from_execution_id\|work_unit_id, target_capabilities, target_agent_id?, context_refs | E4 监听 → 创建 CollaborationRequest |
| `brief` | `brief.revision_created` | brief_id, revision_no, content_diff_ref | E5 监听 → memory_items 同步投影 |
| `policy` | `policy.changed` | project_id, policy_yml, effective_at | E4 监听 → 重新评估 in-flight decisions |
| `topic` | `topic.opened` | channel_id, root_message_seq, topic_id | E3 监听 → WS 广播 |
| `topic` | `topic.needs_human` | topic_id, work_unit_id?, question, recommendation | E10 监听 → 写 notifications |

## 6. 跨域事件时序（Mission → WorkUnit → Execution 完整链）

```
T+0   PO POST /missions { work_item_refs: [RF-201, RF-203, RF-209] }
      ├─ Delivery: INSERT missions (status=CREATED)
      └─ OUTBOX: mission.created

T+1   E4 Outbox Worker → mission.created
      └─ Delivery Mission Planner: 启动 Grill
         ├─ 拉每个 WorkItem 上下文（E8）
         ├─ 调 LLM 分析 cross-story 依赖
         └─ 写 briefs (status=GRILLING) + needs_human_questions[]

T+2   Mission Planner: Grill 收敛
      └─ 写 briefs (status=GRILLED) + 写 brief_revisions v1

T+3   PO GET /missions/:id → 看到 3 needs_human_questions
      └─ PO 回答 → 落 decision_records（E4 事实源）

T+4   PO POST /missions/:id/start
      ├─ Delivery: 写 mission_work_item_refs[] + project_policies snapshot
      ├─ UPDATE missions.status=PLANNING
      └─ OUTBOX: mission.started

T+5   E4 Outbox Worker → mission.started
      └─ Delivery Planner: 建 work_units + work_unit_dependencies
         ├─ 每个 Story 拆 ≥1 WorkUnit
         ├─ 依赖来源标记 EXPLICIT/AGENT_INFERRED/HUMAN_CONFIRMED
         └─ OUTBOX: work_unit.ready ×N（无依赖的 WU）

T+6   E4 Outbox Worker → work_unit.ready (for each)
      ├─ 看 mission.policy.assignment_mode
      ├─ DIRECT  → E4 选指定 Agent（capability 匹配）
      ├─ ROUTED  → E4 Resolver 走标准评分
      └─ OPEN_CLAIM → INSERT work_offers → WS 广播给 eligible Agents

T+7   Agent A claim work_offer_1
      ├─ CAS work_offers.status=CLAIMED
      ├─ E4: INSERT collaboration_requests (assignment_mode=OPEN_CLAIM, target_agent_id=A)
      └─ OUTBOX: work_unit.claimed

T+8   E4: Resolver 不参与（target_agent_id=A），直接 tryAcquireSlot(pending_decision)
      └─ 推 collaboration.request 给 A

T+9   A 推 collaboration.decision: ACCEPT
      ├─ E4: 写 decision_records + CAS collab ACCEPTED + OUTBOX collab.accepted
      └─ E7 Outbox Worker → 创建 agent_executions
         └─ 推 execution.dispatch 给 A

T+10  A 执行 → execution.event ×N
T+11  A 推 execution.result: SUCCEEDED
      ├─ E7: CAS agent_executions SUCCEEDED + OUTBOX execution.completed
      └─ E4 Outbox Worker → 释放 lease + 写 messages（AGENT_OUTPUT 投影）
         └─ Outbox: work_unit.completed

T+12  Delivery Outbox Worker → work_unit.completed
      ├─ 标 WU.status=COMPLETED
      ├─ 解锁依赖 WU（readiness 重算）
      ├─ 若 WU.promote_to_work_item → E8 createWorkItem（走 Provider）
      └─ 新解锁的 WU → OUTBOX work_unit.ready ×M

T+13  Agent B claim 新 WU（重复 T+7）
... (循环 T+7-T+12)

T+20  最后 1 个 WU.completed → mission.completed
      └─ 通知 PO

T+21  Agent A 推 handoff.requested: REVIEW (可选, 走 project_policies)
      └─ E4 Outbox Worker → 创建 CollaborationRequest (target_capabilities=['review','database'], assignment_mode=ROUTED)
      └─ Resolver 选 Reviewer C
      ... (review / fix 循环)
```

## 7. 与现有 9 份 detailed design 的关系

| 现有 detailed | v0.1 引入的影响 | 备注 |
| --- | --- | --- |
| 00-overview | Mission 域加进 bounded domain 列表 | `delivery` bounded domain 实体化（Mission Planner Service） |
| 01-single-agent-task-lifecycle | T+12 增加 work_unit.completed 后的解锁 + OUTBOX work_unit.ready 步骤 | 不改主时序；插入 delivery 监听点 |
| 02-interrupt-and-cancel | Cancel 仍可；Cancel Mission = 标所有 in-flight WU 为 CANCELLED + 取消关联 Execution | Mission cancel 是高层动作，下沉到 WU/Execution |
| 03-ws-connection-and-resume | 不影响（WS 协议不动） | — |
| 04-resolver-and-routing | Resolver 加 3 态分支（DIRECT/ROUTED/OPEN_CLAIM） | 见 #5 [04-topic-needsyou-and-assignment.md](./04-topic-needsyou-and-assignment.md) |
| 05-memory-approval-flow | Brief 内容引用 memory_items；审批门禁复用 | Brief 写入不绕过 E5 approval |
| 06-workitem-provider-sync | WU.promote_to_work_item 走 E8 createWorkItem API | Provider 同步仍归 E9 |
| 07-permission-and-approval-orchestration | Policy 评估**不**经 E6 Guard | Policy 走 `policy.evaluate()` 业务层 |
| 08-error-and-retry | WU 失败 = 触发 work_unit.blocked + 创建 Needs You 通知 | WU 有重试上限（默认 3） |
| 09-implementation-checklist | 加 M10-M12 实施顺序 | 见 §8 |

## 8. 实施顺序（与 M1-M9 衔接）

| 阶段 | 交付 | 与 v0.1 关系 |
| --- | --- | --- |
| M1-M9 | E1-E10 基础 | **不**依赖 v0.1 |
| M10 | Delivery 域基础 | `missions` / `mission_work_item_refs` 表 + Mission Planner Service + Briefs CRUD |
| M11 | WorkUnit + DAG | `work_units` / `work_unit_dependencies` + Planner + Replan |
| M12 | Scheduler + Policy + Autonomy | `project_policies` + Scheduler + Level 0-3 wiring |
| M13 | Topic + Needs You + OPEN_CLAIM + Handoff | E3 扩字段 + E4 扩 assignment_mode + work_offers |
| V1+ | 多 Provider / 跨 Org Mission | 复用 E9 |

**M10-M13 可与 M1-M9 并行**（无强依赖）。M10 必须先于 M11（Brief 是 WU 的输入）。

## 9. 反模式（v0.1 必须避免）

| 反模式 | 后果 | 正确做法 |
| --- | --- | --- |
| **Mission 直接改 WorkItem.status** | 跳过 E8 work-management 业务规则；Provider 同步可能冲突 | Mission → OUTBOX → E8 收 → 走标准 updateWorkItem |
| **WorkUnit 1:1 替换 WorkItem** | PO 视角的 Story 被 Agent 视角覆盖 | WU 是 internal planning unit，WorkItem 仍是 PO 视角 |
| **WorkUnit 直接创建 Execution** | 绕过 E4 → E7 协议；破坏 decision 事实源 | WU → OUTBOX work_unit.claimed → E4 创建 CollaborationRequest |
| **Handoff 让 Agent 直接调 E7** | 协议边界破坏；outbox 兜底失效 | Agent 写 OUTBOX handoff.requested；E4 Worker 创建 CR |
| **OPEN_CLAIM 让 Agent 自建 CR** | 绕过 Resolver 和 E4 协议 | Agent claim work_offer → E4 建 CR（target_agent_id=claimer） |
| **Topic 引入新表** | 与 E3 channel 重复 | 复用 channel + messages 字段 |
| **Project Policy 走 E6 Guard** | REQUIRE_APPROVAL 静默放行 | Policy 走业务层 `policy.evaluate()` |
| **Mission Grill 复制 memory_items.content** | 重复事实源；approval 路径破坏 | Brief 引 `memory_items.id[]` 为 sources |
| **Scheduler 直接选 Agent 跳过 E4** | 协议重复；capacity 兜底失效 | Scheduler 选 WorkUnit + 发布 work_offer；Agent claim → E4 |
| **WU 状态枚举含 EXECUTING/SUCCEEDED** | 与 Execution 状态机混淆 | WU 状态机独立：PLANNED/READY/IN_PROGRESS/COMPLETED/BLOCKED/CANCELLED |

## 10. e2e 验收点（Integration Map 层级）

```
e2e/autonomous-delivery/integration/
  test_001_mission_creation_does_not_touch_workitems.json
    Given PO POST /missions { work_item_refs: [W1, W2] }
    Then missions 落 1 行，mission_work_item_refs 落 2 行
    And   work_items 表无任何 UPDATE
    And   OUTBOX 投递 mission.created

  test_002_brief_references_memory_not_copies.json
    Given Mission Grill 收敛
    Then briefs 落 1 行，brief_revisions 落 1 行
    And   brief_revisions.content 引用 memory_items.id[]（不复制 content）
    And   无 write_memory=REQUIRE_APPROVAL 触发

  test_003_workunit_promote_optional.json
    Given WorkUnit WU-1 完成
    When  policy.promote_to_work_item = true
    Then  E8 createWorkItem 被调，work_items 落 1 行
    And   WU.promoted_work_item_id 落表
    When  policy.promote_to_work_item = false
    Then  E8 createWorkItem **不**被调
    And   WU 仍 COMPLETED

  test_004_open_claim_goes_through_e4.json
    Given WU-1 status=READY, assignment_mode=OPEN_CLAIM
    When  Agent A claim work_offer
    Then  CAS work_offers CLAIMED
    And   E4 创建 collaboration_requests (assignment_mode=OPEN_CLAIM, target_agent_id=A)
    And   E4 → E7 走标准 dispatch 路径
    And   Agent A **不**直接调 E7 API

  test_005_handoff_via_outbox.json
    Given Execution E1 SUCCEEDED
    When  Agent A 推 handoff.requested: REVIEW
    Then  Agent A **不**写 collaboration_requests 表
    And   OUTBOX handoff.requested 投递
    And   E4 Outbox Worker 创建 CollaborationRequest (assignment_mode=DIRECT/ROUTED)

  test_006_topic_reuses_channel.json
    Given Channel C, User 发 root message
    Then  messages.topic_id 自动生成
    And   无 topics 新表
    And   User reply 同 topic_id 自动归入

  test_007_policy_does_not_bypass_e6.json
    Given project_policies.review.require_review = false
    When  WU-1 完成
    Then  E6 Guard 仍按 permissions 表 effect 评估
    And   policy 走 policy.evaluate() 业务层
    And   两者**不**互相覆盖

  test_008_mission_replan_preserves_completed.json
    Given Mission M with 5 WU, 3 COMPLETED, 2 IN_PROGRESS
    When  PO 触发 replan (cross-story 矛盾)
    Then  COMPLETED WU **不**重置
    And   IN_PROGRESS WU 标 BLOCKED 等决策
    And   新 WU 创建（拆/合）

  test_009_invariance_mission_status_does_not_affect_workitem.json
    Given Mission M CANCELLED
    When  查询关联 WorkItems
    Then  WorkItem.status 仍原值（Mission 不直接改 WorkItem.status）
    And   WorkItem 后续由 PO 手动处理
```

## 11. 关键不变量（在 9 条 Invariant 之上的强约束）

- **Mission 创建不触发 E5 approval**（Mission Planner 写 briefs 不走 write_memory）
- **Brief 修订不删除旧 revision**（append-only `brief_revisions`）
- **WorkUnit 拆/合不删除原 WU**（拆 = 新 WU 引 parent；合 = source WU 标 MERGED）
- **Project Policy 改变不重置 in-flight decision**（policy.changed 触发 E4 重新评估，但已 ACCEPTED 的 CR 不重评）
- **OPEN_CLAIM work_offer TTL 1h**（默认；超期自动释放，WU 重新发布）

## 12. 与 AgentBoard / Multica 关系（v0.1 §30-31 落地）

- **AgentBoard 仍可吸收**：Durable execution、Review loop、Retry、Human intervention、Recovery、Artifacts、Events 模式（v0.3.2 已实现）
- **不复用**：AgentBoard 的 fixed Task / Stage workflow
- **Multica 差异**（v0.1 §31）：MateOS 突出 Channel/Topic + Mission + Dynamic Delivery Graph + Open Claim + Needs You + Pluggable Work Management + Provenance-first Memory
- **Positioning**：「从 Agent Project Management 提升到 AI-native Team Collaboration + Autonomous Delivery」
