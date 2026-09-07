# MateOS 总体需求文档（PRD v0.2）

| 文档信息 | 内容 |
| --- | --- |
| 产品名称 | MateOS（备选：MatePro / Crewly / Memora） |
| 文档状态 | Draft（v0.2，吸收外部架构师 review 后修订） |
| 版本 | v0.2 |
| 日期 | 2026-09-07 |
| 下游文档 | 系统设计文档 SYSTEM_DESIGN（待建，见 §12 后续步骤） |

> **v0.1 → v0.2 变更摘要**：新增 Project 实体（Channel/Project 边界分离）；新增 V1 范围声明与 Non Goals；新增 Permission Model、User Story、核心实体清单三章；Mention 系统升级为 Mention Resolver；Agent 决策模型增加 Delegate；Agent Capability 结构化；Agent Credential 与 Agent 分离；Memory 增加 Source 溯源；Agent 协作职责收敛为 CollaborationRequest（执行归 AgentBoard）。

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

MateOS 的解法：将架构决策、Feature 讨论、Bug 经验、Coding 规范沉淀为 **Project Shared Memory**。

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

> 细粒度 skills 标签体系（python / fastapi / postgres 等）**暂缓至 V2+**，V1 用 role + capabilities 已够 Mention Resolver 做能力排序，避免过早过度设计。

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
  "status": "FREE/BUSY/OFFLINE"
}
```

---

## 5. 功能需求

### FR-1 Agent 管理

- 用户创建自己的 Agent，需配置：Provider、Credential（API Key）、Model、Runtime、Workspace
- 一个用户可拥有多个 Agent（如 Coding-Agent、Review-Agent、Architecture-Agent）
- Agent 可随时切换其绑定的 Credential（更换 Provider/Model 不换身份）

### FR-2 Channel 成员管理

- Channel 可邀请 Human 成员与 Agent 成员
- 邀请后成员进入该 Channel 的共享上下文；Agent 同时获得所属 Project 的知识边界（Memory / 决策 / 规范）

### FR-3 Agent 状态管理

Channel 中展示 Agent 状态，状态集合：

- `FREE` / `BUSY` / `THINKING` / `WAITING_CONTEXT` / `OFFLINE`

### FR-4 Mention 系统（核心差异点，优先级最高）

三种 Mention 形式：

| Mention 形式 | 行为 |
| --- | --- |
| `@指定成员` | 直接路由给该成员（Human 或 Agent） |
| `@channel-group`（如 @backend-team） | 走 **Mention Resolver** 寻找合适成员 |
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

**MateOS 不负责任务执行与自动任务拆分**（那是执行层的事）。MateOS 负责：

- Conversation（对话）、Intent（意图）、Collaboration（协作编排意图）、Decision（决策）

当 Agent Accept 一个请求后，MateOS 产生 **CollaborationRequest** 交给 AgentBoard：

```
MateOS: Backend-Agent accepted feature request → create execution request
AgentBoard: Task / Worker / Code / PR / Review
```

自动任务拆分（Parent Task → 子任务分派）归 AgentBoard 编排，MateOS 只透出协作视图。

### FR-7 Shared Memory（核心竞争力）

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
  "source": { "type": "CHANNEL_MESSAGE", "id": "12345" },
  "approved_by": "Jason"
}
```

**Memory 生命周期（人审门禁）：**

1. Agent 提出记忆写入申请（"I learned: ... Save to project memory?"）
2. Human **Approve** 后才进入 Shared Memory

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

> 作为团队负责人，我希望 @backend-team 发起请求时，系统自动匹配最合适的 Agent，而不是我逐个指定。

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

## 9. 与 AgentBoard 的集成边界

MateOS 与 AgentBoard 分层解耦：

| 层 | 系统 | 职责 |
| --- | --- | --- |
| **协作层（Collaboration Layer）** | MateOS | User、Channel、Memory、Mention、Decision → 产出 **CollaborationRequest** |
| **执行层（Execution Layer）** | AgentBoard | Task、Worker、CLI、Code、PR、Review |

```
            MateOS（协作层）
     User / Channel / Memory / Mention / Decision
                   |
          CollaborationRequest
                   |
            AgentBoard（执行层）
     Task / Worker / CLI / Code / PR / Review
```

---

## 10. 版本规划

### MVP（V1）— AI Engineering Team Workspace

| 模块 | 范围 |
| --- | --- |
| User | 注册、登录 |
| Team | 创建团队 |
| **Project** | 创建项目（知识边界） |
| Agent | 创建 Agent、配置 Credential、状态管理 |
| Channel | 创建 Channel、邀请成员 |
| Chat | 消息收发 |
| Mention | @Human、@Agent、@group（Mention Resolver）、@all |
| Agent Decision | Accept / Reject / Need Context（Delegate 留接口） |
| Permission | §6 默认权限矩阵 |

### V2

- Shared Memory（含 Source 溯源 + 人审门禁）
- Project Context
- Agent Collaboration（CollaborationRequest 全链路）
- Agent Delegate（跨 Agent 委派路由）

### V3

- Agent Autonomous Task Delegation（Agent 自主任务委派）
- PR Review
- Code Execution

### V4

- AI Engineering Organization（AI 工程组织）

---

## 11. 明确暂缓项

| 项 | 原因 |
| --- | --- |
| 自动任务拆分（Parent Task → 子任务分派） | 属执行层编排，归 AgentBoard；MateOS 只透出视图 |
| Agent 自主协作（无人类参与的 Agent 间循环） | 防失控，V1 Non Goal |
| 自动 Memory Extraction | 记忆质量优先，先跑通人审门禁 |
| 细粒度 skills 标签体系 | V1 用 role + capabilities 足够 |

---

## 12. 核心实体清单（架构设计输入）

```
Organization / User / Team / Project / Channel / Member
Agent / Credential / Message / Mention / Memory
CollaborationRequest / Permission
```

---

## 13. 开放问题

- [ ] 产品名称最终确定（MateOS / MatePro / Crewly / Memora）
- [ ] Agent Runtime 的具体形态（本地进程 / 远端 Worker / 混合）
- [ ] Memory 的存储与检索方案（向量 / 结构化 / 混合）
- [ ] Permission Model 与 Decision Model 的交互细节（Permission Check 失败时走 Reject 还是 Need Context）
- [ ] CollaborationRequest 的协议格式（与 AgentBoard 既有 Task/Story 模型如何映射）

---

## 14. 后续步骤

1. 编写 **SYSTEM_DESIGN.md**（架构设计文档），重点设计：
   - 数据模型（基于 §12 实体清单）
   - Channel / Message / Mention / Mention Resolver 架构
   - Agent Runtime 接口与 Credential 管理
   - Memory 系统与 Source 溯源
   - CollaborationRequest 协议与 AgentBoard 边界
   - Permission Model 实现
2. 架构文档落点建议：`docs/design/` 目录

---

## 更新记录

| 版本 | 日期 | 变更 |
| --- | --- | --- |
| v0.1 | 2026-09-07 | 初稿，整理自外部 PRD 草案 |
| v0.2 | 2026-09-07 | 吸收外部架构师 review：新增 Project 实体与 Channel/Project 边界分离；V1 收敛为 AI Engineering Team Workspace 并增加 Non Goals；新增 Permission Model / User Story / 核心实体清单；Mention 升级为 Mention Resolver；Decision Model 增加 Delegate（MVP 实现前三种）；Agent Capability 结构化 + Credential 分离；Memory 增加 Source 溯源；FR-6 职责收敛为 CollaborationRequest |
