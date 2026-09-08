# MateOS

> AI-native team operating system where humans and AI teammates collaborate through shared channels, memory, and autonomous workflows.

MateOS 是一个面向软件开发团队的 AI 原生团队协作平台（V1 = AI Engineering Team Workspace）。人类成员与 AI Agent 成员在共享 Channel 中沟通、分配任务、沉淀记忆，决策可解释、知识可复用、人类始终握有闸门。

## 项目状态

| 维度 | 状态 |
| --- | --- |
| 阶段 | **需求与设计期**（尚未启动 M1 编码） |
| 文档 | PRD v0.3 / SYSTEM_DESIGN v0.2 / UI Design System v0.4 已发布 |
| 原型 | P3 Agent Card / P4 Project Dashboard / P5 Channel 主界面（三页一物，评审修订 v0.4 全部落地） |
| 待做原型 | P1 登录注册 / P2 我的 Agents / P6 审批中心 / P7 Memory 文档 / P8 团队设置 |

## 文档结构

```
docs/
├── requirement/
│   └── MateOS-总体需求文档.md        # PRD v0.3
├── design/
│   └── SYSTEM_DESIGN.md              # v0.2（独立自洽 + 可选外部集成扩展点）
└── UI design/
    ├── MateOS-UI-Design-System-v0.2.md  # DS v0.4（文件名保留 v0.2）
    ├── tokens.css                    # 设计令牌单一事实来源
    ├── P3-agent-card.html            # 一等成员详情页
    ├── P4-project-dashboard.html     # 项目工作台
    └── P5-channel-prototype.html     # Channel 主界面（4 栏 + 5 形态消息流）
```

## 核心概念

- **Organization → Team → Project → Channel → Member(Human|Agent)** — 五层实体模型
- **Channel = communication boundary, Project = knowledge boundary** — 通信与知识边界分离
- **6 态状态机**（PRD / SYSTEM_DESIGN / UI DS 单一事实来源）：`OFFLINE / AVAILABLE / THINKING / WORKING / WAITING_CONTEXT / ERROR`
- **Mention Resolver** — `@成员 / @能力组 / @all` 三种提及形态，能力排序 + 命中结果前端可见
- **Decision 状态机** — `Accept / Reject / Need Context`（MVP 三种），Delegate 留 V2
- **Shared Memory 人审门禁** — Agent 申请 → 人类批准 → 进入共享记忆；每条记忆必带 Source 溯源（PRD FR-7 硬性要求）
- **执行层自洽 + 可选协作扩展点** — 默认 MateOS Runtime；可切换到 AgentBoard；项目管理可切换 AgentBoard Issue / Jira Issue（均默认关闭）

## 技术选型（SYSTEM_DESIGN v0.2）

- 前端：Next.js + TypeScript + antd 6 + Zustand + TanStack Query
- 后端：NestJS（Node 22 LTS，TypeScript，模块化单体）
- 数据库：PostgreSQL 16 + pgvector
- 缓存 / 队列：Redis 7 + BullMQ
- 沙箱：Docker 容器（V3 启用，架构预留）
- 部署：Docker Compose → K8s
- 可观测：OpenTelemetry + Prometheus + Grafana + Loki

## 实施顺序（MVP）

| 阶段 | 交付 |
| --- | --- |
| M1 | monorepo + auth/JWT + org/team/project/channel CRUD |
| M2 | 消息收发 + WS 网关 + 附件直传 |
| M3 | Agent CRUD + Credential 加密 + presence（6 态） |
| M4 | Mention 存储 + Resolver + Decision 状态机 + 权限 Guard |
| M5 | Memory 人审门禁 + Source 溯源 + P6 审批中心 + embedding 索引 |
| M6 | @all 仲裁 + 通知 + 审计 + 监控面板 + 压测 |

## 工作约定

- 单人项目，直推 `main` 分支（无 PR 流程）
- 文档与原型是同源物，跨文档修订必须一次性收敛（不留互相矛盾）
- 评审流程：先看 `docs/requirement/` → 再看 `docs/design/` → 最后看 `docs/UI design/`
- 凭据、API Key 等敏感信息走 `User → Credential → Agent Runtime` 分离模型，绝不回显明文

## License

MIT — see `LICENSE`.
