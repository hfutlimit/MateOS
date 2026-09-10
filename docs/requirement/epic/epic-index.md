# MateOS Epic 总览

> 详细需求见同目录下 `E1-...md` ~ `E10-...md`。本文档提供全局索引与依赖关系。
>
> **v0.4 架构重切（2026-09-08）**：
> - 删除 AgentBoard execution / WorkItem backend 切换 / 多个复合概念
> - 新增 Work Management 域（独立）+ Agent Execution domain（durable，不只是 Connector）
> - CollaborationRequest 提为一等实体（Trigger 多种来源汇聚）
> - Capability × Permission 单一事实源（删 `can_execute` / `can_review`）
> - Agent lifecycle × activity 拆分
> - Permission effect 改 `REQUIRE_APPROVAL`
> - 消息流改 projection（5 形态 UI 保留，事实源在 decision_records / memory_proposals / agent_executions）

## Epic 清单（v0.4：10 个）

| Epic | 标题 | 阶段 | 主依赖 | 文档 |
| --- | --- | --- | --- | --- |
| **E1** | Identity & Workspace | S1（能力域 M1） | — | [E1-identity-workspace.md](./E1-identity-workspace.md) |
| **E2** | Agent Registry | S1（能力域 M3a/M3b） | E1 | [E2-agent-registry.md](./E2-agent-registry.md) |
| **E3** | Channel & Timeline | S1（能力域 M2） | E1 | [E3-channel-timeline.md](./E3-channel-timeline.md) |
| **E4** | Collaboration & Routing | S1（能力域 M4） | E2, E3, E6 | [E4-collaboration-routing.md](./E4-collaboration-routing.md) |
| **E5** | Shared Context & Memory | S2（能力域 M5） | E1, E3, E6 | [E5-shared-memory.md](./E5-shared-memory.md) |
| **E6** | Authorization & Approval | S1（随 E4；能力域 M4） | E1 | [E6-authorization-approval.md](./E6-authorization-approval.md) |
| **E7** | Agent Execution | S1（能力域 M8） | E2, E4 | [E7-agent-execution.md](./E7-agent-execution.md) |
| **E8** | Work Management Core | S3（能力域 M6） | E1, E6 | [E8-work-management-core.md](./E8-work-management-core.md) |
| **E9** | Work Management Integrations | V1+ | E8 | [E9-work-management-integrations.md](./E9-work-management-integrations.md) |
| **E10** | Observability & Operations | S1（随主链路；能力域 M9） | 全部 | [E10-observability-operations.md](./E10-observability-operations.md) |

## 实施顺序（S1/S2/S3 竖切，对齐 SYSTEM_DESIGN v0.9 §11 与 detailed/09 §1）

```
S1 ──► E1 (Identity & Workspace) ──► E3 (Channel & Timeline) ──► E2 (Agent Registry)
       ──► E6 (Authorization) + E4 (Collaboration & Routing) ──► E7 (Agent Execution)
       ──► E10 (Observability，随主链路审计)
         —— 端到端验收：Channel 里 @agent → 接受 → 执行 → 产出回帖，全程可审计

S2 ──► E5 (Shared Context & Memory)
         —— 端到端验收：propose_memory → 人审 → memory_items → 检索 → dispatch 注入

S3 ──► E8 (Work Management Core)
         —— 端到端验收：WorkItem → @agent 执行 → 回帖 + 状态更新

V1+ ─► E9 (Work Management Integrations - Jira)
```

> **M1–M9 已退为能力域标签**（D7 竖切，2026-09-10 拍板）：M 号只用于标记「这块能力属于哪个域」，**不再是交付里程碑**。唯一实施基线 = [`detailed/09-implementation-checklist.md`](../../design/detailed/09-implementation-checklist.md) §1（S1/S2/S3 竖切 + 按刀 DoD）。

## 依赖矩阵

|        | E1 | E2 | E3 | E4 | E5 | E6 | E7 | E8 | E9 | E10 |
| ------ | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- |
| **E1** | —  | 提供 owner_user_id / project | 提供 project + member | — | 提供 project | 提供 subject + scope | — | 提供 project | — | 提供 actor |
| **E2** |    | — | 提供 Agent member | lifecycle+activity 过滤 | — | — | 提供 agent + activity | — | — | — |
| **E3** |    |    | — | 消息载体 + Trigger 提取 | 记忆申请投影 | channel scope | execution 输出投影 | — | — | audit |
| **E4** |    |    |    | — | — | permission 决策依据 | Accept → Execution | Trigger from WorkItem (V1+) | — | audit |
| **E5** |    |    |    |    | — | write_memory = REQUIRE_APPROVAL | dispatch 注入 memory_refs | WorkItem 可选引用 memory | — | audit |
| **E6** |    |    |    |    |    | — | — | — | — | — |
| **E7** |    |    |    |    |    |    | — | work_item_ref optional | — | audit |
| **E8** |    |    |    |    |    |    |    | — | Provider 抽象 | audit |
| **E9** |    |    |    |    |    |    |    |    | — | — |
| **E10** |    |    |    |    |    |    |    |    |    | — |

## 核心原则（v0.4 冻结）

1. **三大独立 lifecycle**：
   - **Collaboration**：`Trigger → CollaborationRequest → Decision → Execution (Accept 分支)`
   - **WorkItem**：WorkItem lifecycle 独立
   - **Agent Execution**：Execution/Attempt/Event/Artifact 独立
   - 三者通过 ID 引用，**不共享同一份 `Task` 事实源**
2. **Project = 协作/知识/工作边界**，不挂 Provider 字段
3. **WorkItem 与 Execution 完全独立**：Execution 可无 WorkItem；WorkItem 可多 Execution
4. **Provider 抽象**：`WorkManagementProvider` interface；业务层永不分 provider 类型
5. **Capability × Permission 单一事实源**：Agent 表无 `can_execute` / `can_review`
6. **lifecycle × activity 拆分**：`lifecycle` 由 owner 控制；`activity` 由系统/Runtime 更新
7. **Permission effect 三态**：`ALLOW` / `DENY` / `REQUIRE_APPROVAL`
8. **消息流是 projection**：5 形态 UI 保留，DECISION/AGENT_OUTPUT/MEMORY_REQUEST 形态只引 `entity_ref`
9. **执行后端永久不切外部**：Agent Execution 永远是 MateOS 自研（v0.4 起永久 Non Goal，PRD §8）

## 文档结构（每篇 Epic）

1. 背景与动机
2. 范围（In Scope / Out of Scope）
3. 数据模型
4. API（REST + WS）
5. 关键流程
6. UI（页面 / 组件 / 状态）
7. 验收标准
8. 与其他 Epic 的关系
9. 风险与开放问题
10. 实施顺序

## 状态

全部 10 篇 `Draft`，待评审。验收标准中凡有 `E2E：` 前缀的项都将被纳入 M9 之后的 e2e 套件。

## 与现有文档的对应

| 文档 | 角色 |
| --- | --- |
| `../MateOS-总体需求文档.md`（PRD v0.4） | 全局需求与版本规划 |
| `../../design/SYSTEM_DESIGN.md` v0.3 | 全局架构与数据模型 |
| `../../UI design/MateOS-UI-Design-System-v0.2.md` v0.5 | 全局 UI 规范 |
