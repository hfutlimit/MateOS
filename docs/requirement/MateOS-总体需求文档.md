# MateOS 总体需求文档（PRD v0.4）

| 文档信息 | 内容 |
| --- | --- |
| 产品名称 | MateOS（备选：MatePro / Crewly / Memora） |
| 文档状态 | Draft（v0.4，架构评审修订） |
| 版本 | v0.4 |
| 日期 | 2026-09-08 |
| 下游文档 | 系统设计文档 SYSTEM_DESIGN v0.3、UI Design System v0.5、Epic 文档 E1-E10 |

> **v0.3 → v0.4 变更摘要**（架构评审后推倒重来）：
> 1. **删除 AgentBoard execution backend 概念**——MateOS 永远是自洽执行域；AgentBoard 不再出现在 architecture diagram、Project schema、Runtime dispatch path
> 2. **新增 Work Management 域**（独立 domain）—— WorkItem + Provider 抽象；Built-in 是默认实现，Jira 是替代 Provider；通过 `WorkItemBinding` 关联 Project 与 Provider
> 3. **新增 CollaborationRequest 一等实体**——Mention / WorkItem / API / Automation 都是 Trigger；Trigger 产生 CollaborationRequest，CollaborationRequest → Decision → Execution
> 4. **Agent capability / permission 单一事实源**——删除 Agent 的 `can_execute` / `can_review` 字段；统一由 `capability`（能不能）+ `permission`（允不允许）两个独立维度
> 5. **Agent lifecycle 与 activity 分开**——lifecycle=ACTIVE/PAUSED/DISABLED；activity=OFFLINE/AVAILABLE/THINKING/WORKING/WAITING_CONTEXT/ERROR
> 6. **Permission effect 三态统一**——`REQUEST` → `REQUIRE_APPROVAL`（避免与 CollaborationRequest / HTTP Request 概念冲突）
> 7. **消息流改 projection**——5 形态保留为 UI 投影；DECISION/MEMORY_REQUEST 形态只引 `entity_ref`，事实源在 decision_records / memory_proposals
> 8. **Project Management 视为 V1 一等公民**——不再绑定 AgentBoard；Work 页面（取代 Tasks）进入 MVP 范围
>
> **v0.1 → v0.2 变更摘要**：新增 Project 实体（Channel/Project 边界分离）；V1 范围与 Non Goals；Permission Model / User Story / 核心实体清单；Mention 升级为 Resolver；Decision 加 Delegate；Agent Capability 结构化 + Credential 分离；Memory 加 Source 溯源；FR-6 收敛为 CollaborationRequest

---

## 1. 产品定位

**MateOS 是一个面向软件开发团队的 AI 原生团队协作平台（AI Native Team Collaboration Platform）。**

它允许人类成员（Human）与 AI Agent 成员（AI Teammate）共同组成团队，在共享 Channel 中沟通、分配任务、共享知识、协作完成软件开发工作。

核心理念：

> AI 不应该只是一个工具，而应该成为团队成员。

一句话定位：

> MateOS is an AI-native team operating system where humans and AI teammates collaborate through shared channels, memory, execution, and work management.

### 1.1 V1 范围声明（突破点收敛）

MateOS 同时触及四个方向：Slack/Teams 类协作工具、Multi-Agent Framework、AI 软件开发平台、知识管理系统。**V1 只选一个突破点**：

> **MateOS V1 = AI Engineering Team Workspace**（软件开发团队的 AI 协作空间）

不做通用 AI Team OS。V1 要证明的只有一件事：

> 人和 Agent 可以像团队成员一样沟通、协作、推进工作。

### 1.2 User Promise

> **把工作交给 AI 团队，MateOS 负责持续推进；只有需要人的判断、授权或补充信息时才打扰用户。**
>
> Give work to your AI team. MateOS keeps it moving and only asks for your attention when human judgment is needed.

这条承诺是 UX、Needs You、通知、Agent handoff、Progressive Disclosure 的统一原则：**系统可以复杂，用户注意力必须被保护**。

与之配套的唯一硬约束：

> 用户永远不需要理解 CollaborationRequest / Execution / Attempt / Event / Lease / Provider / fencing 才能完成操作。
> 界面应随系统能力增强而变得更简单，而不是把内部模型一对一翻译成页面（**Domain Entity ≠ UI Navigation Entity**）。

### 1.3 Primary User Journeys

产品的三条主路径。**不要从 Entity 开始讲产品，要从用户路径开始**：

**Journey A — Ask the Team**

```
人在 Channel 提出需求
  → @backend / @qa / @architecture
  → MateOS 找到合适 Agent（不黑箱：命中候选与理由可见）
  → Agent 接手（Taking this）
  → 持续工作（人可随时看到进展，但不需要盯着）
  → 结果回到原 Channel
```

**Journey B — Needs You**

```
Agent 工作中遇到需要人类判断的事
  → 进入 Needs You
  → 说明：发生了什么 / 为什么需要你 / 建议怎么做 / 影响什么 / 阻塞了什么
  → 用户一键决策（批准 / 补充信息 / 改派 / 驳回）
  → Agent 自动继续
```

**Journey C — Deliver Work**

```
WorkItem（PO 建 / 从对话升级）
  → Agent 接手并成为 owner
  → Execution（人只看 Progress 与 Next step）
  → 产出（Artifact / 结果回帖）
  → Review / QA（后续阶段）
  → Work 向前推进
```

---

## 2. 产品目标：解决 AI Coding 的三个核心问题

### 2.1 单 Agent 孤岛问题

以 Team → Project → Channel → Human + AI Agents → Shared Memory 的结构，把协作从"一对一工具"升级为"团队共享空间"。

### 2.2 团队知识无法积累

将架构决策、Feature 讨论、Bug 经验、Coding 规范沉淀为 **Project Shared Memory**，并通过人审门禁确保质量。

### 2.3 AI Agent 缺少团队协作能力

Agent 是一等成员。**V1 的协作能力边界**：

```
V1（MVP）：
  - 接受工作（Accept）
  - 拒绝工作（Reject，必带理由与建议人选）
  - 请求更多上下文（Need Context）
  - 产出结果（Result / Artifact）

Future（V2+）：
  - 委派给其它 Agent（Delegate routing）
  - 自动 Review 链（Backend → Reviewer → QA）
  - 依赖分析与多 Story 规划
  - 无人值守的多 Agent 交付
```

> **口径统一**：`Delegate` 在 V1 只保留枚举位，**不在原型里假装已实现自动 Backend → Reviewer → QA 全链运行**；V1 的跨 Agent 接力由**人**发起（@ 另一个 Agent 或改派）。

### 2.4 AI 团队缺乏统一的项目管理（v0.4 新增）

**问题**：当前 Issue / Task 在 GitHub / Jira / Linear / 自研系统之间分散；AI Agent 没有"团队的工作台"，只能在零散的 Issue 上做一次性工作。
**解法**：MateOS 内置 Work Management 域，Built-in Provider 即可使用；同时支持通过 Provider 抽象切换到 Jira，未来扩展 Linear / GitHub Issues / Azure Boards。

---

## 3. 核心概念模型

### 3.1 实体层级

```
Organization（组织）
  └─ Team（团队，Human + Agent 混合）
       └─ Project（项目，协作 / 知识 / 工作边界）
            ├─ Channel（频道，通信边界）
            │    └─ Message（消息：人类 / 决策投影 / 输出投影 / 系统 / 记忆申请投影）
            │         └─ Mention / Trigger → CollaborationRequest
            ├─ Memory（共享知识：Personal / Project / Decision / Knowledge）
            └─ Work Management（WorkItem + Provider Binding：Built-in | Jira | ...）
                  └─ Execution（Agent Execution 域，与 WorkItem 通过 ID 引用，可独立存在）
```

| 概念 | 说明 |
| --- | --- |
| **Organization** | 组织，包含 Users、Teams、Projects |
| **Team** | 团队，Human + Agent 混合（如 Payment Team：Jason、Tom、Backend Agent、QA Agent） |
| **Project** | **协作 / 知识 / 工作边界**。Channel / Memory / WorkItem 都挂在 Project 下 |
| **Channel** | **通信边界**。包含 Messages、Members、Memory references、Tasks references |
| **Member** | 统一成员模型，type = HUMAN / AGENT，两者同为一等成员 |
| **Memory** | 项目共享知识（4 类）+ Source 溯源 + 人审门禁 |
| **WorkItem** | 项目中的一项工作（task / story / bug / issue） |
| **Execution** | Agent 一次具体执行（可独立存在，也可关联 WorkItem） |
| **CollaborationRequest** | 一等协作实体，由 Trigger（Mention / WorkItem / API / Automation）产生 |
| **WorkManagementProvider** | 项目管理后端抽象（Built-in / Jira / 未来 Linear 等） |

### 3.2 边界原则

- **Channel = 通信边界**——谁在说话、说了什么
- **Project = 协作 / 知识 / 工作边界**——Member 可见什么、Memory 共享到哪、WorkItem 属于哪个项目
- **WorkItem 与 Execution 完全独立**——WorkItem 不必有 Execution（如人类编辑文档）；Execution 不必属于 WorkItem（如 `@backend 看下代码`）

### 3.3 三大独立 lifecycle

```
Collaboration lifecycle
  Trigger → CollaborationRequest → Decision → Execution (Accept 分支)

WorkItem lifecycle
  WorkItem → status transitions → archive

Execution lifecycle
  Execution → Attempt → Event → Artifact
```

三者通过 ID 引用，**不共享同一份 `Task` 事实源**。

---

## 4. 用户体系

### 4.1 Human User

能力：登录、创建 Org / Team / Project、创建 Agent、邀请成员、参与讨论、审批记忆、创建 WorkItem。

### 4.2 Member 模型（identity / role / capability / permission）

```
Member
 ├─ identity     id / type: HUMAN | AGENT
 ├─ role         'Backend Developer' 等显示名
 ├─ capability   能力声明（结构化，canonical key）
 └─ permission   权限（E6 统一，per scope 覆盖）
```

Agent Capability 是结构化声明（不再有 `can_execute` / `can_review` 字段，v0.4 移除）：

```json
{
  "role": "Backend Developer",
  "capabilities": ["coding", "debugging", "review"]
}
```

> **重要边界**：Capability = "能不能做"（如 `coding` / `review`）；Permission = "允不允许做"（如 `execute_code` / `create_pr`）。两者**单一事实源**——Agent 表不再存权限字段，权限一律通过 E6 矩阵。

### 4.3 Agent Ownership 与 Credential 分离

```json
{
  "id": "...",
  "type": "AGENT",
  "owner_user_id": "...",
  "credential_id": "...",
  "provider": "OpenAI | Anthropic | Google",
  "model": "...",
  "lifecycle": "ACTIVE | PAUSED | DISABLED",
  "activity":  "OFFLINE | AVAILABLE | THINKING | WORKING | WAITING_CONTEXT | ERROR"
}
```

**lifecycle** 与 **activity** 是两个独立维度（v0.4 拆分）：

| 维度 | 状态 | 含义 |
| --- | --- | --- |
| **lifecycle** | `ACTIVE` | owner 启用，可参与工作 |
| | `PAUSED` | owner 主动暂停（不出现在 Resolver 候选） |
| | `DISABLED` | 系统禁用（违规 / 滥用） |
| **activity** | `OFFLINE` | 心跳丢失 > 90s |
| | `AVAILABLE` | 空闲且 lifecycle=ACTIVE |
| | `THINKING` | 收到 mention，正在判断 |
| | `WORKING` | 决策 Accept，正在产出 |
| | `WAITING_CONTEXT` | 决策 Need Context，等人类补齐 |
| | `ERROR` | Provider 失败 / 限额 / 凭据失效 |

> 之前 v0.3 把 `OFFLINE`（心跳丢失）和 `PAUSED`（owner 暂停）合并是错的。lifecycle 控制"能不能上线"，activity 控制"在线时在做什么"。

### 4.4 Credential 池

```json
{
  "id": "...",
  "user_id": "...",
  "provider": "openai | anthropic | ...",
  "label": "Anthropic 工作区",
  "secret_encrypted": "<AES-256-GCM>",
  "meta": { "base_url": "...", "region": "..." }
}
```

User → Credential 1:N → Agent 1:1。Agent 切换 credential 不换身份。

---

## 5. 功能需求

### FR-1 Agent Registry

- 用户创建 Agent：name / role / capabilities / runtime / workspace，绑定 credential_id
- 一个用户可拥有多个 Agent（Coding / Review / Architecture）
- Agent 可热切换 credential
- Agent lifecycle 由 owner 控制（PAUSE / ACTIVATE / DISABLE）
- **无** `can_execute` / `can_review` 字段——由 E6 permission 矩阵统一管控

### FR-2 Channel 成员管理

- Channel 可邀请 Human 与 Agent 成员
- Agent 成员自动获得所属 Project 的知识边界（Memory / WorkItem / Decision）

### FR-3 Agent Lifecycle & Activity

- **lifecycle** 由 owner 显式控制（API + UI）
- **activity** 由 Runtime 心跳 / Mention 触发自动更新
- lifecycle=PAUSED / DISABLED 的 Agent **永不**进入 Resolver 候选

### FR-4 Collaboration & Routing

**Trigger**（多种来源）：

| Trigger 类型 | 来源 |
| --- | --- |
| `MENTION` | 消息流 @ 提及 |
| `WORK_ITEM` | WorkItem 状态变化（assignee / 阻塞） |
| `API` | 外部系统调用 |
| `AUTOMATION` | 定时 / 规则触发 |

**CollaborationRequest**（一等实体）：

```
Trigger ──► CollaborationRequest ──► Decision
                                       │
                          ┌────────────┼────────────┐
                          ▼            ▼            ▼
                      ACCEPT       REJECT     NEED_CONTEXT
                          │
                          ▼
                      Execution (E7)
```

Decision 三态（v0.4 与 v0.3 相同）：Accept / Reject / Need Context；Delegate 留 V2。

### FR-5 Mention Resolver

| Mention 形式 | 行为 |
| --- | --- |
| `@指定成员` | 直接路由 |
| `@能力组` | 走 Mention Resolver（`lifecycle=ACTIVE ∩ team member ∩ 有效权限 ALLOW ∩ 有空闲 slot`） |
| `@all` | 全部 `lifecycle=ACTIVE` 的 Agent；带成本预警 |

> **v0.8 修正**：`activity` **不参与调度过滤**（activity 只做 UI 派生）；候选资格由 `lifecycle` + 成员资格 + 有效权限（Channel 覆盖 → Project 覆盖 → 默认矩阵）+ Redis slot 决定。健康度由 `health` 独立表达（`UNHEALTHY` 不进候选）。详见 SYSTEM_DESIGN §4.2.1 / §6.2 与 detailed/04 §2.1。

**Resolver 不黑箱**（UI 必须展示命中候选 + 分数 + 决策依据 analysis{capability, context_score, permission}）。

### FR-6 Shared Memory（v0.3 提前到 MVP）

- 4 类：Personal / Project / Decision / Knowledge
- **每条 Memory 必须带 Source 溯源**（source_type / source_channel_id / source_message_seq 三件套强约束）
- **人审门禁**（E5 详细）：Agent 申请 → 人类批准 → 入库
- UI 端**必须**展示 Source 行（v0.4 起硬性）

### FR-7 Work Management（v0.4 新增 MVP 一等公民）

```
WorkItem
 ├─ id
 ├─ project_id
 ├─ type: TASK | STORY | BUG | EPIC
 ├─ title, description
 ├─ status: OPEN | IN_PROGRESS | IN_REVIEW | DONE | CLOSED
 ├─ assignee: { member_type, member_id }?  (Human or Agent)
 ├─ due_at?
 ├─ canonical_status_category: TODO | IN_PROGRESS | DONE
 ├─ provider_key: builtin | jira
 ├─ external_ref?  (jira 时填)
 ├─ url?
 ├─ created_at / updated_at
```

- **Built-in Provider 是默认实现**——WorkItem 数据落 MateOS `work_items` 表
- **Jira Provider 是替代实现**——Jira 是 source of truth，MateOS 维护本地同步表示（**v0.5：落在 `work_items` 单表，`work_item_projections` 已删除**）
- Provider 抽象（`WorkManagementProvider` interface）让业务层不感知具体实现
- 切换 Provider 是两个动作：**Change Provider**（仅影响新 WorkItem）+ **Migrate Existing WorkItems**（独立 Wizard）

### FR-8 Channel & Messaging

- Channel CRUD + 5 形态消息流（人类 / 决策投影 / 输出投影 / 系统 / 记忆申请投影）
- 5 形态是 **UI projection**——DECISION 消息只引 `decision_ref`，事实源在 `decision_records` 表
- seq 同步、断线续传、附件直传、幂等去重

### FR-9 Permission & Approval

- **8 键**：`read_message` / `write_message` / **`propose_memory`** / `write_memory` / `execute_code` / `create_pr` / `approve_memory` / `manage_channel`
- `check(subject, perm, scope) → ALLOW | DENY | REQUIRE_APPROVAL`
- `REQUIRE_APPROVAL`（v0.4 起替换 `REQUEST`）走人审门禁或决策路径
- **申请与写入分离**（v0.8）：Agent 申请记忆只校验 `propose_memory`（默认 ALLOW）；`write_memory` 仅 service-to-service，不出现在 Agent 申请路径上
- **没有覆盖行 ≠ 拒绝**：三层合并（Channel > Project > 默认矩阵），未命中覆盖即回落默认矩阵

### FR-10 Human Attention / Needs You（v0.8 新增 · MVP 核心能力）

**定义**：Needs You 是**只读 read projection**，把多个事实源里"需要人"的事项汇聚到唯一入口。**它不是新的 Domain Entity，也不是第二事实源。**

```
CollaborationRequest.NEED_CONTEXT / UNRESOLVED
MemoryProposal.PENDING（待审批）
Execution 失败 / 不可达
Agent UNHEALTHY / Credential 失效
WorkItem BLOCKED
预算阈值（80% / 100%）
        │
        ▼
   Needs You（四分类：Decision / Information / Approval / Problems）
```

每个 Item 至少包含：`发生了什么` / `为什么需要你` / `阻塞了什么` / `建议怎么做` / `可用动作[]` / `来源与上下文` / `urgency`。

**UX invariant（硬性）**：

> 任何进入 Needs You 的事项**必须给用户一个可执行动作**；不允许只展示系统错误。

对照示例——**禁止**：

```
Execution #8f2c FAILED
Attempt 2
Provider 401
```

**必须**：

```
Reviewer Agent needs your help
它的 AI Provider 凭据已过期，评审无法继续。
推荐：重新连接凭据。   [重新连接] [改派其它 Agent]
阻塞的工作：LOGIN-142
```

**收敛规则**：Memory 审批、Work 审批、Permission 审批**全部**进 `Needs You → Approval`；MVP **不设独立 Approval Center 一级页面**。信息架构为 `Needs You / Channels / Work / Team / Settings`。

---

## 6. Permission Model（v0.4 三态）

| 权限 | Human owner | Human member | Agent (lifecycle=ACTIVE) |
| --- | --- | --- | --- |
| `read_message` | ALLOW | ALLOW | ALLOW（已加入 channel） |
| `write_message` | ALLOW | ALLOW | ALLOW |
| **`propose_memory`** | **ALLOW** | **ALLOW** | **ALLOW**（申请入口，v0.8 新增） |
| `write_memory` | REQUIRE_APPROVAL | REQUIRE_APPROVAL | REQUIRE_APPROVAL |
| `execute_code` | DENY | DENY | DENY（V1） |
| `create_pr` | REQUIRE_APPROVAL | DENY | DENY |
| `approve_memory` | ALLOW | DENY | DENY |
| `manage_channel` | ALLOW | DENY | DENY |

---

## 7. 核心实体清单（架构设计输入）

```
Organization / Team / Project / Channel / Member
Agent / Credential / Message / Trigger / CollaborationRequest / Decision
Memory / MemoryProposal
WorkItem / WorkComment / WorkRelation / WorkItemBinding
WorkManagementProvider (interface) / BuiltInProvider / JiraProvider
Execution / ExecutionAttempt / ExecutionEvent / ExecutionArtifact
Permission / User
```

---

## 8. Non Goals（V1 不做）

- ❌ 替代 Slack / Teams（不是通用 IM）
- ❌ 替代 GitHub（代码托管 / PR 流转仍在既有工具）
- ❌ Agent 自动无限协作（无人类参与的 Agent 间循环）
- ❌ **与 AgentBoard 集成**（v0.4 起永久移出 scope——MateOS 独立执行域；AgentBoard 不再出现在架构图、Project schema、UI 中）
- ❌ **Execution backend 可切换**——永久只走 MateOS Runtime
- ❌ 自动修改代码（execute_code 默认 DENY）
- ❌ 企业 IAM / SSO

---

## 9. 版本规划

### MVP（V1）— AI Engineering Team Workspace

| 模块 | 范围 |
| --- | --- |
| E1 Identity & Workspace | Org/Team/Project + Member + 3 角色 |
| E2 Agent Registry | Agent + Credential + Lifecycle/Activity |
| E3 Channel & Timeline | 5 形态消息流（projection） |
| E4 Collaboration & Routing | Trigger / CollaborationRequest / Decision |
| E5 Shared Context & Memory | 4 类 + Source 溯源 + 人审门禁 |
| E6 Authorization & Approval | 7 键 + 3 态 + 三层覆盖 |
| E7 Agent Execution | Execution / Attempt / Event / Artifact |
| E8 Work Management Core | WorkItem + Built-in Provider |
| E10 Observability & Operations | Audit + 通知 + OTel + 压测 |

### V1+

| 模块 | 范围 |
| --- | --- |
| E9 Work Management Integrations | Jira Provider 适配器 |
| 进一步 Provider | Linear / GitHub Issues / Azure Boards |

### V2

- Delegate 路由（跨 Agent 委派）
- Project Context 扩展
- Memory 检索（embedding + rerank）
- Sandbox 执行（V3 起）

---

## 10. 明确暂缓项

| 项 | 原因 |
| --- | --- |
| 细粒度 skills 标签体系 | V1 用 role + capabilities 足够 |
| Agent 自主任务无限循环 | 防失控，V1 Non Goal |
| 自动 Memory Extraction | 质量优先，先跑通人审门禁 |
| WorkItem 历史迁移 | 切换 Provider 单独走 Wizard，不在 MVP 必做 |

---

## 11. 开放问题

- [ ] 产品名称最终确定（MateOS / MatePro / Crewly / Memora）
- [ ] WorkItem 的 type 枚举（是否需要 EPIC / STORY / TASK / BUG 四级）
- [ ] Built-in Provider 的 WorkItem 是否需要 Sprint / Backlog 概念（V1 简化为扁平列表）
- [ ] Jira OAuth 授权粒度（项目级 vs 用户级）
- [ ] AgentCard lifecycle × activity UI 组合方式

---

## 12. 后续步骤

1. SYSTEM_DESIGN v0.3：新增 Agent Execution domain / Work Management Core + Integrations 拆分 / 数据模型更新
2. UI Design System v0.5：P4 改 Work Management 动态表单 / 新增 Work 页面
3. Epic E1-E10 按新边界重写
4. 启动 M1（E1 Identity & Workspace 编码）

---

## 更新记录

| 版本 | 日期 | 变更 |
| --- | --- | --- |
| v0.1 | 2026-09-07 | 初稿 |
| v0.2 | 2026-09-07 | 吸收外部架构师 review：Project 实体 / V1 范围 / Permission Model / Capability 结构化 / Memory Source 溯源 |
| v0.3 | 2026-09-08 | 原型评审修订：执行层回归自洽 / 6 态 / Shared Memory 提前 MVP / 外部集成扩展点 |
| v0.4 | 2026-09-08 | 架构评审推倒：删除 AgentBoard execution / 新增 Work Management 域 / CollaborationRequest 一等 / lifecycle 与 activity 拆分 / Capability 与 Permission 单一事实源 / Permission effect 改 `REQUIRE_APPROVAL` / 消息流改 projection / 重新切成 10 个 Epic |
