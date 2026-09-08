# MateOS 总体需求文档（PRD v0.3）

| 文档信息 | 内容 |
| --- | --- |
| 产品名称 | MateOS（备选：MatePro / Crewly / Memora） |
| 文档状态 | Draft（v0.3，原型评审修订） |
| 版本 | v0.3 |
| 日期 | 2026-09-08 |
| 下游文档 | 系统设计文档 SYSTEM_DESIGN v0.2、UI Design System v0.4 |

> **v0.2 → v0.3 变更摘要**：执行层口径回归自洽（MateOS 独立运行，AgentBoard 改为可选协作扩展点而非分层前提）；Agent 状态枚举收敛为 6 态（与 UI DS / SYSTEM_DESIGN 对齐）；Shared Memory 人审门禁从 V2 提前到 MVP；新增「外部项目集成」扩展点（AgentBoard / Jira 可切换）；Capability taxonomy 统一为 canonical key + 显示名。
> **v0.1 → v0.2 变更摘要**：新增 Project 实体（Channel/Project 边界分离）；新增 V1 范围声明与 Non Goals；新增 Permission Model、User Story、核心实体清单三章；Mention 系统升级为 Mention Resolver；Agent 决策模型增加 Delegate；Agent Capability 结构化；Agent Credential 与 Agent 分离；Memory 增加 Source 溯源。

---

## 1. 产品定位

**MateOS 是一个面向软件开发团队的 AI 原生团队协作平台（AI Native Team Collaboration Platform）。**

它允许人类成员（Human）与 AI Agent 成员（AI Teammate）共同组成团队，在共享 Channel 中沟通、分配任务、共享知识，并协作完成软件开发工作。

核心理念：

> AI 不应该只是一个工具，而应该成为团队成员。

一句话定位：

> MateOS is an AI-native team operating system where humans and AI teammates collaborate through shared channels, memory, and autonomous workflows.

### 1.1 V1 范围声明（突破点收敛）

MateOS 同时触及四个方向：Slack/Teams 类协作工具、Multi-Agent Framework、AI 软件开发平台、知识管理系统。**V1 只选一个突破点：**

> **MateOS V1 = AI Engineering Team Workspace**（软件开发团队的 AI 协作空间）

不做通用 AI Team OS。V1 要证明的只有一件事：

> 人和 Agent 可以像团队成员一样沟通。

---

## 2. 产品目标：解决 AI Coding 的三个核心问题

### 2.1 单 Agent 孤岛问题

当前每个开发者各自持有独立的 AI Assistant，导致：

- 上下文不共享
- 知识无法沉淀
- 决策无法复用

MateOS 的解法：以 Team → Project → Channel → Human + AI Agents → Shared Memory 的结构，把协作从"一对一工具"升级为"团队共享空间"。

### 2.2 团队知识无法积累

当前个人解决问题的产出（Chat history + Local knowledge）无法被团队复用。

MateOS 的解法：将架构决策、Feature 讨论、Bug 经验、Coding 规范沉淀为 **Project Shared Memory**，并通过人审门禁确保质量。

### 2.3 AI Agent 缺少团队协作能力

当前 Agent 是 User → Prompt → Agent 的单点工具。MateOS 让 Agent 成为可协作的团队成员，能够：

- 接受任务
- 拒绝任务
- 请求更多上下文
- 委派任务给更合适的成员（Delegate）
- 与其他 Agent 协作
- Review 其他 Agent 的工作

---

## 3. 核心概念模型

### 3.1 实体层级

```
Organization（组织）
  └─ Team（团队，Human + Agent 混合）
       └─ Project（项目，知识边界）
            └─ Channel（频道，通信边界）
                 └─ Member（成员：HUMAN / AGENT）
```

| 概念 | 说明 |
| --- | --- |
| **Organization** | 组织，包含 Users、Teams、Projects |
| **Team** | 一个开发团队，成员由 Human 与 Agent 混合组成（如 Payment Team：Jason、Tom、Backend-Agent、QA-Agent） |
| **Project** | **知识边界**。同一 Project 下的所有 Channel 共享 repository、架构、Memory、Coding 规范（如 Payment System 下有 #architecture、#backend、#qa，三个 channel 共享同一套项目记忆） |
| **Channel** | **通信边界 / Context Container**。包含 Messages、Members、Memory references、Tasks、Decisions、Files、Repository context；自动形成 Feature Memory |
| **Member** | 统一成员模型，type 为 HUMAN 或 AGENT，两者同为一等成员 |

### 3.2 边界原则（关键设计决策）

- **Channel = communication boundary**：谁在说话、说了什么
- **Project = knowledge boundary**：Agent 知道什么、依据什么

两者必须分开。否则 #backend 的 Agent 不知道 #architecture 讨论过什么。

---

## 4. 用户体系

### 4.1 Human User

能力：登录、创建 Team、创建 Project、创建 Agent、邀请成员、参与讨论。

### 4.2 Member 模型（identity / role / capability / permission）

Member 由四个维度构成：

```
Member
 ├─ identity     身份（id / type: HUMAN | AGENT）
 ├─ role         角色语义（如 Developer / Backend Developer）
 ├─ capability   能力声明（结构化）
 └─ permission   权限（见 §6 Permission Model）
```

Agent 能力为结构化声明（不再只是扁平字符串数组）：

```json
{
  "role": "Backend Developer",
  "capabilities": ["coding", "debugging", "review"],
  "can_execute": true,
  "can_review": false
}
```

> **Capability taxonomy**：v0.3 收敛为 `canonical_key + display_name` 模式。`canonical_key` 用于 Resolver 打分、过滤、决策模型（英文 snake_case，如 `coding` / `debugging` / `review` / `testing` / `architecture`），`display_name` 按用户语言展示（中文 / English / Mixed）。细粒度 skills 标签体系（python / fastapi / postgres 等）**暂缓至 V2+**，V1 用 role + capabilities 已够 Mention Resolver 做能力排序。

### 4.3 Agent Ownership 与 Credential 分离

- **Ownership**：每个 Agent 必须属于一个 Human User（User owns Agent）。原因：成本统计、权限控制、责任归属、审计。
- **Credential 分离**：API Key 等凭据不属于 Agent，模型为 `User → Credential → Agent Runtime`。同一个 Agent 未来可切换 Claude / GPT / Gemini，不与单一 Provider 绑死。

```json
{
  "id": "",
  "type": "AGENT",
  "owner_user_id": "",
  "credential_id": "",
  "provider": "OpenAI/Anthropic",
  "model": "",
  "status": "OFFLINE/AVAILABLE/THINKING/WORKING/WAITING_CONTEXT/ERROR"
}
```

### 4.4 Agent 状态机（与 SYSTEM_DESIGN / UI DS 对齐的 6 态）

| 状态 | 含义 | 触发 |
| --- | --- | --- |
| `OFFLINE` | 未部署 / owner 暂停 / 日限额触顶 | owner 启用 |
| `AVAILABLE` | 空闲且在线 | 收到 Mention 退出 |
| `THINKING` | 正在做能力/上下文/权限判断 | 收到 Mention |
| `WORKING` | 决策为 Accept，正在产出 | 决策 Accept |
| `WAITING_CONTEXT` | 决策为 Need Context，等待人类补齐 | 决策 Need Context |
| `ERROR` | Provider 调用失败 / 密钥失效 | Runtime 报错 |

> 6 态是 PRD / SYSTEM_DESIGN / UI DS 单一事实来源（v0.3 起；v0.2 三处枚举不一致问题关闭）。

---

## 5. 功能需求

### FR-1 Agent 管理

- 用户创建自己的 Agent，需配置：Provider、Credential（API Key）、Model、Runtime、Workspace
- 一个用户可拥有多个 Agent（如 Coding-Agent、Review-Agent、Architecture-Agent）
- Agent 可随时切换其绑定的 Credential（更换 Provider/Model 不换身份）

### FR-2 Channel 成员管理

- Channel 可邀请 Human 成员与 Agent 成员
- 邀请后成员进入该 Channel 的共享上下文；Agent 同时获得所属 Project 的知识边界（Memory / 决策 / 规范）

### FR-3 Agent 状态管理（6 态）

Channel 中展示 Agent 状态，状态集合（与 §4.4 / SYSTEM_DESIGN §6 / UI DS §4 收敛）：

`OFFLINE` / `AVAILABLE` / `THINKING` / `WORKING` / `WAITING_CONTEXT` / `ERROR`

### FR-4 Mention 系统（核心差异点，优先级最高）

三种 Mention 形式：

| Mention 形式 | 行为 |
| --- | --- |
| `@指定成员` | 直接路由给该成员（Human 或 Agent） |
| `@channel-group`（如 @backend） | 走 **Mention Resolver** 寻找合适成员 |
| `@all` | 所有成员收到；Agent 自行判断是否需要响应，避免 Agent 无限聊天 |

**Mention Resolver**（未来 AI team orchestration 的基础）：

```
Message
  ↓
Mention Resolver（解析 mention 意图 + 成员资格过滤）
  ↓
Candidate Agents（候选集合）
  ↓
Capability Ranking（按 role/capabilities 打分，如 Backend-Agent 0.95 / Security-Agent 0.7 / QA-Agent 0.5）
  ↓
Decision（路由给 Top-N 或征求确认）
```

> UI 必须展示 Resolver 命中结果（被选中的 Agent 与分数），不可黑箱。

### FR-5 Agent 决策模型（Decision Model）

Agent 收到请求后**不立即执行**，决策流程：

```
Message → Context Evaluation → Capability Check → Permission Check → Decision
```

Decision 四种结果：

1. **Accept** — 确认有能力、有上下文、有权限，开始执行
2. **Reject** — 明确说明拒绝原因（如缺少领域知识）
3. **Need Context** — 列出所需补充信息（如 API 规格、数据库设计）
4. **Delegate** — "I cannot handle UI. Delegate to Frontend-Agent?" 转交给更合适的成员

> MVP 实现前三种；Delegate 依赖跨 Agent 委派路由，放 V2。

### FR-6 Agent 协作（Collaboration，职责收敛）

**MateOS 自洽**（v0.3 修订）：MateOS 负责 Conversation、Intent、Collaboration、Decision 全链路；执行层由 MateOS 自研 Runtime（SYSTEM_DESIGN §6 Connector）承接，**不强制依赖 AgentBoard**。如需与 AgentBoard 协作，遵循 §10 可选协作扩展点。

```
MateOS：User / Channel / Memory / Mention / Decision / Runtime(自研)
       ↕ 可选协议（§10）
AgentBoard：Task / Worker / CLI / Code / PR / Review（V1+ 集成）
```

> 当 Agent Accept 一个请求后，MateOS Runtime 自身执行（对话型 LLM 调用）；如需长时任务/代码执行，进入 §10 的可切换执行后端。

### FR-7 Shared Memory（核心竞争力，v0.3 提前到 MVP）

| Memory 类型 | 归属 | 示例 |
| --- | --- | --- |
| Personal Memory | 个人 | 用户偏好（如"Jason prefers clean architecture"） |
| Project Memory | 团队共享 | 项目事实（如"Payment uses Stripe webhook"） |
| Decision Memory | 团队共享 | 架构决策记录（如 ADR-001：为什么选 PostgreSQL） |
| Knowledge Memory | 团队共享 | 业务知识（如"Customer entity represents billing owner"） |

**每条 Memory 必须带 Source 溯源**（否则半年后"根据 memory..."回答时无法回答"谁说的"）：

```json
{
  "type": "DECISION",
  "content": "Payment uses Stripe webhook",
  "source": { "type": "CHANNEL_MESSAGE", "channel_id": "...", "message_seq": 12345 },
  "approved_by": "Jason"
}
```

**Memory 生命周期（人审门禁）**：

1. Agent 提出记忆写入申请（"I learned: ... Save to project memory?"）
2. Human **Approve** 后才进入 Shared Memory
3. UI 端必须在记忆卡上展示 Source 行（频道 + 消息引用）

> 自动 Memory Extraction（Agent 无感自动沉淀）列入暂缓，见 §11。

---

## 6. Permission Model

Agent 权限默认矩阵（可按 Channel/Project 覆盖）：

| 权限 | 默认值 | 说明 |
| --- | --- | --- |
| read message | **yes** | 读 Channel 消息 |
| write message | **yes** | 发消息 |
| write memory | **request** | 写 Memory 须走人审门禁（FR-7） |
| execute code | **no** | 执行代码默认关闭（执行层能力，须显式授予） |
| create PR | **optional** | 按 Project 配置 |

Human 成员权限沿用常规协作工具模型（owner / member），V1 从简。

---

## 7. User Story（V1 核心场景）

**Developer（Jason）：**

> 作为开发者，我希望邀请我的 Coding Agent 加入项目 Channel，让它理解团队上下文并协助开发。

**Team Lead（Tom）：**

> 作为团队负责人，我希望 @backend 发起请求时，系统自动匹配最合适的 Agent，而不是我逐个指定。

**Agent（Backend-Agent）：**

> 作为 Agent 成员，当收到超出我能力的请求时，我希望明确 Reject 并说明原因，或 Delegate 给 Frontend-Agent，而不是硬做。

---

## 8. Non Goals（V1 不做）

- ❌ 替代 Slack / Teams（不是通用 IM）
- ❌ 替代 GitHub（代码托管 / PR 流转仍在既有工具）
- ❌ Agent 自动无限协作（无人类参与的 Agent 间自动循环对话）
- ❌ 自动修改代码（execute code 默认 no，见 §6）
- ❌ 企业级 IAM / SSO

---

## 9. 核心实体清单（架构设计输入）

```
Organization / User / Team / Project / Channel / Member
Agent / Credential / Message / Mention / Memory
CollaborationRequest / Permission
ExternalIntegration（v0.3 新增：AgentBoard / Jira 等可选）
```

---

## 10. 外部集成扩展点（v0.3 新增）

MateOS 自洽运行（§FR-6），但为长期生态预留两条可选集成扩展点。两条都是**可选开关**，默认关闭，不影响 MVP 交付。

### 10.1 执行后端切换（AgentBoard 协作）

如用户已部署 AgentBoard，MateOS 可在 Project 设置中开启「执行后端：AgentBoard」：

- MateOS 决策 Accept 后，将请求以 `CollaborationRequest` 协议推送到 AgentBoard
- AgentBoard 负责 Task / Worker / Code / PR / Review 编排
- 结果回写 MateOS Channel（仍展示决策与产出，但不占用 MateOS Runtime 资源）
- 协议细节：见 SYSTEM_DESIGN §8 协议汇总

**默认**：执行后端 = MateOS Runtime（自研）。

### 10.2 项目管理工具切换（AgentBoard / Jira）

项目管理页面（未来 P-Project Center）支持外部 Issue 跟踪系统集成：

| 集成项 | 默认 | 可选 |
| --- | --- | --- |
| 协作请求视图 | MateOS 内部 | AgentBoard Issue 视图 / Jira Issue 视图 |
| 任务状态同步 | MateOS 内部 | 双向同步到 AgentBoard / Jira |
| 评论 / 决策回写 | MateOS 内部 | 写入外部 Issue 评论 |

**V1+ 范围**：MVP 不实现同步逻辑，仅在 P4 Project Dashboard 预留「外部项目集成」开关位（V1+ 启用，UI 见 P4 原型）。

---

## 11. 版本规划

### MVP（V1）— AI Engineering Team Workspace

| 模块 | 范围 |
| --- | --- |
| User | 注册、登录 |
| Team | 创建团队 |
| **Project** | 创建项目（知识边界） |
| Agent | 创建 Agent、配置 Credential、状态管理（6 态） |
| Channel | 创建 Channel、邀请成员 |
| Chat | 消息收发 |
| Mention | @Human、@Agent、@group（Mention Resolver）、@all |
| Agent Decision | Accept / Reject / Need Context（Delegate 留接口） |
| **Shared Memory** | **人审门禁 + Source 溯源（v0.3 提前到 MVP）** |
| Memory 审批中心 | P6 页面（V1 含） |
| Permission | §6 默认权限矩阵 |

### V2

- Delegate 路由（跨 Agent 委派）
- Project Context 扩展
- Agent Collaboration 协议对接 AgentBoard（§10.1）
- 项目管理外部集成（§10.2）

### V3

- Agent Autonomous Task Delegation
- PR Review / Code Execution

### V4

- AI Engineering Organization

---

## 12. 明确暂缓项

| 项 | 原因 |
| --- | --- |
| 自动任务拆分（Parent Task → 子任务分派） | MateOS Runtime 内部编排；执行后端可切到 AgentBoard（§10.1） |
| Agent 自主协作（无人类参与的 Agent 间循环） | 防失控，V1 Non Goal |
| 自动 Memory Extraction | 记忆质量优先，先跑通人审门禁 |
| 细粒度 skills 标签体系 | V1 用 role + capabilities 足够 |

---

## 13. 开放问题

- [ ] 产品名称最终确定（MateOS / MatePro / Crewly / Memora）
- [ ] Agent Runtime 的具体形态（本地进程 / 远端 Worker / 混合）
- [ ] Memory 的存储与检索方案（向量 / 结构化 / 混合）
- [ ] Permission Model 与 Decision Model 的交互细节（Permission Check 失败时走 Reject 还是 Need Context）
- [ ] AgentBoard 协作协议（§10.1）的最终消息格式
- [ ] Jira 集成（§10.2）的 webhook + OAuth 策略

---

## 14. 后续步骤

1. 维护 **SYSTEM_DESIGN.md** v0.2，落地：6 态、§10 扩展点协议、§V1+ 项目集成位
2. 维护 **UI Design System** v0.4，关掉 0.5px 验证、P6/P7 升 MVP、加 §10.2 UI 钩子
3. 修订三张原型（P3/P4/P5）兑现 v0.3 变更

---

## 更新记录

| 版本 | 日期 | 变更 |
| --- | --- | --- |
| v0.1 | 2026-09-07 | 初稿，整理自外部 PRD 草案 |
| v0.2 | 2026-09-07 | 吸收外部架构师 review：新增 Project 实体与 Channel/Project 边界分离；V1 收敛为 AI Engineering Team Workspace 并增加 Non Goals；新增 Permission Model / User Story / 核心实体清单；Mention 升级为 Mention Resolver；Decision Model 增加 Delegate（MVP 实现前三种）；Agent Capability 结构化 + Credential 分离；Memory 增加 Source 溯源；FR-6 职责收敛为 CollaborationRequest |
| v0.3 | 2026-09-08 | 原型评审修订：执行层回归自洽（FR-6 + §10.1 协作扩展点替代硬分层）；Agent 状态 6 态对齐 SYSTEM_DESIGN / UI DS；Shared Memory 人审门禁从 V2 提前到 MVP（FR-7 + 页面清单 P6/P7 升 MVP）；新增「外部项目集成」扩展点（§10.2，AgentBoard / Jira 可切换）；Capability taxonomy 收敛为 canonical_key + display_name |
