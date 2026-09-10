# Detailed Design · Overview

> 配套：PRD v0.4 / SYSTEM_DESIGN v0.3.2 / UI DS v0.6 / E1-E10 epic docs。
> 本目录是 **implementation spec**——比 epic 更细，比代码更抽象。
> 每篇独立可读，但引用前置依赖。

## 设计目录

| # | 文档 | 关键问题 |
| --- | --- | --- |
| [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md) | **单个 Agent 接受任务 → 处理 → 回复** 一整套 | Agent 内部状态机、状态广播时序、消息流投影 |
| [02-interrupt-and-cancel.md](./02-interrupt-and-cancel.md) | **能否被打断** | Cancel 路径（用户/lifecycle/超时）、WS 断线、Runtime 重启、Agent 进程崩溃 |
| [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md) | **WS 连接生命周期 + resume 协议** | Hello / heartbeat / dispatch / event / result / error / resume_request / resume_ack |
| [04-resolver-and-routing.md](./04-resolver-and-routing.md) | **Resolver 调度 + Slot 原子 reservation** | Lua tryAcquireSlot、Top-N、60s 超时重路由、Cancel 传播 |
| [05-memory-approval-flow.md](./05-memory-approval-flow.md) | **E5 Memory 人审门禁** | Agent 申请 → 人类审批 → Source 溯源 → projection 消息流 |
| [06-workitem-provider-sync.md](./06-workitem-provider-sync.md) | **WorkItem ↔ Provider 双向同步** | Built-in / Jira / 状态映射 / 冲突检测 / webhook 30 天 refresh |
| [07-permission-and-approval-orchestration.md](./07-permission-and-approval-orchestration.md) | **E6 Permission 三态 + policy.evaluate** | checkPermission / Guard / cache / invalidation |
| [08-error-and-retry.md](./08-error-and-retry.md) | **ERROR 触发 + 重试 + 退避 + 降级** | Provider 5xx / 401 / 限频 / 永久失败 |
| [09-implementation-checklist.md](./09-implementation-checklist.md) | **S1/S2/S3 竖切实施顺序 + 能力域标签 + DoD** | 先做哪一刀、阻塞关系、每刀验收 |
| [10-agent-stub-and-sdk.md](./10-agent-stub-and-sdk.md) | **S1 的 Agent 端：stub + SDK 契约** | 协议面、B1–B8 行为矩阵、与 Runtime 的边界 |

## 全局架构图（一致于 SYSTEM_DESIGN v0.3.2）

```
                       Client (Web / Desktop)
                              │
                ┌─────────────┴─────────────┐
                │                           │
              REST /api/v1              WSS /ws
                │                           │
                ▼                           ▼
    ┌─────────────────────────┐   ┌─────────────────────┐
    │ ASP.NET Core (monolith) │   │ WS Gateway + Outbox Relay │
    │                         │   │  (event fanout)        │
    │  ┌───────────────────┐  │   └─────────────────────┘
    │  │ Modules:          │  │              │
    │  │  auth/iam         │  │              │
    │  │  project/member   │  │              │
    │  │  channel/message  │  │              │
    │  │  work-item        │  │              │
    │  │  permission       │  │              │
    │  └───────────────────┘  │              │
    └─────────────┬───────────┘              │
                  │                          │
       ┌──────────┼──────────┬───────────────┼──────────────┐
       │          │          │               │              │
       ▼          ▼          ▼               ▼              ▼
   ┌──────┐  ┌──────┐  ┌────────┐  ┌────────────┐  ┌────────────┐
   │auth/ │  │channel│  │ memory │  │  agent     │  │ work-item  │
   │iam   │  │      │  │ worker │  │  runtime   │  │  (E8 core) │
   └──────┘  └──────┘  └────────┘  │  gateway   │  └────────────┘
                                   │  (E7)      │
                                   └─────┬──────┘
                                         │ WSS Connector
                                         ▼
                                ┌────────────────────┐
                                │  External Agent     │
                                │  Process            │
                                │  (Coding/Review/...) │
                                └────────────────────┘

   ┌──────────────────────────────────────────────────────┐
   │  Infrastructure: PostgreSQL 16 (pgvector) │ Redis 7  │
   │  - Single source of truth: agent_executions / decision_records / memory_* / work_items
   │  - Redis: presence / pub-sub / atomic slot semaphore (Lua)
   └──────────────────────────────────────────────────────┘
```

## 三条 lifecycle 边界（不再混淆）

| Lifecycle | 起点 | 终点事实源 | 状态枚举 |
| --- | --- | --- | --- |
| **Collaboration** | Trigger (MENTION/WORK_ITEM/API/AUTOMATION) | `collaboration_requests` | PENDING / ACCEPTED / REJECTED / NEED_CONTEXT / UNRESOLVED / CANCELLED |
| **WorkItem** | WorkItem CRUD | `work_items` | OPEN / IN_PROGRESS / IN_REVIEW / DONE / CLOSED |
| **Agent Execution** | E4 调 E7 API 创建 | `agent_executions` | PENDING / RUNNING / SUCCEEDED / FAILED / CANCELLED / TIMEOUT |

UI 通过 E7 拉 execution status 做 `display_state="EXECUTING"` projection，**不再**让 CollaborationRequest.status 镜像 Execution status。

## 设计原则

1. **DB 是事实源**——Redis 仅做 scheduling state（slot semaphore、presence、缓存），不持久化
2. **WS reconnect ≠ new attempt**——attempt 是 logical 重试单位
3. **envelope.id 全局去重**——terminal message（result/error/cancel）必须保证 exactly-once
4. **CAS 状态转换**——所有终态变更 WHERE status IN (PENDING, RUNNING)
5. **ProviderKey = string**——Registry 模式，零硬编码
6. **Connection 复用**——Org/Owner 级，多 Project 共享
7. **Guard 不静默放行**——REQUIRE_APPROVAL 视为 403，业务层显式 policy.evaluate
8. **Slot 提前 reservation**——Resolver 选 Agent 后立即 tryAcquireSlot，不等 ACCEPT

## 关键术语

| 术语 | 含义 |
| --- | --- |
| **Trigger** | 协作起点（MENTION / WORK_ITEM / API / AUTOMATION） |
| **CollaborationRequest** | 协作一等实体（v0.4.1 引入） |
| **Decision** | Agent 对 CollaborationRequest 的回应（ACCEPT/REJECT/NEED_CONTEXT/DELEGATE） |
| **Execution** | Agent 一次具体执行（v0.4 引入，可独立于 WorkItem） |
| **Attempt** | Execution 内的 logical 重试单位（≠ WS session） |
| **Event** | Execution 流式事件（STDOUT/PROGRESS/TOOL_CALL/LLM_TICK/ARTIFACT/ERROR） |
| **Slot** | Agent capacity 配额单位（max_concurrency） |
| **Lease** | 一次 Slot reservation 的引用标识 |
| **Provider** | WorkItem 后端（Built-in / Jira / Linear ...） |
| **Binding** | Project × Provider 的活跃关联 |
| **Connection** | Provider 的 OAuth/凭据容器（Org/Owner 级） |

## 必读顺序

如需理解 single agent 完整流程，**先读 01**，再看 02（interrupt）和 03（WS）。
如需理解 work item 与 execution 关系，**先读 04**（resolver），再看 06（provider sync）。
如需理解人审门禁，**直接读 05**。
