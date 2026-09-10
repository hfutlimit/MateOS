# MateOS 系统架构设计（SYSTEM_DESIGN v0.5）

| 文档信息 | 内容 |
| --- | --- |
| 上游文档 | docs/requirement/MateOS-总体需求文档.md（PRD v0.4） |
| 文档状态 | Draft |
| 版本 | **v0.5** |
| 日期 | 2026-09-10 |
| 本版要点 | v0.4.5 正文回修（8 处硬冲突）+ v0.5 技术栈拍板 .NET / capacity rebuild fencing / webhook durable inbox / dispatch_ack / outbox_events 与 DDL 收口 —— 明细见文末「更新记录」，历史推理见 `docs/review/` |
| 前提 | **MateOS 为独立自洽系统**：Agent Execution 域自研（永久不切到任何外部执行后端）；Work Management 是独立域（Built-in + Jira Provider 抽象，与 Execution 完全解耦） |

> **v0.2 → v0.3 变更摘要**（架构评审后推倒重来）：
> 1. **删除 AgentBoard execution backend**：Architecture diagram、Project schema、Runtime dispatch path 全清
> 2. **新增 Agent Execution domain（§6）**：`agent_executions` / `execution_attempts` / `execution_events` / `execution_artifacts`；异步队列（v0.5 起 = outbox relay）不再是 source of truth
> 3. **新增 Work Management 域（§9）**：`WorkItem` / `WorkItemBinding` / Provider 抽象（Built-in + Jira）；Project 不再存 `integration_backend` / `issue_tracker`
> 4. **新增 CollaborationRequest 一等实体（§4.2）**：Trigger → CollaborationRequest → Decision → Execution
> 5. **Permission effect `REQUEST` → `REQUIRE_APPROVAL`**：与 CollaborationRequest / HTTP Request 概念分离
> 6. **Agent lifecycle / activity 拆分**：lifecycle=ACTIVE/PAUSED/DISABLED，activity=OFFLINE/.../ERROR
> 7. **删除 `can_execute` / `can_review` 字段**：Capability（能不能）+ Permission（允不允许）单一事实源
> 8. **消息流改 projection**：`messages.content_type` 5 形态保留，DECISION/MEMORY_REQUEST 形态只引 `entity_ref`
>
> **v0.3 → v0.3.1 协议收口**（E4/E7 边界冻结）：
> 9. **E4 / E7 协议彻底拆开**：`collaboration.*`（E4 拥有）vs `execution.*`（E7 拥有）；`execution.result` 不携带 decision；`collaboration.decision` 不携带 execution_id
> 10. **execution_id 由 E4 Orchestrator → E7 API 产生**（不是 Agent 自报），消除 v0.4 的不可能时序
> 11. **CollaborationRequest.status 收敛**为 `PENDING|ACCEPTED|REJECTED|NEED_CONTEXT|UNRESOLVED|CANCELLED`（去掉 EXECUTING/COMPLETED/FAILED，Execution 状态由 E7 维护）
> 12. **Resolver 调度改用 lifecycle=ACTIVE + slot 占用 < max_concurrency**（不再用 activity；activity 仅做 UI derived）—— v0.7 起 slot 占用判定为 Redis **per-lease ZSET**，见 §4.2.1
> 13. **Attempt ≠ WS session**：reconnect 不新建 attempt；新增 `execution.resume` 协议
> 14. **Event 协议级幂等**：`provider_event_id` + UNIQUE(attempt_id, provider_event_id)
> 15. **E8 简化**：`work_item_projections` 表删除；`work_items` 单表含 `search_text` / `provider_status` / `provider_meta`
> 16. **ProviderKey 去硬编码**：`type ProviderKey = string` + `ProviderRegistry` 模式
> 17. **DB UNIQUE 强制 active binding 唯一**：`work_item_bindings(project_id) WHERE is_active=true`
> 18. **Provider 路由规则冻结**：CREATE 用 `activeBinding.provider_key`；UPDATE 用 `work_item.provider_key`
> 19. **Jira Status Mapping 动态**：`listStatuses(binding)` 而非 Provider 级静态模板
>
> **v0.3.1 → v0.3.2 并发与幂等收口**（架构评审后）：
> 20. **Capacity 原子化**：旧方案 `agents.max_concurrency + agents.active_slots` 是 DB + Redis 双事实源，存在 SELECT → accept → INSERT 的 race。**新方案**：仅 `max_concurrency` 落 DB（durable config），slot 占用归 Redis Lua 原子操作。**v0.7 定稿**：per-lease **ZSET** `agent-capacity:{agent_id}:leases`（member=lease_id，score=expires_at）+ `fence`/`rebuilding`；中间 Hash 版（`used` + 整 key EXPIRE）已废弃 —— 详见 §4.2.1
> 21. **Slot 提前 reservation**：Resolver 选 Agent → 立即 tryAcquireSlot → 成功才 dispatch；不等到 ACCEPT 后再 acquire（语义"我可以但 Runtime 拒绝"很奇怪）
> 22. **`agents.active_slots` 字段删除**：E2 删字段，状态归 Redis
> 23. **resume 协议反向**（E7 改）：旧 `execution.resume` 是 Agent 推 last_event_seq、Runtime 补发——但 event 是 Agent 产，Runtime 补发是错的。新协议 `execution.resume_request`（Agent 问）→ `execution.resume_ack`（Runtime 答 `last_persisted_seq`）→ Agent 从 43 续发
> 24. **Terminal CAS 幂等**（E7 改）：`UPDATE agent_executions SET status=? WHERE id=? AND status IN ('PENDING','RUNNING')`；affected_rows=0 表示 stale/duplicate → 忽略；envelope `id` 落 `terminal_envelope_id` 字段用于去重
> 25. **`execution_events.attempt_id` NOT NULL**：旧允许 NULL 时 UNIQUE(attempt_id, ...) 因 PostgreSQL 多 NULL 行为不触发，重复事件漏去重
> 26. **`execution_artifacts.attempt_id` NOT NULL**：同 25
> 27. **Permission Guard 拆**（E6 改）：旧 Guard 收到 `REQUIRE_APPROVAL` 静默放行让业务层"忘记"审批。**新 Guard 只决 ALLOW/DENY**，`REQUIRE_APPROVAL` 视为 403；业务层显式 `policy.evaluate()` 走 E5 / E4 门禁
> 28. **Work Management Connection 解耦**（E8 + E9 改）：`work_management_connections` 从 `(project_id, user_id)` 改为 `(org_id, owner_user_id)`，一个 Org 可有多个 Jira Connection；`work_item_bindings.connection_id` 引用
> 29. **`JiraProvider.getSelfMetadata()` 删除静态 status 模板**：只返回 capability 描述；status mapping 通过 `listStatuses(binding)` 动态拉
> 30. **Webhook 注册 + 定期 refresh**（E9 新增）：Jira Cloud 动态 webhook 30 天过期；scheduler 在 webhook_expires_at < now+7d 时调官方 `PUT /rest/api/3/webhook/refresh`
> 31. **删除"Busy → 自动 NEED_CONTEXT"行为**（UI DS v0.6）：v0.4.2 修复 Resolver 后 Busy Agent 直接被跳过，不再产生"我很忙"伪造决策
>
> **v0.4.5 → v0.5 变更摘要**（= 正文回修 + 决策落地，明细见 §12 更新记录）：
> 32. **正文回修 8 处硬冲突**：CR 状态收敛 6 态 / Resolver 改 `lifecycle ∩ slot ∩ 有效权限`（activity 不参与）/ `work_item_projections` 三处删除 / Connector 改 `resume_request`+`resume_ack` / `execution_events` 幂等 DDL / `work_management_connections` 改 `(org_id, owner_user_id)` / `getSelfMetadata` 口径（删模板不删方法）/ `messages` 去分区（E3）；权限由 7 键改 **8 键**（新增 `propose_memory`）
> 33. **技术栈拍板（2026-09-10，owner 裁决）**：后台 = **ASP.NET Core（.NET 10）**；异步 = **PostgreSQL transactional outbox + `SKIP LOCKED` relay**，**BullMQ 移出 Current Design**；协议层保持技术中立
> 34. **`outbox_events` 收口**：从 detailed 的重复定义上移到本文件 §5.2，补 `status ∈ {PENDING, PUBLISHED, DEAD}` 与 relay 领取索引；延迟重试/超时统一走 `next_attempt_at`（取代 MQ delayed job）
> 35. **DDL 补列**：`agents.max_concurrency / health`、`agent_executions.active_attempt_no / terminal_envelope_id`、`execution_attempts.status CHECK + dispatch_sent_at / dispatch_acked_at / last_persisted_seq`、`work_items.binding_id / provider_* / search_text`
> 36. **`execution.dispatch_ack` 独立成协议消息**：`status` 回归纯 activity，ACK 与 UI 状态解耦（`03` §2 / §7.2）
> 37. **Jira webhook 收口**：`webhook_inbox` durable inbox（去重与持久化同事务）+ Bearer **JWT 验签**（app client secret，挂 connection 级）

---

## 1. 架构总览

### 1.1 分层视图

```
┌─────────────────────────────────────────────────────┐
│  Client（Web / Desktop）                            │
│  Next.js SPA — REST + WebSocket                     │
└───────────────┬─────────────────────┬───────────────┘
                │ HTTPS /api/v1        │ WSS /ws
┌───────────────▼─────────────────────▼───────────────┐
│  Application 层（ASP.NET Core Modular Monolith）     │
│  auth │ org/team/project │ channel │ mention │ memory│
│  collaboration │ permission │ agent-registry       │
│  execution │ work-management │ runtime              │
└───────┬──────────────┬───────────────┬──────────────┘
        │               │               │
┌───────▼──────┐ ┌──────▼───────┐ ┌─────▼─────────────┐
│ Orchestrator │ │ Memory       │ │ Agent Runtime     │
│ Worker       │ │ Worker       │ │ Gateway + Sandbox │
│ (Trigger →   │ │ (索引/检索/   │ │ (Connector +      │
│  Req → Dec)  │ │  门禁)       │ │  Execution Domain)│
└───────┬──────┘ └──────┬───────┘ └─────┬────────────┘
        └───────────────┼───────────────┘
                        │
      ┌─────────────────▼──────────────────┐
      │  基础设施：PostgreSQL16(+pgvector)   │
      │  Redis 7 │ S3/MinIO │ Docker       │
      └────────────────────────────────────┘
```

### 1.2 三大独立 lifecycle 域

| 域 | 入口 | 终点 | 事实源 |
| --- | --- | --- | --- |
| **Collaboration** | Trigger（MENTION / WORK_ITEM / API / AUTOMATION） | Execution | `collaboration_requests` + `decision_records` |
| **Work Management** | WorkItem CRUD | DONE / CLOSED | `work_items`（Built-in）或 Jira（Provider） |
| **Agent Execution** | CollaborationRequest.decision=ACCEPT | Artifact 落库 | `agent_executions` / `attempts` / `events` / `artifacts` |

**互不绑定**：WorkItem 可无 Execution（人类编辑），Execution 可无 WorkItem（`@backend 看下代码`），Memory 引用可选填 WorkItem 关联。

### 1.3 演进策略

MVP 不做微服务。单体 + 独立 Worker 进程；触发以下任一条件再拆分：
- WS 并发 > 5k
- Memory 索引/检索成 CPU 热点
- 模块所有权清晰且团队 > 8 人

---

## 2. 技术选型

| 领域 | 选型 | 理由 |
| --- | --- | --- |
| 前端 | Next.js + TypeScript + antd 6 | 团队栈；TanStack Query 管 WS 缓存 |
| 后端 | **ASP.NET Core（.NET 10 LTS，C#）** | 模块化匹配 Permission + Domain 分层；`BackgroundService` 承载 outbox relay |
| ORM / 迁移 | EF Core 10（Npgsql）+ FluentMigrator 风格的显式 SQL 迁移 | DDL 是本设计的硬约束（分区/唯一约束/CK），需要真实迁移而非 schema-first 推断 |
| DB | PostgreSQL 16 + pgvector | 关系 + JSONB + 全文 + 向量一库 |
| 缓存 / 异步 | Redis 7（presence / pub/sub / 限流）+ **PostgreSQL transactional outbox + `SELECT ... FOR UPDATE SKIP LOCKED` relay** | 异步不引入独立 MQ：outbox 与业务同事务提交，relay 只做 transport |
| 对象存储 | S3 / MinIO | 附件 + Execution Artifact |
| LLM | Provider Adapter（OpenAI-compatible） | Credential 分离 |
| 沙箱 | Docker（V3） | V1 不执行代码，架构预留 |
| 部署 | Docker Compose → K8s | |
| 可观测 | OTel + Prometheus + Grafana + Loki | |

> ✅ **技术栈已拍板（v0.5 · 2026-09-10，owner 裁决）**
> **后台 = ASP.NET Core（.NET 10）+ PostgreSQL 16 + transactional outbox + `BackgroundService` relay。BullMQ 从 Current Design 全部移除。**
> 裁决要点：
> - 异步不再依赖独立 MQ。所有跨模块副作用走 `outbox_events`，由 relay 用 `SELECT ... FOR UPDATE SKIP LOCKED` 领取并投递；重试用 `next_attempt_at`，超阈值置 `DEAD`。（BullMQ 的等价物，见 id=187 口径）
> - **协议层保持技术中立**：Connector 协议、REST 契约、消息 envelope 只描述语义，不绑定语言；detailed 里的伪代码是**语言无关的示意**，不得反向绑架架构。
> - 已同步：本表 / `detailed/09` §3.1 / `detailed/03` §8 / 全部原 BullMQ 表述（改为 outbox relay）/ `README.md`。
> - AgentBoard 文档 id=187 首页「后台 .NET 已裁决」据此确认为有效裁决。

---

## 3. 服务拆分

```
apps/
  web/              # Next.js 前端
  api/              # ASP.NET Core 单体（含 WS 中间件 + BackgroundService relay）
packages/
  contracts/        # OpenAPI + JSON Schema（WorkItem / Execution / Connector envelope），前端 TS 类型由契约生成
  config/           # eslint/tsconfig + EditorConfig
services/
  orchestrator/     # Trigger → CollaborationRequest → Decision
  memory/           # Memory 索引/检索/审批
  work-management/  # WorkItem 域 + Provider 适配（Built-in + Jira）
  runtime/          # Agent Runtime Gateway + Execution Domain
```

### 3.1 模块职责

| 模块 | 职责 | 不负责 |
| --- | --- | --- |
| auth/iam | 注册 / JWT / Org / Team / Project / Member | Channel scope 权限（归 permission） |
| channel | Channel CRUD / 消息读写 / seq 分配 / 投影 | Trigger 解析（归 orchestrator） |
| mention | Mention 提取 + 投递触发 | Resolver（归 orchestrator） |
| **collaboration** | Trigger → CollaborationRequest / Decision / 超时重路由 | Execution（归 runtime） |
| orchestrator | Resolver（lifecycle ∩ slot 过滤，activity 不参与）/ Decision 状态机 | |
| memory | Memory CRUD / Source 溯源 / 人审门禁 / 索引 | 消息存储 |
| permission | 8 键 + 3 态（ALLOW/DENY/REQUIRE_APPROVAL）+ 三层覆盖 | UI 配置 |
| agent-registry | Agent CRUD / Credential 绑定 / lifecycle+activity | Execution（归 runtime） |
| **runtime** | Connector 协议 / Execution / Attempt / Event / Artifact / 心跳 | 业务决策（归 orchestrator） |
| **work-management** | WorkItem 域 / Built-in + Provider 抽象 | Project 本身 |
| task | （v0.4 起废弃）| |

---

## 4. 核心流程

### 4.1 Trigger → CollaborationRequest → Decision

```
Trigger Source
  ├─ MENTION（消息流 @）
  ├─ WORK_ITEM（WorkItem 状态变化 / 分配）
  ├─ API（外部调用）
  └─ AUTOMATION（cron / 规则）

↓ Orchestrator 统一接收

CollaborationRequest
  ├─ id
  ├─ trigger_type / trigger_ref
  ├─ from: { channel_id?, actor }
  ├─ request_kind: "MESSAGE_RESPONSE" | "WORK_ITEM_EXECUTION" | "API_CALL" | "AUTOMATION_RUN"
  ├─ target_agent_id?    # 显式指定时无 Resolver
  ├─ required_capabilities: ["coding", "review"]
  ├─ context_refs: { channel_id?, message_seq?, memory_refs: [...], work_item_id? }
  ├─ status: PENDING | ACCEPTED | REJECTED | NEED_CONTEXT | UNRESOLVED | CANCELLED   # v0.3.1 收敛
  ├─ deadline_s
  └─ idempotency_key

↓ Resolver（如未指定 target_agent_id）
  ① 硬过滤：lifecycle=ACTIVE ∩ 成员资格 ∩ 有效权限 ALLOW    # v0.7 改：不再用 activity / active_slots（见 §6.2 与 §4.2.1）
  ② Capability ranking：capability_match + load + accept_rate_30d
  ③ 产出 Top-N 候选

↓ 派发到 Agent
  ① 写 decision_records
  ② WS 推 CollaborationRequest.resolved
  ③ 决策 Accept → Orchestrator 调 E7 API 创建 agent_executions  # v0.3.1 改
    | Reject / Need Context → 落 decision + 通知发起人
```

### 4.2 Decision 状态机（v0.3.1 收敛）

```
CollaborationRequest.status
  PENDING ─┬─► ACCEPTED  ─► Orchestrator 调 E7 API 创建 agent_executions
           ├─► REJECTED  （reason 必填）
           ├─► NEED_CONTEXT （needs[] 必填，阻塞等待补充）
           ├─► UNRESOLVED （全部候选超时）
           └─► CANCELLED  （发起人 / 管理员 / lifecycle 变更触发）

Execution 状态完全在 E7：
  agent_executions.status ∈ {PENDING, RUNNING, SUCCEEDED, FAILED, CANCELLED, TIMEOUT}

UI 渲染时如需「执行中」状态，由前端从 E7 API 拉取后做 projection（display_state）
CollaborationRequest 不再镜像 Execution 状态——三条 lifecycle 真正独立
```

`decision_records` 表（事实源）——所有 Decision 必带 `analysis{capability, context_score, permission}`，UI 决策卡片从 decision_records 投影生成（**不复制**）。

PENDING 超时（默认 60s）→ 取下一个候选；全部超时 → CollaborationRequest.status=UNRESOLVED + 通知发起人。

### 4.2.1 Agent Capacity 原子化（v0.3.2 收口）

**旧方案问题**：

```
agents.max_concurrency = 1
agents.active_slots = 0   (DB 字段)

T0:  Request A → Resolver 读 active_slots=0 < 1 → 选 Agent B
T1:  Request B → Resolver 读 active_slots=0 < 1 → 选 Agent B
T2:  两次 dispatch → 两次 ACCEPT → 两次 active_slots++ → 实际值=2
```

**双事实源 + 不可序列化**。同时 SELECT 看到的是过期快照。

**中间方案（v0.4.2–v0.6 Hash 版）—— 已废弃，勿实现**：

```
Redis key: agent-capacity:{agent_id}   hash { used, max, leases{lease_id→collab_id} }
tryAcquireSlot：used++、leases[lease_id]=collab_id、EXPIRE（整 key）
releaseSlot：HDEL leases[lease_id]、used--
```

废弃原因：`EXPIRE` 作用于**整个 key**，任一 lease 续期都会延长其它 lease（per-lease 隔离失效）；`used` 与 `leases` 是两个可变字段，release 路径漏一次即永久漂移；无法表达「每个 lease 各自过期」。

**Current（v0.7）单一事实源：per-lease ZSET + fencing**

```
agent-capacity:{agent_id}:leases        ZSET  member=lease_id, score=expires_at(ms)
agent-capacity:{agent_id}:config        hash  { max }      ← 来自 agents.max_concurrency
agent-capacity:{agent_id}:fence         int   递增
agent-capacity:{agent_id}:rebuilding    fence 值，SET NX EX 30
```

```
EVAL tryAcquireSlot(agent_id, lease_id, ttl_ms)
  1. if exists rebuilding            → return {0, 'REBUILDING'}
  2. ZREMRANGEBYSCORE leases -inf now  （惰性回收全部过期 lease）
  3. if ZSCORE leases lease_id        → return {1, 'EXISTS'}   （幂等）
  4. if ZCARD leases >= max           → return {0, 'FULL'}
  5. ZADD leases now+ttl_ms lease_id  → return {1, ZCARD}

EVAL promoteLease(agent_id, from_lease, to_lease, ttl_ms)   ZREM from → ZADD to
EVAL renewLease(agent_id, lease_id, ttl_ms)                 只改该 member 的 score
EVAL releaseLease(agent_id, lease_id)                       ZREM lease_id
```

**为什么是 ZSET**：`ZREMRANGEBYSCORE` 一次回收所有过期项；`ZCARD` 即当前占用（不需要 `used` 计数，消除第二事实源）；每个 lease 独立 score，续期互不影响。

**Fencing**：所有 lease 写操作带 fence token，小于当前 fence 的写入被丢弃 —— 防多实例同时 rebuild 互相覆盖。

**启动 rebuild**：`incr fence → SET rebuilding NX EX 30 → 读 DB → 写 staging ZSET → RENAME 原子发布 → 比对 fence 后清锁`；恢复窗口内 `tryAcquireSlot` 一律返回 `REBUILDING`（实现细节见 `detailed/04` §3.6）。

**单一事实源**：
- `max_concurrency`：DB 字段（durable config）
- 占用集合与到期时间：Redis ZSET（Runtime scheduling state，可随时由 DB 重建）
- DB 与 Redis **没有**双写

**Slot 提前 reservation**：

```
Resolver 选 Agent → tryAcquireSlot(pending_decision) → 成功才推 collaboration.request
  ↓
Agent ACCEPT
  ↓
E4 事务外直调 E7 创建 execution（lease 由 E4 promote 为 execution lease，仍引用 collaboration_requests.slot_lease_id）
  ↓
E7 terminal → outbox → E4 releaseLease
  ↓
或：Agent REJECT / NEED_CONTEXT / timeout / 管理员 cancel → E4 releaseLease
```

**capacity invariant（v0.7 冻结）**：容量在 **Resolver 阶段**已由 lease 预占（`tryAcquireSlot → ACCEPT → promoteLease`），因此**执行阶段不应再出现 `CAPACITY_FULL`**。
若 `execution.dispatch` 时 Agent 报满/掉线，按 **transport 问题**处理（重试同一 `(execution_id, attempt_no)` → 超过阈值 FAILED + 进 Inbox），**不回到 Resolver 重路由** —— CR 在 ACCEPT 后即终态，且 `UNIQUE(collaboration_request_id)` 保证一个 CR 只有一个 Execution。详见 `detailed/03` §7.2 与 `detailed/10` §2 B2。

### 4.3 Memory 写入门禁

```
Agent proposal（type / content / source 引用）
  ↓ permission check：propose_memory（v0.7 修正：8 键拆分后应为 propose_memory，默认 ALLOW）
  ↓ 写 memory_proposals（status=PROPOSED）
  ↓ WS 推送给 project 内有 approve_memory 权限的人类
  ↓ Approve → permission check：approve_memory → 写 memory_items（status=APPROVED）→ 入库 + 索引
  ↓ Reject → memory_proposals.status=REJECTED
```

> **v0.7 修正**：此前此处写 `write_memory → REQUIRE_APPROVAL`，与 8 键设计（`propose_memory` 管申请、`write_memory` 仅 service-to-service）矛盾，会导致 `propose_memory` 存在却不控制申请入口。`write_memory` **不**出现在 Agent 申请路径上，仅由 Memory Service 在审批通过后内部调用。

Source 三件套（`source_type` / `source_channel_id` / `source_message_seq`）强约束。

### 4.4 Work Management 抽象

```
WorkManagementProvider interface          # 正式定义见 §9.1（v0.7 摘要同步）
  ├─ getCapabilities() → ProviderCapabilities
  ├─ getSelfMetadata() → ProviderMetadata            # 只返回 capability，无静态 status 模板
  ├─ listStatuses(binding) → ProviderStatus[]        # v0.3.1：按 binding 动态拉
  ├─ listWorkItems(query, binding) → WorkItemPage
  ├─ getWorkItem(ref, binding) → WorkItem
  ├─ createWorkItem(input, binding) → WorkItem
  ├─ updateWorkItem(ref, changes, binding) → WorkItem
  ├─ addComment(ref, comment, binding) → WorkComment
  └─ getStatusMapping(binding) → CanonicalStatusMapping[]

BuiltInProvider（MVP）
  └─ work_items 直接读写 PG（单表即 canonical 表示）

JiraProvider（V1+ E9）
  └─ Jira REST + Webhook → 同步进 work_items（v0.4.5：无 projections 表）
```

业务层永远只依赖 `WorkManagementProvider` 接口；`if (provider === 'jira') ...` 永不允许出现。

---

## 5. 数据模型（PostgreSQL）

### 5.1 实体表清单

| 表 | 说明 | 引入版本 |
| --- | --- | --- |
| users | 用户 | E1 |
| organizations | 组织 | E1 |
| organization_members | 组织成员 | **E1 v0.4 新增** |
| teams | 团队 | E1 |
| team_members | 团队成员 | E1 |
| projects | 项目（**无** integration_backend / issue_tracker） | E1 |
| project_members | 项目成员 | E1 |
| credentials | 凭据池（加密） | E2 |
| agents | Agent（**无** can_execute/can_review；lifecycle+activity） | E2 |
| agent_project_membership | Agent 在项目内的可见范围 | E2 |
| agent_tokens | Runtime token | E2 |
| channels | 频道 | E3 |
| channel_members | 频道成员 | E3 |
| channel_seq_counters | seq 分配 | E3 |
| messages | 消息（projection，5 形态引 entity_ref） | E3 |
| attachments | 附件 | E3 |
| read_cursors | 已读位点 | E3 |
| triggers | 触发器原始记录 | E4 |
| collaboration_requests | 协作请求一等实体 | **E4 v0.4 新增** |
| decision_records | 决策事实源 | E4 |
| memory_proposals | Memory 申请（审批前） | **E5 v0.4 新增** |
| memory_items | Memory 库 | E5 |
| memory_chunks | Memory 索引 | E5 |
| memory_review_actions | 审批审计 | E5 |
| permissions | 权限矩阵 | E6 |
| agent_executions | Agent 执行记录 | **E7 v0.4 新增** |
| execution_attempts | 重试/重连的 attempt | E7 |
| execution_events | 流式事件 | E7 |
| execution_artifacts | 产物（diff / file / log） | E7 |
| work_items | Built-in WorkItem；Provider=Jira 时亦为本地同步表示（单表） | **E8 v0.4 新增** |
| work_item_bindings | Project × WorkItemProvider 关联 | E8 |
| work_comments | WorkItem 评论 | E8 |
| work_relations | WorkItem 关系（blocks / relates_to） | E8 |
| work_management_connections | Provider 连接信息（Org 级 OAuth token + client secret） | E8 |
| work_management_webhooks | Jira 动态订阅注册（v0.4.3 独立表） | E8/E9 |
| webhook_inbox | Provider webhook 收件箱（去重 + 持久化） | **E9 v0.5 新增** |
| work_sync_audit | Provider 同步审计 | E8 |
| **outbox_events** | **异步/跨模块副作用的唯一出口（relay 消费）** | **E7 v0.4 · v0.5 收口** |
| audit_logs | 全量审计 | E10 |
| notifications | 通知 | E10 |
| llm_calls | LLM 调用统计 | E7 |

### 5.2 关键表结构

```sql
-- projects（v0.4 简化：移除 integration_backend / issue_tracker）
CREATE TABLE projects (
  id          UUID PRIMARY KEY,
  team_id     UUID NOT NULL REFERENCES teams(id),
  name        TEXT NOT NULL,
  description TEXT,
  repo_url    TEXT,
  created_at  TIMESTAMPTZ DEFAULT now()
);

-- agents（v0.4 简化）
CREATE TABLE agents (
  id              UUID PRIMARY KEY,
  owner_user_id   UUID NOT NULL REFERENCES users(id),
  credential_id   UUID NOT NULL REFERENCES credentials(id),
  name            TEXT NOT NULL,
  role            TEXT NOT NULL,
  capabilities    JSONB NOT NULL DEFAULT '[]',     -- canonical key
  -- 无 can_execute / can_review
  lifecycle       TEXT NOT NULL DEFAULT 'ACTIVE'
                  CHECK (lifecycle IN ('ACTIVE','PAUSED','DISABLED')),
  activity        TEXT NOT NULL DEFAULT 'OFFLINE'
                  CHECK (activity IN ('OFFLINE','AVAILABLE','THINKING','WORKING','WAITING_CONTEXT','ERROR')),
  activity_reason TEXT,
  -- v0.5 补（原 DDL 缺失，但 detailed/04 §3.6、08 §4.2 的 SQL 依赖这两列）
  max_concurrency INT NOT NULL DEFAULT 1,               -- durable config；active_slots 归 Redis
  health          TEXT NOT NULL DEFAULT 'HEALTHY'
                  CHECK (health IN ('HEALTHY','DEGRADED','UNHEALTHY')),
  daily_limit_usd NUMERIC(10,2) DEFAULT 5.00,
  monthly_budget_usd NUMERIC(10,2) DEFAULT 50.00,
  created_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_agents_lifecycle_activity ON agents(lifecycle, activity);
CREATE INDEX idx_agents_health ON agents(health) WHERE health <> 'HEALTHY';

-- collaboration_requests（v0.4 新增一等实体）
CREATE TABLE collaboration_requests (
  id                    UUID PRIMARY KEY,
  trigger_type          TEXT NOT NULL CHECK (trigger_type IN ('MENTION','WORK_ITEM','API','AUTOMATION')),
  trigger_ref           JSONB,                          -- {channel_id, message_seq, work_item_id, ...}
  request_kind          TEXT NOT NULL,                  -- 'MESSAGE_RESPONSE' | 'WORK_ITEM_EXECUTION' | 'API_CALL' | 'AUTOMATION_RUN'
  from_actor_type       TEXT NOT NULL,
  from_actor_id         UUID NOT NULL,
  target_agent_id       UUID,                            -- 显式指定时填
  required_capabilities JSONB NOT NULL DEFAULT '[]',
  context_refs          JSONB NOT NULL DEFAULT '{}',    -- {channel_id, message_seq, memory_refs, work_item_id}
  status                TEXT NOT NULL DEFAULT 'PENDING'
                        -- v0.4.5：收敛 6 态（AD-I4：CR 不镜像 Execution 状态，EXECUTING/COMPLETED/FAILED 一律禁用）
                        CHECK (status IN ('PENDING','ACCEPTED','REJECTED','NEED_CONTEXT','UNRESOLVED','CANCELLED')),
  target_execution_id   UUID,                            -- 接受后回填
  deadline_s            INT NOT NULL DEFAULT 600,
  idempotency_key       TEXT UNIQUE,
  created_at            TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_collab_status_time ON collaboration_requests(status, created_at DESC);
CREATE INDEX idx_collab_target_agent ON collaboration_requests(target_agent_id, created_at DESC) WHERE target_agent_id IS NOT NULL;

-- agent_executions（v0.4 新增）
CREATE TABLE agent_executions (
  id                  UUID PRIMARY KEY,
  collaboration_request_id UUID REFERENCES collaboration_requests(id),
  work_item_ref       JSONB,                            -- {provider_key, work_item_id, external_ref} optional
  agent_id            UUID NOT NULL REFERENCES agents(id),
  status              TEXT NOT NULL DEFAULT 'PENDING'
                      CHECK (status IN ('PENDING','RUNNING','SUCCEEDED','FAILED','CANCELLED','TIMEOUT')),
  input               JSONB NOT NULL,                   -- request input snapshot
  context_refs        JSONB NOT NULL DEFAULT '{}',
  attempt_count       INT NOT NULL DEFAULT 0,
  -- v0.5 补（原 DDL 缺失，但 detailed/01 §4.4、04 §3.6、08 §3 的 CAS 与 rebuild 全部依赖）
  active_attempt_no   INT,                              -- 当前 attempt；终态置 NULL
  terminal_envelope_id UUID,                            -- 终态 envelope 去重（变更 #24）
  started_at          TIMESTAMPTZ,
  completed_at        TIMESTAMPTZ,
  failure_code        TEXT,
  failure_message     TEXT,
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_executions_agent ON agent_executions(agent_id, created_at DESC);
CREATE INDEX idx_executions_status ON agent_executions(status, created_at DESC);
-- 在途 Execution 恢复用（04 §3.6 启动 rebuild）
CREATE INDEX idx_executions_active_attempt ON agent_executions(agent_id, status)
  WHERE status IN ('PENDING','RUNNING') AND active_attempt_no IS NOT NULL;
-- E4 → E7 创建幂等（detailed/01 §5）
CREATE UNIQUE INDEX uq_executions_collab ON agent_executions(collaboration_request_id)
  WHERE collaboration_request_id IS NOT NULL;

-- execution_attempts（v0.5：枚举收敛 + 补 dispatch/续传列）
CREATE TABLE execution_attempts (
  id              UUID PRIMARY KEY,
  execution_id    UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_no      INT NOT NULL,
  runtime_session_id TEXT,
  -- v0.5：三处口径（STARTED / RUNNING / INTERRUPTED）合并为唯一枚举
  status          TEXT NOT NULL CHECK (status IN ('STARTED','RUNNING','COMPLETED','FAILED','INTERRUPTED')),
  dispatch_sent_at    TIMESTAMPTZ,       -- Runtime 推 dispatch 的时刻
  dispatch_acked_at   TIMESTAMPTZ,       -- 收到 execution.dispatch_ack 的时刻（03 §7）
  last_persisted_seq  BIGINT,            -- resume 连续位点（03 §3.2）
  started_at      TIMESTAMPTZ,
  completed_at    TIMESTAMPTZ,
  error           TEXT,
  UNIQUE (execution_id, attempt_no)
);

-- execution_events（v0.4.5：变更 #14 协议级幂等落地）
CREATE TABLE execution_events (
  id                BIGSERIAL PRIMARY KEY,
  execution_id      UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_id        UUID NOT NULL REFERENCES execution_attempts(id),  -- v0.4.5：NOT NULL，否则幂等约束形同虚设
  provider_event_id TEXT NOT NULL,                     -- 协议级幂等键（envelope 必带）
  event_type        TEXT NOT NULL,                     -- 'STDOUT' | 'PROGRESS' | 'TOOL_CALL' | 'LLM_TICK' | 'ARTIFACT' | 'ERROR'
  payload           JSONB NOT NULL,
  trace_id          TEXT,
  created_at        TIMESTAMPTZ DEFAULT now(),
  UNIQUE (attempt_id, provider_event_id)               -- 重复投递直接冲突，写入端 ON CONFLICT DO NOTHING
);
CREATE INDEX idx_events_execution_time ON execution_events(execution_id, created_at);
CREATE INDEX idx_events_attempt ON execution_events(attempt_id);

-- execution_artifacts
CREATE TABLE execution_artifacts (
  id            UUID PRIMARY KEY,
  execution_id  UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  kind          TEXT NOT NULL,                         -- 'FILE' | 'DIFF' | 'LOG' | 'SCREENSHOT'
  name          TEXT NOT NULL,
  s3_key        TEXT NOT NULL,
  size          BIGINT,
  mime          TEXT,
  created_at    TIMESTAMPTZ DEFAULT now()
);

-- work_items（单表：Built-in 时即事实源；Jira 时是本地同步表示，v0.5 取代 work_item_projections）
CREATE TABLE work_items (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id),
  binding_id          UUID NOT NULL REFERENCES work_item_bindings(id),   -- v0.4.3 必填（§9.3 路由按它走）
  type                TEXT NOT NULL CHECK (type IN ('TASK','STORY','BUG','EPIC')),
  title               TEXT NOT NULL,
  description         TEXT,
  status              TEXT NOT NULL DEFAULT 'OPEN'
                      CHECK (status IN ('OPEN','IN_PROGRESS','IN_REVIEW','DONE','CLOSED')),
  canonical_status_category TEXT NOT NULL DEFAULT 'TODO'
                      CHECK (canonical_status_category IN ('TODO','IN_PROGRESS','DONE')),
  assignee_type       TEXT CHECK (assignee_type IN ('HUMAN','AGENT')),
  assignee_id         UUID,
  due_at              TIMESTAMPTZ,
  -- Provider 维度（v0.5 补：§9.2 声明"自带这些列"，原 DDL 缺失）
  provider_key        TEXT NOT NULL,                     -- 'builtin' | 'jira'
  external_ref        TEXT,                              -- Built-in 时 NULL；Jira 时 'PROJ-123'
  external_url        TEXT,
  provider_status     TEXT,                              -- 原始 status 字面值
  provider_meta       JSONB,
  raw_payload         JSONB,                             -- 最近一次 Provider DTO（审计/回放）
  search_text         TEXT NOT NULL DEFAULT '',          -- 检索用（tsv 索引）
  last_sync_at        TIMESTAMPTZ,
  last_sync_status    TEXT CHECK (last_sync_status IN ('OK','FAILED','CONFLICT')),
  created_by_type     TEXT NOT NULL,
  created_by_id       UUID NOT NULL,
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now()
);
-- 外部引用唯一（Built-in 行 external_ref 为 NULL，不受约束）
CREATE UNIQUE INDEX uq_work_items_external ON work_items(provider_key, external_ref)
  WHERE external_ref IS NOT NULL;
CREATE INDEX idx_work_items_project_updated ON work_items(project_id, updated_at DESC);
CREATE INDEX idx_work_items_search ON work_items USING GIN (to_tsvector('simple', search_text));

-- v0.4.5：work_item_projections 已删除（变更 #15 / v0.3.1 起）
-- Jira 等 Provider 的本地同步表示直接落在 work_items 单表，相关列：
--   provider_key / external_ref / external_url / provider_status / provider_meta
--   / search_text / raw_payload / last_sync_at / last_sync_status
-- 索引：UNIQUE (provider_key, external_ref) 建在 work_items 上

-- work_item_bindings（Project × Provider 关联；v0.4 替代 projects.issue_tracker）
CREATE TABLE work_item_bindings (
  id              UUID PRIMARY KEY,
  project_id      UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  provider_key    TEXT NOT NULL,                        -- 'builtin' | 'jira'
  connection_id   UUID,                                 -- Provider 连接（V1+ 启用）
  external_project_ref TEXT,                            -- Jira project key（如 'PROJ'）
  settings        JSONB,                                -- status mapping 等
  is_active       BOOLEAN NOT NULL DEFAULT true,
  created_at      TIMESTAMPTZ DEFAULT now(),
  updated_at      TIMESTAMPTZ DEFAULT now(),
  UNIQUE (project_id, provider_key)
);

-- work_management_connections（Provider OAuth / API key）
-- v0.4.5（变更 #28）：Org 级共享，一个 Org 可有多条 Connection，不再绑 Project
CREATE TABLE work_management_connections (
  id              UUID PRIMARY KEY,
  provider_key    TEXT NOT NULL,                        -- 'jira'
  org_id          UUID NOT NULL REFERENCES organizations(id),
  owner_user_id   UUID NOT NULL REFERENCES users(id),
  access_token_encrypted  BYTEA NOT NULL,
  refresh_token_encrypted BYTEA,
  expires_at      TIMESTAMPTZ,
  meta            JSONB,
  -- v0.5：app 级 client secret（Jira OAuth 2.0 动态 webhook 的 Bearer JWT 用它验签）
  -- 注意：它属于「app/connection」维度，**不**挂在 webhook 注册行上
  client_secret_encrypted BYTEA,
  created_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_wmc_org_provider ON work_management_connections(org_id, provider_key);

-- work_management_webhooks（v0.4.3 从 Connection 拆出为独立表）
CREATE TABLE work_management_webhooks (
  id                  UUID PRIMARY KEY,
  binding_id          UUID NOT NULL REFERENCES work_item_bindings(id) ON DELETE CASCADE,
  connection_id       UUID NOT NULL REFERENCES work_management_connections(id),
  external_webhook_id TEXT NOT NULL,       -- Jira 返回的动态订阅 ID（匹配 payload.matchedWebhookIds）
  filter_jql          TEXT,
  filter_events       JSONB,
  expires_at          TIMESTAMPTZ NOT NULL,
  last_refreshed_at   TIMESTAMPTZ,
  refresh_status      TEXT CHECK (refresh_status IN ('OK','FAILED','DISABLED')),
  last_error          TEXT,
  created_at          TIMESTAMPTZ DEFAULT now(),
  UNIQUE (external_webhook_id)
);

-- webhook_inbox（v0.5：收件箱，去重与持久化同一事务）
CREATE TABLE webhook_inbox (
  id            BIGSERIAL PRIMARY KEY,
  provider_key  TEXT NOT NULL,
  delivery_id   TEXT NOT NULL,             -- X-Atlassian-Webhook-Identifier（仅标识单次投递）
  webhook_id    UUID NOT NULL REFERENCES work_management_webhooks(id),
  binding_id    UUID NOT NULL REFERENCES work_item_bindings(id),
  payload       JSONB NOT NULL,
  status        TEXT NOT NULL DEFAULT 'PENDING'
                CHECK (status IN ('PENDING','PROCESSED','DEAD')),
  attempt_count INT NOT NULL DEFAULT 0,
  last_error    TEXT,
  received_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  processed_at  TIMESTAMPTZ,
  UNIQUE (provider_key, delivery_id)
);
CREATE INDEX idx_webhook_inbox_pending ON webhook_inbox(status, received_at) WHERE status = 'PENDING';

-- outbox_events（v0.5：全部异步与跨模块副作用的唯一出口；原 BullMQ 的替代）
CREATE TABLE outbox_events (
  id              UUID PRIMARY KEY,
  aggregate_type  TEXT NOT NULL,          -- 'collaboration' | 'execution' | 'work_item' | ...
  aggregate_id    UUID NOT NULL,
  event_type      TEXT NOT NULL,          -- 'execution.completed' | 'execution.retry' | 'collab.timeout' | ...
  payload         JSONB NOT NULL,
  idempotency_key TEXT UNIQUE,            -- 消费者侧去重
  status          TEXT NOT NULL DEFAULT 'PENDING'
                  CHECK (status IN ('PENDING','PUBLISHED','DEAD')),
  attempt_count   INT NOT NULL DEFAULT 0,
  next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT now(),   -- 延迟重试/超时的承载（relay 到期领取）
  last_error      TEXT,
  published_at    TIMESTAMPTZ,
  created_at      TIMESTAMPTZ DEFAULT now()
);
-- relay 领取：SELECT ... WHERE status='PENDING' AND next_attempt_at <= now()
--             ORDER BY next_attempt_at FOR UPDATE SKIP LOCKED
CREATE INDEX idx_outbox_ready ON outbox_events(next_attempt_at) WHERE status = 'PENDING';
CREATE INDEX idx_outbox_aggregate ON outbox_events(aggregate_type, aggregate_id);
CREATE INDEX idx_outbox_dead ON outbox_events(created_at DESC) WHERE status = 'DEAD';

-- permissions（v0.4 effect 改三态）
CREATE TABLE permissions (
  id           UUID PRIMARY KEY,
  scope_type   TEXT NOT NULL CHECK (scope_type IN ('PROJECT','CHANNEL')),
  scope_id     UUID NOT NULL,
  subject_type TEXT NOT NULL CHECK (subject_type IN ('USER','AGENT')),
  subject_id   UUID NOT NULL,
  -- v0.4.5：8 键（detailed/07 v0.4.3 拆分出 propose_memory）
  perm_key     TEXT NOT NULL CHECK (perm_key IN (
                'read_message','write_message','propose_memory','write_memory',
                'execute_code','create_pr','approve_memory','manage_channel')),
  effect       TEXT NOT NULL CHECK (effect IN ('ALLOW','DENY','REQUIRE_APPROVAL')),
  created_at   TIMESTAMPTZ DEFAULT now(),
  UNIQUE (scope_type, scope_id, subject_type, subject_id, perm_key)
);
```

### 5.3 关键设计决策

- **Project = 协作/知识/工作边界**，不挂 Provider 字段；Provider 通过 `work_item_bindings` 关联
- **WorkItem 与 Execution 完全独立**——`work_item_ref` 在 `agent_executions` 中是 optional JSONB
- **Trigger 多种来源**统一汇聚到 `collaboration_requests`，Mention 不再直接路由到 Agent
- **消息流是 projection**——`messages.content_type` 5 形态保留，DECISION 形态只引 `decision_ref`（不复制 decision_records 字段）
- **Memory Source 强约束**——`source_type` + `source_channel_id` + `source_message_seq` 三件套，CHANNEL_MESSAGE 时必填
- **Permission effect 三态**——ALLOW / DENY / REQUIRE_APPROVAL；后两者语义清晰，不与 CollaborationRequest 概念冲突
- **Agent lifecycle 与 activity 独立**——lifecycle 控制"能不能上线"，activity 控制"在线时在做什么"
- **Capability 与 Permission 单一事实源**——Agent 表无 `can_execute` / `can_review`；权限统一由 `permissions` 表覆盖

---

## 6. Agent Runtime & Execution Domain

### 6.1 出站连接

```
Agent 进程（Coding-Agent、Review-Agent...）
   │  WSS 出站 + JWT(agent_token)
   ▼
Agent Runtime Gateway（services/runtime）
  ├─ 注册/心跳：上报 lifecycle + activity
  ├─ 任务下发：dispatch (携带 collaboration_request_ref)
  └─ 流式回传：progress / result / error → execution_events
```

### 6.2 Lifecycle × Activity 状态机

| 维度 | 进入 | 退出 | UI |
| --- | --- | --- | --- |
| lifecycle=ACTIVE | owner 启用 | owner 暂停 / 系统禁用 | 正常状态点 |
| lifecycle=PAUSED | owner 暂停 | owner 启用 | 灰点 + 「已暂停」徽标 |
| lifecycle=DISABLED | 系统禁用 | 申诉后恢复 | 红点 + 「已禁用」徽标 |
| activity=OFFLINE | 心跳 > 90s 丢失 | 心跳恢复 | 灰点 |
| activity=AVAILABLE | 心跳 + idle | 收到 CollaborationRequest | 绿点 |
| activity=THINKING | 已接收 collaboration_request | 产出 decision | 紫点 + 呼吸 |
| activity=WORKING | decision=ACCEPT → 创建 execution | execution 完成 | 蓝点 + 光标 |
| activity=WAITING_CONTEXT | decision=NEED_CONTEXT | 人类补齐 | 琥珀点 + 计数 |
| activity=ERROR | Provider 失败 / 限额 | owner 处理 | 红点 + fix_hint |

**Resolver 过滤（v0.4.5 修正 · 变更 #25 / DM-I8）**：`lifecycle=ACTIVE ∩ Redis tryAcquireSlot 成功 ∩ 有效权限 ALLOW`。**activity 不参与调度过滤**，只做 UI 派生；不再按 `{AVAILABLE, THINKING}` 排除候选。

### 6.3 Execution Domain（v0.4 新增）

```
CollaborationRequest (ACCEPTED)
   │
   ▼
agent_executions
   ├─ status: PENDING → RUNNING → SUCCEEDED / FAILED / CANCELLED / TIMEOUT
   ├─ input: request input snapshot
   ├─ context_refs: {channel_id, message_seq, memory_refs, work_item_ref?}
   ├─ attempt_count: 0
   │
   └─ execution_attempts (1..N)
        ├─ runtime_session_id
        ├─ status: RUNNING → COMPLETED / FAILED
        └─ execution_events
              ├─ event_type: STDOUT / PROGRESS / TOOL_CALL / LLM_TICK / ARTIFACT / ERROR
              └─ payload: { content, meta, trace_id }
   └─ execution_artifacts
         └─ s3_key + metadata

retry / reconnect / crash recovery / timeout / cancel / streaming / artifact 全部基于 Execution 模型
```

**关键原则**：outbox relay（原 BullMQ 语义）绝不能成为 Agent Execution 的事实源。relay 只做 transport / scheduler（把任务推到 Runtime），`agent_executions` + `execution_attempts` + `execution_events` 才是事实源。延迟重试走 `outbox_events.next_attempt_at`，不依赖 MQ 的 delayed job。

### 6.4 ERROR 触发（lifecycle 维度）

| 触发 | 行为 |
| --- | --- |
| Provider 5xx 连续 3 次 | activity=ERROR |
| Provider 401/403 | activity=ERROR + reason=auth_failed |
| 日/周限额触顶 | activity=ERROR + reason=rate_limit |
| Sandbox 启动失败（V3） | activity=ERROR |

**v0.4.5 修正**：`activity=ERROR` **不影响** Resolver 候选——能否接新活只看 `lifecycle=ACTIVE` + 有空闲 slot + 有效权限 ALLOW；activity 仅影响 UI 呈现。真正把 Agent 挡在调度外的是 `agents.health='UNHEALTHY'`（detailed/08 §4.2）与 slot 耗尽。

### 6.5 Connector 协议（v1.1 — v0.3.1 协议边界拆开）

```jsonc
// 通用 envelope
{ "v": 1, "type": "hello|heartbeat|status|dispatch|event|result|error|execution.resume_request|execution.resume_ack", "id": "uuid", "ts": 0, "payload": {} }

// E7 拥有
{ "type": "dispatch", "payload": {
    "execution_id": "...",
    "collaboration_request_id": "..."?,     // optional，可无 WorkItem 触发
    "work_item_ref": {"provider_key":"builtin", "work_item_id":"..."}?,   // optional
    "input": { "prompt": "...", "params": {} },
    "context": { "memory_refs": [], "recent_messages": [], "permissions": {} },
    "deadline_s": 600, "idempotency_key": "..."
}}

{ "type": "event", "payload": {
    "execution_id": "...", "attempt_no": 1,
    "event_type": "STDOUT|PROGRESS|TOOL_CALL|LLM_TICK|ARTIFACT|ERROR",
    "provider_event_id": "evt-uuid-123",    // 协议级幂等键（v0.3.1 新增）
    "seq": 42,                               // 单 attempt 内单调
    "payload": { "content": "...", "meta": {} }
}}

{ "type": "result", "payload": {
    "execution_id": "...", "attempt_no": 1,
    "status": "SUCCEEDED|FAILED|CANCELLED",   // 不携带 decision
    "output": { "markdown": "...", "code": "..." },
    "usage": { "tokens_in": 0, "tokens_out": 0, "duration_ms": 0 },
    "artifacts": [ { "kind": "FILE", "name": "...", "s3_key": "..." } ]
}}

// v0.4.5（变更 #23）：resume 反向 —— Agent 问、Runtime 答
{ "type": "execution.resume_request", "payload": {
    "execution_id": "...", "attempt_no": 1
}}
{ "type": "execution.resume_ack", "payload": {
    "execution_id": "...", "attempt_no": 1,
    "last_persisted_seq": 41,          // 连续位点（非 MAX(seq)）
    "snapshot": { "input": {...}, "context": {...}, "deadline_s": 600 }
}}

// E4 拥有（独立消息名空间，不重叠）
{ "type": "collaboration.decision", "payload": {
    "collaboration_request_id": "...",
    "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason": "...", "needs": [...],
    "analysis": { "capability": true, "context_score": 88, "permission": true }
}}
```

设计原则：
- Agent 无入站端口（NAT 穿透）；上下文注入走引用；Project 知识边界由服务端强制
- **v0.3.1 协议边界**：`collaboration.*`（E4）vs `execution.*`（E7）严格分离
- **v0.3.1 event 幂等**：`provider_event_id` + UNIQUE(attempt_id, provider_event_id) DB 保证
- **attempt ≠ WS session**：reconnect 走 `execution.resume_request` / `execution.resume_ack`；reconnect 不新建 attempt，Agent 从 `last_persisted_seq + 1` 续发（Runtime 不补发 event——event 是 Agent 产的）

设计原则：Agent 无入站端口（NAT 穿透）；上下文注入走引用；Project 知识边界由服务端强制。

---

## 7. Permission Model 实现

- **8 个权限键**（v0.4.5：detailed/07 v0.4.3 拆分出 `propose_memory`；E6 §2.1 仍写 7 键，**待同步**）
- 3 态：`check(subject, perm, scope) → ALLOW | DENY | REQUIRE_APPROVAL`
- 三层覆盖：默认矩阵 → Project 覆盖 → Channel 覆盖
- **没有覆盖行 ≠ 拒绝**：未命中覆盖时回落到默认矩阵（detailed/04 §2.1 已按此修正候选筛选）
- Redis 缓存 + `perm.changed` pub/sub 失效

### 7.1 默认矩阵

| 权限 | Human owner | Human member | Agent (lifecycle=ACTIVE) |
| --- | --- | --- | --- |
| read_message | ALLOW | ALLOW | ALLOW |
| write_message | ALLOW | ALLOW | ALLOW |
| **propose_memory**（v0.4.5 新增） | **ALLOW** | **ALLOW** | **ALLOW** |
| write_memory | REQUIRE_APPROVAL | REQUIRE_APPROVAL | REQUIRE_APPROVAL |
| execute_code | DENY | DENY | DENY |
| create_pr | REQUIRE_APPROVAL | DENY | DENY |
| approve_memory | ALLOW | DENY | DENY |
| manage_channel | ALLOW | DENY | DENY |

---

## 8. 协议汇总

| 边界 | 协议 |
| --- | --- |
| Client ↔ API | REST `/api/v1` + JWT |
| Client ↔ Gateway | WSS + JSON envelope |
| Client ↔ Gateway · 消息级续传 | `resume(last_seq)`（E3 §5.3，消息 seq 游标）—— **与 execution 级 resume 不同名空间** |
| Agent ↔ Runtime · 执行级续传 | `execution.resume_request` / `execution.resume_ack`（E7，attempt 内连续位点） |
| 内部异步 | PostgreSQL `outbox_events` + `SKIP LOCKED` relay（BackgroundService），**无独立 MQ** |
| 内部广播 | Redis Pub/Sub |
| Agent ↔ Runtime | WSS Connector 协议 v1（含 execution_id / collaboration_request_id） |
| Work Provider ↔ Built-in | 直读 PG |
| Work Provider ↔ Jira | REST + Webhook（V1+ E9） |
| LLM 调用 | OpenAI-compatible HTTP |
| 对象存储 | S3 API |

---

## 9. Work Management 域

### 9.1 Provider 抽象（v0.3.1 改：去硬编码）

```ts
// v0.3.1 改：ProviderKey = string（去硬编码）
type ProviderKey = string;

interface WorkManagementProvider {
  readonly key: ProviderKey;
  getCapabilities(): ProviderCapabilities;
  // v0.4.5（变更 #29）：方法保留，但**只返回 capability 描述**，
  // 静态 status 模板已删除；status mapping 一律走 listStatuses(binding) 动态拉取
  getSelfMetadata(): Promise<ProviderMetadata>;
  // v0.3.1 改：listStatuses 接受 binding（不是全局模板）
  listStatuses(binding: WorkItemBinding): Promise<ProviderStatus[]>;
  listWorkItems(query: WorkItemQuery, binding: WorkItemBinding): Promise<WorkItemPage>;
  getWorkItem(ref: WorkItemRef, binding: WorkItemBinding): Promise<WorkItem>;
  createWorkItem(input: WorkItemInput, binding: WorkItemBinding): Promise<WorkItem>;
  updateWorkItem(ref: WorkItemRef, changes: WorkItemChanges, binding: WorkItemBinding): Promise<WorkItem>;
  addComment(ref: WorkItemRef, comment: WorkCommentInput, binding: WorkItemBinding): Promise<WorkComment>;
  listComments(ref: WorkItemRef, binding: WorkItemBinding): Promise<WorkComment[]>;
  getStatusMapping(binding: WorkItemBinding): Promise<CanonicalStatusMapping[]>;
}

interface ProviderRegistry {
  register(provider: WorkManagementProvider): void;
  get(key: ProviderKey): WorkManagementProvider | undefined;
  list(): ProviderMetadata[];
}
```

**业务层**：`workManagementProviderRegistry.get(...)` 拿到实例后调用，**永不分 provider 类型**。加 YouTrack / Azure Boards / Asana 时只在 bootstrap 注册新 Provider，**Work Management Core 零修改**。

### 9.2 Built-in Provider

- Source of truth = `work_items` 单表（v0.3.1 删 work_item_projections）
- work_items 自带 `provider_key` / `external_ref` / `external_url` / `provider_status` / `provider_meta` / `search_text`
- Built-in 时 `provider_key='builtin'` / `external_ref=NULL`
- 默认实现，V1 内置
- Project 默认 binding=builtin，无需配置

### 9.3 路由规则（v0.3.1 冻结）

```
CREATE WorkItem:
  provider = providerRegistry.get(project.activeBinding.provider_key)
  → 必须有 active binding；否则 422 "no active Work Management Provider"

UPDATE WorkItem (status / title / assignee / etc.):
  provider = providerRegistry.get(work_item.provider_key)
  → 永远用 work_item.provider_key，**不**看 active binding
  → 切到 Jira 后旧 Built-in WorkItem 仍走 BuiltInProvider
```

### 9.4 DB Invariant（v0.3.1）

```sql
-- Project 同 active provider 唯一
CREATE UNIQUE INDEX uq_project_active_work_provider
  ON work_item_bindings(project_id)
  WHERE is_active = true;
```

> 一个 Project 同一时刻只能有一个 active Work Management Provider。由 DB 强制，不靠 service code 守门。

### 9.5 Jira Provider（V1+ E9）

- Source of truth = Jira Cloud
- MateOS 维护 `work_items`（v0.3.1 单表）本地同步表示
- 通过 webhook 接收 Jira 状态变化
- 双绑去重：payload_hash 防重复同步
- 冲突：CONFLICT + 通知 owner 仲裁

### 9.6 Change Provider 语义

**Change Provider**（仅影响新 WorkItem） + **Migrate Existing WorkItems**（独立 Wizard，V2）：
- 切换 provider_key 立即生效
- 历史 WorkItem 迁移走向导，可选导出 CSV / 单向同步到新 Provider
- V1 简化为仅 Change Provider；Migrate 留 V2

### 9.7 Jira Status Mapping（v0.3.1 改：动态拉）

```
初始化 binding 时：
  1. OAuth + GET /rest/api/3/project/{key}/statuses  // 实际 status 按 Issue Type 变
  2. 写入 binding.settings.status_options
  3. P11 UI 渲染动态选择
  4. 用户选好映射后写 binding.settings.status_mapping
```

> **不要在 JiraProvider 类里写死 TODO → ['To Do', 'Open'] 静态模板**。Atlassian 官方说状态按项目 + Issue Type 变。

---

## 10. 缓存设计（Redis）

| 用途 | Key | TTL |
| --- | --- | --- |
| Agent presence | `presence:{agent_id}` | 90s |
| Slot lease（v0.7 ZSET） | `agent-capacity:{agent_id}:leases`（member=lease_id, score=expires_at） | 每条 lease 独立（90s / 300s） |
| Slot 配置 / 重建闸 / fence | `agent-capacity:{agent_id}:config` / `:rebuilding` / `:fence` | config 24h；rebuilding 30s |
| Channel 近期消息 | `timeline:{channel_id}` | 10min |
| 未读计数 | `unread:{member_id}:{channel_id}` | 24h |
| 权限矩阵 | `perm:{scope}:{subject}` | 5min |
| Mention 排序特征 | `mfeat:{project}:{agent_id}` | 1h |
| Mention 解析结果 | `mention:{id}` | 10min |
| WorkItem 缓存 | `workitem:{provider}:{ref}` | 5min |
| Provider token | `wmc:{provider}:{org_id}:{owner_user_id}`（v0.4.2 起 Org 级） | refresh |

> **纪律**：Redis 全部是**可重建缓存 / scheduling state**，不是事实源（G-1）。任何 Redis 数据丢失都必须能由 PostgreSQL 重建（slot 见 §4.2.1 的 rebuild 流程）。

---

## 11. UI Projection & Human Attention Model（v0.8 新增）

### 11.1 原则

> **Domain Entity ≠ UI Navigation Entity。**
> 领域模型不参与导航设计；用户界面只暴露"发生了什么 / 需要我做什么 / 接下来会自动发生什么"。

**Progressive Disclosure 三级**：

| 级别 | 内容 | 默认 |
| --- | --- | --- |
| **Level 1 · Outcome** | `Working` / `Needs you` / `Completed` / `Problem` | 展开 |
| **Level 2 · Explanation** | 为什么是这个 Agent / 为什么被阻塞 / 下一步自动做什么 | 一次点击 |
| **Level 3 · Technical Trace** | Execution ID / Attempt / Event / Model / Tokens / Latency / Logs | **永不默认展开** |

**不允许出现在 Level 1 的**：`Lease`、`fencing`、`provider_event_id`、`attempt_no`、`provider_key`、内部状态码（如 `PROVIDER_401`）等运行时概念。

### 11.2 Human Attention（Needs You）投影

```
CollaborationRequest.NEED_CONTEXT / UNRESOLVED
MemoryProposal.PENDING
Execution 失败 / 不可达（送达失败、超阈值）
Agent health = UNHEALTHY / Credential 失效
WorkItem BLOCKED
预算阈值（80% / 100%）
        │  read projection（非事实源）
        ▼
Needs You（Decision / Information / Approval / Problems）
```

**AttentionItem（只读视图字段，非表）**：

| 字段 | 说明 |
| --- | --- |
| `source_type` / `source_id` | 事实源定位（CR / Proposal / Execution / WorkItem / Agent / Budget） |
| `project_id` / `agent_id?` | 归属与责任 Agent |
| `title` | 用户语言标题（"Reviewer Agent needs your help"） |
| `summary` | 发生了什么 |
| `reason` | **为什么需要你** |
| `blocked` | 阻塞了什么 |
| `recommended_action?` | 建议做法（可空，但为空时必须有 `available_actions`） |
| `available_actions[]` | 可执行动作（至少 1 个） |
| `urgency` | `now` / `today` / `later` |
| `created_at` | 用于排序 |

**硬的约束**：

1. **AttentionItem 不是业务事实源**：MVP **不需要** `attention_items` 表，直接查询/联合视图即可；后续性能不足再 materialize（仍需从事实源重建）。
2. **UX invariant**：任何进入 Needs You 的事项**必须带至少一个可执行动作**；不允许只展示错误。
3. **单入口**：Memory / Work / Permission 审批统一进 `Needs You → Approval`；MVP 不设独立 Approval Center。
4. **就地操作回源**：在 Needs You 里操作后，状态迁移由**原模块**执行（Inbox 不做第二事实源，不直接写别人的表）。

### 11.3 与三 lifecycle 的关系（不变）

本模型**不改动** Collaboration / Work Management / Agent Execution 三个独立 lifecycle，也不引入第四个 lifecycle；它只是它们的**读侧投影与呈现规范**。

---

## 12. 实施顺序（v0.6：S1/S2/S3 竖切）

> **唯一基线 = `docs/design/detailed/09-implementation-checklist.md`**。
> **v0.6 拍板（D7）**：实施顺序 = **S1 → S2 → S3** 三刀竖切；下表的 M 编号退为**能力域标签**（用于指向各 detailed 分册），**不再表示实施顺序**。
>
> - **S1 一句话 → 产出**：`@mention` → CollaborationRequest → Resolver → Decision → Execution → event/result → 回帖（Agent 端用 stub，见 detailed/10）
> - **S2 记忆闸门**：`propose_memory` → 人审 → memory_items → 检索 → dispatch 注入
> - **S3 工作推进**：WorkItem + Built-in Provider + Work 页面 + WorkItem ↔ Execution 关联
> - 之后：V1+ E9 Jira、1k 压测与完整可观测（M8/M9 内容）

**能力域标签对照（不是顺序）**：

| 标签 | 交付 | 对应 Epic |
| --- | --- | --- |
| M1 | monorepo + auth/JWT + Org/Team/Project/Member | E1 |
| M2 | Channel + Message + seq + WS 网关 + outbox relay | E3 |
| M3a | Agent + Credential + lifecycle/activity/health | E2 |
| M3b | Connector 基础（transport：`collaboration.request` / dispatch / ack） | E7 部分 |
| M4a | Permission + Approval 三态（8 键） | E6 |
| M4b | Trigger + CollaborationRequest + Resolver + Decision | E4 |
| M5 | Memory + 人审门禁 + 索引 | E5 |
| M6 | WorkItem + Built-in Provider + Work 页面 | E8 |
| M7 | Agent Execution domain 完整（attempts/events/artifacts/resume） | E7 |
| M8 | Observability + 监控 Dashboard + 压测 | E10 |
| M9 | 集成 + 端到端 | 全部 |
| V1+ | Jira Provider 适配器 | E9 |

> 评审建议改为 **S1/S2/S3 竖切**（@mention→产出 / 记忆闸门 / 工作推进），**尚未拍板**（见 `docs/review/`）。

---

## 13. 更新记录

| 版本 | 日期 | 变更 |
| --- | --- | --- |
| v0.1 | 2026-09-07 | 初稿：独立系统 + 3 Worker + Connector + 实施顺序 |
| v0.2 | 2026-09-08 | 原型评审修订：6 态 + 错误码 + Source 约束 + 集成扩展点 + ERROR 触发恢复 |
| v0.3 | 2026-09-08 | 架构评审推倒：删除 AgentBoard execution / 新增 Agent Execution domain（agent_executions + attempts + events + artifacts）/ 新增 Work Management 域（Provider 抽象 + Built-in + bindings + projections）/ CollaborationRequest 一等实体 / Permission effect 改 REQUIRE_APPROVAL / Agent lifecycle+activity 拆分 / 删除 can_execute/can_review / 移除 projects.integration_backend 与 issue_tracker / Trigger 多源汇聚 / Project 不再挂 Provider 字段 |
| v0.4.5 | 2026-09-10 | **正文回修**（此前只改摘要未改正文）：CR 状态收敛 6 态；Resolver 改 `lifecycle ∩ slot ∩ 有效权限`，§6.4 ERROR 不再挡候选；`work_item_projections` 架构图/表清单/DDL 三处删除；Connector 协议改 `execution.resume_request`/`resume_ack`；`execution_events` 加 `provider_event_id + attempt_id NOT NULL + UNIQUE`；`work_management_connections` 改 `(org_id, owner_user_id)`；`getSelfMetadata` 补"只返回 capability"；权限 7 键 → 8 键（DDL + 默认矩阵 + §3.1）；§8 协议汇总区分消息级/执行级 resume |
| v0.5 | 2026-09-10 | **技术栈拍板 .NET**（ASP.NET Core + EF Core + outbox relay，BullMQ 移除，协议层中立）；**DDL 收口**：补 `agents.max_concurrency/health`、`agent_executions.active_attempt_no/terminal_envelope_id` + 幂等唯一索引、`execution_attempts` 状态 CHECK 与 dispatch/续传列、`work_items.binding_id/provider_*/search_text`，新增 `outbox_events` / `webhook_inbox` / `work_management_webhooks` 三表入清单与 DDL；§11 实施顺序与 detailed/09 对齐（M7=E7、M8=E10） |
| v0.6 | 2026-09-10 | **D9 冻结**：ACCEPT 后由 E4 事务外直调 E7 创建 Execution，outbox 仅兜底重试（§11 同步）；**D7 拍板**：实施切法改 **S1/S2/S3 竖切**，M1–M9 退为能力域标签（§11 重写）；**S1 的 Agent 端 = stub**（新增 `detailed/10-agent-stub-and-sdk.md`，B1–B8 行为矩阵）；补 E6 审计触发点（E10 §3.1）与 E5 `policy.evaluate('propose_memory')` 调用点 |
| v0.7 | 2026-09-10 | **§4.2.1 换成 per-lease ZSET + fencing**（Hash 版标废弃；含 rebuild 顺序与 `REBUILDING` 闸；E4 §4.2 同步）；**`dispatch_ack` 语义定型为 transport 收据**（去掉 `accepted=false` 与"dispatch 拒收→重路由"这条在 CR 状态机里无合法迁移的分支，改为送达失败 → 重试同一 attempt → Inbox）；**§4.3 Memory 门禁改 `propose_memory`**（此前误写 `write_memory`）；**§4.4 Provider 摘要同步正式接口**（`listStatuses(binding)` / `getStatusMapping(binding) → CanonicalStatusMapping[]`）；§4.1 候选过滤去掉 `active_slots` 表述 |
| v0.8 | 2026-09-10 | **新增 §11 UI Projection & Human Attention Model**（Progressive Disclosure 三级；AttentionItem 为只读投影、非事实源；UX invariant"每条必须带可执行动作"；单入口 Needs You → Approval）；§10 缓存表补 slot ZSET/fence/rebuilding key 并把 `wmc` 修为 Org 级；§1–§10 三个 lifecycle 设计不变 |
