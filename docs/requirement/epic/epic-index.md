# MateOS Epic 总览

> 详细需求见同目录下 `E1-...md` ~ `E9-...md`。本文档提供全局索引与依赖关系。

| Epic | 标题 | 阶段 | 主依赖 | 文档 |
| --- | --- | --- | --- | --- |
| **E1** | User & Team Management | MVP（M1） | — | [E1-user-team-management.md](./E1-user-team-management.md) |
| **E2** | Agent as First-Class Member | MVP（M3） | E1 | [E2-agent-first-class-member.md](./E2-agent-first-class-member.md) |
| **E3** | Channel & Messaging | MVP（M2） | E1 | [E3-channel-messaging.md](./E3-channel-messaging.md) |
| **E4** | Mention Resolver & Decision Engine | MVP（M4） | E2, E3, E6 | [E4-mention-resolver-decision.md](./E4-mention-resolver-decision.md) |
| **E5** | Shared Memory & Knowledge Base | MVP（M5） | E1, E3, E6 | [E5-shared-memory.md](./E5-shared-memory.md) |
| **E6** | Permission & Access Control | MVP（M4 同步） | E1 | [E6-permission-access-control.md](./E6-permission-access-control.md) |
| **E7** | Agent Runtime & Connector Protocol | MVP（M3-M4） | E2 | [E7-agent-runtime-connector.md](./E7-agent-runtime-connector.md) |
| **E8** | Observability & Operations | MVP（M6） | E1-E7 全部 | [E8-observability-operations.md](./E8-observability-operations.md) |
| **E9** | External Integration Extensions | V1+ | E2, E4 | [E9-external-integration.md](./E9-external-integration.md) |

## 实施顺序（M1-M6，对齐 SYSTEM_DESIGN §13）

```
M1 ─► E1 (基础 + auth)
M2 ─► E3 (消息 + WS 网关)
M3 ─► E2 + E7 (Agent + Connector 心跳)
M4 ─► E6 + E4 (权限 + Mention/Decision)
M5 ─► E5 (Memory 人审门禁)
M6 ─► E8 (打磨 + 监控)
V1+ ─► E9 (外部集成)
```

## 依赖矩阵

|        | E1 | E2 | E3 | E4 | E5 | E6 | E7 | E8 | E9 |
| ------ | -- | -- | -- | -- | -- | -- | -- | -- | -- |
| **E1** | — | 提供 owner_user_id | 提供 sender | — | 提供 owner | 提供 subject | — | 提供 actor | — |
| **E2** |    | —  | 提供 Agent 成员 | 提供 Decision agent | — | 提供 can_execute / can_review | 提供 agent 实体 | — | 提供切换开关 |
| **E3** |    |    | —  | 提供 message / mention 载体 | 提供 memory 消息流 | 提供 channel 范围 | — | — | — |
| **E4** |    |    |    | —  | — | 决策依据含 permission | 派发任务给 Runtime | — | 协作 request 协议 |
| **E5** |    |    |    |    | —  | write memory = request | — | — | 双向同步 |
| **E6** |    |    |    |    |    | —  | — | 提供 audit actor | — |
| **E7** |    |    |    |    |    |    | —  | 6 态 + ERROR 触发 | — |
| **E8** |    |    |    |    |    |    |    | —  | — |
| **E9** |    |    |    |    |    |    |    |    | —  |

## 与现有文档的对应

| 文档 | 角色 |
| --- | --- |
| `../MateOS-总体需求文档.md`（PRD v0.3） | 全局需求与版本规划，**本目录是 PRD 各章的细化** |
| `../../design/SYSTEM_DESIGN.md` v0.2 | 全局架构与数据模型，**本目录的 API/数据模型字段与之一致** |
| `../../UI design/MateOS-UI-Design-System-v0.2.md` v0.4 | 全局 UI 规范，**本目录的 UI 章节引用其 §3-§7** |

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

全部 9 篇 `Draft`，待评审。验收标准中凡有 `E2E：` 前缀的项都将被纳入 M6 之后的 e2e 套件。
