# MateOS

> AI-native team operating system where humans and AI teammates collaborate through shared channels, memory, and autonomous workflows.

MateOS 是一个面向软件开发团队的 AI 原生团队协作平台（V1 = AI Engineering Team Workspace）。人类成员与 AI Agent 成员在共享 Channel 中沟通、分配任务、沉淀记忆，决策可解释、知识可复用、人类始终握有闸门。

## 项目状态

| 维度 | 状态 |
| --- | --- |
| 阶段 | **需求与设计收口期**（尚未启动 M1 编码） |
| 文档 | PRD v0.4 / SYSTEM_DESIGN v0.5（技术栈已拍板 .NET）/ UI Design System v0.6 / detailed 00–09 v0.5 |
| 原型 | P3 Agent Card / P4 Project Dashboard / P5 + P5b Channel 主界面（评审修订 v0.4 全部落地） |
| 待做原型 | P0-Inbox / P0-Fleet（**M1 前必须出**）/ P1 登录注册 / P2 我的 Agents / P6 审批中心 / P7 Memory 文档 / P8 团队设置 |

## Single Source of Truth

- **Current Design 只有两处**：`docs/requirement/MateOS-总体需求文档.md`（PRD）+ `docs/design/SYSTEM_DESIGN.md`。其他一切（detailed / epic / 原型）都是它们的展开，**与之冲突时以这两份为准并回改子文档**。
- `docs/design/detailed/00–09` = Current implementation spec。
- `docs/design/future/` = **V2 愿景，不作 M1–M9 依据**（`autonomous-delivery/` 已于 2026-09-10 迁入）。
- `docs/spec/domain-model-v0.3.md` = 领域模型（产品主语 = Agent；`Execution.work_item_ref` 可为 NULL）。
- 评审/提案类文档放 `docs/review/`，只记录结论与待拍板项，不是 spec。

## 文档结构

```
docs/
├── requirement/
│   ├── MateOS-总体需求文档.md        # PRD v0.4（单一事实源）
│   └── epic/                         # E1–E10 Epic（PRD 的展开）
├── design/
│   ├── SYSTEM_DESIGN.md              # v0.5（单一事实源；技术栈已拍板 .NET）
│   ├── MateOS-architecture.html      # 架构图（只读展示，随 SYSTEM_DESIGN 更新）
│   ├── detailed/                     # Current implementation spec
│   │   ├── 00-overview.md … 09-implementation-checklist.md
│   └── future/                       # V2 愿景，不作 M1–M9 依据
│       └── autonomous-delivery/      # Mission / WorkUnit / Scheduler（保留但冻结）
├── spec/
│   └── domain-model-v0.3.md          # 领域模型 + 不变量（I1–I10）
├── review/                           # 评审结论与待拍板项
└── UI design/
    ├── MateOS-UI-Design-System-v0.2.md  # DS v0.6（文件名保留 v0.2）
    ├── tokens.css                    # 设计令牌单一事实来源
    ├── P3-agent-card.html            # 一等成员详情页
    ├── P4-project-dashboard.html     # 项目工作台
    ├── P5-channel-prototype.html     # Channel 主界面（4 栏 + 5 形态消息流）
    └── P5b-topic-channel.html        # Topic 线程 + 共享上下文带
```

## 核心概念

- **Organization → Team → Project → Channel → Member(Human|Agent)** — 五层实体模型
- **Channel = communication boundary, Project = knowledge boundary** — 通信与知识边界分离
- **6 态状态机**（PRD / SYSTEM_DESIGN / UI DS 单一事实来源）：`OFFLINE / AVAILABLE / THINKING / WORKING / WAITING_CONTEXT / ERROR`
- **Mention Resolver** — `@成员 / @能力组 / @all` 三种提及形态，能力排序 + 命中结果前端可见
- **Decision 状态机** — `Accept / Reject / Need Context`（MVP 三种），Delegate 留 V2
- **Shared Memory 人审门禁** — Agent 申请 → 人类批准 → 进入共享记忆；每条记忆必带 Source 溯源（PRD FR-7 硬性要求）
- **执行层永久自洽** — 只有 MateOS Runtime；与 AgentBoard 集成已永久移出 scope（PRD §8 Non Goals）。项目管理默认 Built-in，Jira 为 V1+ 可选 Provider（**不是**执行后端）。
- **MateOS = Agent-centered Team OS** — 产品主语是 Agent：首页 = Fleet + Inbox；WorkItem 是 Agent 的工作对象，不是产品主干。

## 技术选型（SYSTEM_DESIGN v0.5 · 2026-09-10 已拍板）

- 前端：Next.js + TypeScript + antd 6 + Zustand + TanStack Query
- 后端：**ASP.NET Core（.NET 10 LTS, C#）** — 模块化单体 + `BackgroundService` relay
- ORM / 迁移：EF Core 10（Npgsql）+ 显式 SQL 迁移
- 数据库：PostgreSQL 16 + pgvector
- 缓存：Redis 7（presence / pub/sub / 限流）
- 异步：**PostgreSQL transactional outbox + `SKIP LOCKED` relay**（BullMQ 已从 Current Design 移除）
- 沙箱：Docker 容器（V3 启用，架构预留）
- 部署：Docker Compose → K8s
- 可观测：M1 起只做结构化日志 + trace_id + audit + `/metrics`；OTel + Prometheus + Grafana + Loki 推到 M8

> 协议层（Connector / REST / envelope）保持**技术中立**：只定义语义，不绑定实现语言，避免伪代码反向绑架架构。

## 实施顺序（MVP）

以 `docs/design/detailed/09-implementation-checklist.md` 的 **M1–M9** 为 Current 基线（本 README 早期的 M1–M6 已作废）。

| 阶段 | 交付 | Epic |
| --- | --- | --- |
| M1 | 工程骨架 + auth/JWT + Org/Team/Project/Member | E1 |
| M2 | Channel + Message + seq + WS 网关 + outbox relay | E3 |
| M3a | Agent + Credential + lifecycle/activity/health | E2 |
| M3b | Connector transport（`collaboration.request` / dispatch / `dispatch_ack`） | E7 部分 |
| M4a | Permission + Approval 三态（8 键） | E6 |
| M4b | Trigger + CollaborationRequest + Resolver + Decision | E4 |
| M5 | Memory 人审门禁 + 索引 | E5 |
| M6 | WorkItem + Built-in Provider + Work 页面 | E8 |
| M7 | Agent Execution domain 完整（attempts/events/artifacts/resume） | E7 |
| M8 | Observability + Dashboard + 压测 | E10 |
| M9 | 集成 + 端到端 | 全部 |
| V1+ | Jira Provider 适配器 | E9 |

> ⚠ 评审已建议改为 **S1/S2/S3 竖切**（@mention→产出 / 记忆闸门 / 工作推进），**尚未拍板**（见 `docs/review/`）。首个纵向闭环的 Agent 端提案见 detailed/09 §2.1。

## 工作约定

- 单人项目，直推 `main` 分支（无 PR 流程）
- 文档与原型是同源物，跨文档修订必须一次性收敛（不留互相矛盾）
- 评审流程：先看 `docs/requirement/` → 再看 `docs/design/` → 最后看 `docs/UI design/`
- 凭据、API Key 等敏感信息走 `User → Credential → Agent Runtime` 分离模型，绝不回显明文

## License

MIT — see `LICENSE`.
