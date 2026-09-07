# MateOS 总体需求文档（PRD v0.1）

| 文档信息 | 内容 |
| --- | --- |
| 产品名称 | MateOS（备选：MatePro / Crewly / Memora） |
| 文档状态 | Draft（初稿，整理自外部 PRD 草案） |
| 版本 | v0.1 |
| 日期 | 2026-09-07 |
| 下游文档 | 架构设计文档（待建，见 §10 后续步骤） |

---

## 1. 产品定位

**MateOS 是一个面向软件开发团队的 AI 原生团队协作平台（AI Native Team Collaboration Platform）。**

它允许人类成员（Human）与 AI Agent 成员（AI Teammate）共同组成团队，在共享 Channel 中沟通、分配任务、共享知识，并协作完成软件开发工作。

核心理念：

> AI 不应该只是一个工具，而应该成为团队成员。

一句话定位：

> MateOS is an AI-native team operating system where humans and AI teammates collaborate through shared channels, memory, and autonomous workflows.

---

## 2. 产品目标：解决 AI Coding 的三个核心问题

### 2.1 单 Agent 孤岛问题

当前每个开发者各自持有独立的 AI Assistant，导致：

- 上下文不共享
- 知识无法沉淀
- 决策无法复用

MateOS 的解法：以 Team → Channel → Human + AI Agents → Shared Memory 的结构，把协作从"一对一工具"升级为"团队共享空间"。

### 2.2 团队知识无法积累

当前个人解决问题的产出（Chat history + Local knowledge）无法被团队复用。

MateOS 的解法：将架构决策、Feature 讨论、Bug 经验、Coding 规范沉淀为 **Project Shared Memory**。

### 2.3 AI Agent 缺少团队协作能力

当前 Agent 是 User → Prompt → Agent 的单点工具。MateOS 让 Agent 成为可协作的团队成员，能够：

- 接受任务
- 拒绝任务
- 请求更多上下文
- 与其他 Agent 协作
- Review 其他 Agent 的工作

---

## 3. 核心概念模型

| 概念 | 说明 |
| --- | --- |
| **Organization** | 组织，包含 Users、Teams、Projects |
| **Team** | 一个开发团队，成员由 Human 与 Agent 混合组成（如 Payment Team：Jason、Tom、Backend-Agent、QA-Agent） |
| **Channel** | 核心协作空间，类似 Slack / Teams Channel，本质是**一个共享上下文边界**。Channel 自动形成 Feature Memory |
| **Member** | 统一成员模型，type 为 HUMAN 或 AGENT，两者同为一等成员 |

---

## 4. 用户体系

### 4.1 Human User

能力：登录、创建 Team、创建 Agent、邀请成员、参与讨论。

### 4.2 AI Agent Member

Agent 是一等成员，核心属性：

```json
{
  "id": "",
  "type": "AGENT",
  "owner_user_id": "",
  "provider": "OpenAI/Anthropic",
  "model": "",
  "status": "FREE/BUSY/OFFLINE",
  "capabilities": ["coding", "review", "architecture"]
}
```

### 4.3 Agent Ownership

每个 Agent 必须属于一个 Human User（User owns Agent）。原因：

- API Key 隔离
- 成本统计
- 权限控制
- 责任归属

---

## 5. 功能需求

### FR-1 Agent 管理

- 用户创建自己的 Agent，需配置：Provider、API Key、Model、Runtime、Workspace
- 一个用户可拥有多个 Agent（如 Coding-Agent、Review-Agent、Architecture-Agent）

### FR-2 Channel 成员管理

- Channel 可邀请 Human 成员与 Agent 成员
- 邀请后成员进入该 Channel 的共享上下文

### FR-3 Agent 状态管理

Channel 中展示 Agent 状态，状态集合：

- `FREE` / `BUSY` / `THINKING` / `WAITING_CONTEXT` / `OFFLINE`

### FR-4 Mention 系统（核心交互方式）

| Mention 形式 | 行为 |
| --- | --- |
| `@指定成员` | 直接路由给该成员（Human 或 Agent） |
| `@channel-group`（如 @backend-team） | 系统按 capabilities 匹配并寻找合适的 Agent（Developer / QA / Security 等） |
| `@all` | 所有成员收到；Agent 自行判断是否需要响应，避免 Agent 无限聊天 |

### FR-5 Agent 决策模型（Decision Model）

Agent 收到请求后**不立即执行**，决策流程：

```
Message → Context Evaluation → Capability Check → Permission Check → Decision
```

Decision 三种结果：

1. **Accept** — 确认有能力、有上下文、有权限，开始执行
2. **Reject** — 明确说明拒绝原因（如缺少领域知识）
3. **Need Context** — 列出所需补充信息（如 API 规格、数据库设计）

### FR-6 Agent 协作（Collaboration）

- 多个 Agent 可围绕一个 Feature 协作
- 任务自动拆分为 Parent Task → 子任务分派给不同 Agent（Backend / Database / QA / Security 等）

### FR-7 Shared Memory（核心竞争力）

| Memory 类型 | 归属 | 示例 |
| --- | --- | --- |
| Personal Memory | 个人 | 用户偏好（如"Jason prefers clean architecture"） |
| Project Memory | 团队共享 | 项目事实（如"Payment uses Stripe webhook"） |
| Decision Memory | 团队共享 | 架构决策记录（如 ADR-001：为什么选 PostgreSQL） |
| Knowledge Memory | 团队共享 | 业务知识（如"Customer entity represents billing owner"） |

**Memory 生命周期（人审门禁）：**

1. Agent 提出记忆写入申请（"I learned: ... Save to project memory?"）
2. Human **Approve** 后才进入 Shared Memory

---

## 6. 与 AgentBoard 的集成边界

MateOS 与 AgentBoard 分层解耦：

| 层 | 系统 | 职责 |
| --- | --- | --- |
| **协作层（Collaboration Layer）** | MateOS | Communication、Team、Channel、Memory、Mention |
| **执行层（Execution Layer）** | AgentBoard | Task、Worker、Agent Runtime、Code Execution、Review |

```
        MateOS（协作层）
              |
         AgentBoard（执行层）
```

---

## 7. 版本规划

### MVP（V1）

| 模块 | 范围 |
| --- | --- |
| User | 注册、登录 |
| Agent | 创建 Agent、配置 API Key、状态管理 |
| Team | 创建团队 |
| Channel | 创建 Channel、邀请成员 |
| Chat | 消息收发 |
| Mention | @Human、@Agent |
| Agent Response | Accept / Reject / Need Context |

### V2

- Shared Memory
- Project Context
- Agent Collaboration

### V3

- Agent Autonomous Task Delegation（Agent 自主任务委派）
- PR Review
- Code Execution

### V4

- AI Engineering Organization（AI 工程组织）

---

## 8. 非功能性约束（待细化）

以下为初稿隐含约束，后续架构文档需明确：

- **Agent 权限与归属**：所有 Agent 行为可追溯到 owner_user_id
- **成本隔离**：API Key 按用户隔离，成本可统计
- **响应控制**：`@all` 场景下 Agent 须有响应判断机制，防止 Agent 无限聊天
- **记忆质量门禁**：写入 Shared Memory 必须经 Human Approve

---

## 9. 开放问题

- [ ] 产品名称最终确定（MateOS / MatePro / Crewly / Memora）
- [ ] Agent Runtime 的具体形态（本地进程 / 远端 Worker / 混合）
- [ ] Channel 与 Project 的关系（一个 Channel 是否绑定一个 Project）
- [ ] Memory 的存储与检索方案（向量 / 结构化 / 混合）
- [ ] Agent 决策模型中 Permission Check 的权限体系设计

---

## 10. 后续步骤

1. 编写 **MateOS 架构设计文档**，重点设计：
   - 数据模型
   - Channel / Message / Mention 架构
   - Agent Runtime 接口
   - Memory 系统
   - 与 AgentBoard 的边界
2. 架构文档落点建议：`docs/design/` 目录
