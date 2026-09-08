# MateOS 系统架构设计（SYSTEM_DESIGN v0.3）

| 文档信息 | 内容 |
| --- | --- |
| 上游文档 | docs/requirement/MateOS-总体需求文档.md（PRD v0.4） |
| 文档状态 | Draft |
| 版本 | v0.3 |
| 日期 | 2026-09-08 |
| 前提 | **MateOS 为独立自洽系统**：Agent Execution 域自研（永久不切到任何外部执行后端）；Work Management 是独立域（Built-in + Jira Provider 抽象，与 Execution 完全解耦） |

> **v0.2 → v0.3 变更摘要**（架构评审后推倒重来）：
> 1. **删除 AgentBoard execution backend**：Architecture diagram、Project schema、Runtime dispatch path 全清
> 2. **新增 Agent Execution domain（§6）**：`agent_executions` / `execution_attempts` / `execution_events` / `execution_artifacts`；BullMQ 不再是 source of truth
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
> 12. **Resolver 调度改用 lifecycle=ACTIVE + active_slots < max_concurrency**（不再用 activity；activity 仅做 UI derived）
> 13. **Attempt ≠ WS session**：reconnect 不新建 attempt；新增 `execution.resume` 协议
> 14. **Event 协议级幂等**：`provider_event_id` + UNIQUE(attempt_id, provider_event_id)
> 15. **E8 简化**：`work_item_projections` 表删除；`work_items` 单表含 `search_text` / `provider_status` / `provider_meta`
> 16. **ProviderKey 去硬编码**：`type ProviderKey = string` + `ProviderRegistry` 模式
> 17. **DB UNIQUE 强制 active binding 唯一**：`work_item_bindings(project_id) WHERE is_active=true`
> 18. **Provider 路由规则冻结**：CREATE 用 `activeBinding.provider_key`；UPDATE 用 `work_item.provider_key`
> 19. **Jira Status Mapping 动态**：`listStatuses(binding)` 而非 Provider 级静态模板
>
> **v0.3.1 → v0.3.2 并发与幂等收口**（架构评审后）：
> 20. **Capacity 原子化**：旧方案 `agents.max_concurrency + agents.active_slots` 是 DB + Redis 双事实源，存在 SELECT → accept → INSERT 的 race。**新方案**：仅 `max_concurrency` 落 DB（durable config），`active_slots` 归 Redis Lua atomic semaphore；`agent-capacity:{agent_id}` hash，tryAcquireSlot / releaseSlot 一次 EVAL 完成
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
│  Application 层（NestJS Modular Monolith）           │
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
| 后端 | NestJS (Node 22 LTS) | 模块化匹配 Permission + Domain 分层 |
| ORM | Prisma | Schema 即文档 |
| DB | PostgreSQL 16 + pgvector | 关系 + JSONB + 全文 + 向量一库 |
| 缓存/队列 | Redis 7 + BullMQ | presence / pub/sub / 限流 / 异步任务 |
| 对象存储 | S3 / MinIO | 附件 + Execution Artifact |
| LLM | Provider Adapter（OpenAI-compatible） | Credential 分离 |
| 沙箱 | Docker（V3） | V1 不执行代码，架构预留 |
| 部署 | Docker Compose → K8s | |
| 可观测 | OTel + Prometheus + Grafana + Loki | |

---

## 3. 服务拆分

```
apps/
  web/              # Next.js 前端
  api/              # NestJS 单体（含 ws 模块）
packages/
  contracts/        # DTO + zod schema（含 WorkItem / Execution）
  config/           # eslint/tsconfig
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
| orchestrator | Resolver（lifecycle ∩ activity 过滤）/ Decision 状态机 | |
| memory | Memory CRUD / Source 溯源 / 人审门禁 / 索引 | 消息存储 |
| permission | 7 键 + 3 态（ALLOW/DENY/REQUIRE_APPROVAL）+ 三层覆盖 | UI 配置 |
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
  ① 硬过滤：lifecycle=ACTIVE ∩ active_slots < max_concurrency ∩ permission 允许    # v0.3.1 改
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

**新方案**：单一事实源（Redis Lua atomic semaphore）

```
Redis key: agent-capacity:{agent_id}
  used        当前已 reservation 的 slot 数
  max         来自 agents.max_concurrency（启动时写入）
  leases      hash: lease_id → collaboration_request_id
```

```
EVAL tryAcquireSlot(agent_id, lease_id, collab_id, ttl)
  1. if leases[lease_id] exists → 重复 reservation，idempotency 返回成功
  2. if used >= max → return {0, used}            # 失败
  3. used++, leases[lease_id] = collab_id
  4. EXPIRE
  5. return {1, used+1}

EVAL releaseSlot(agent_id, lease_id)
  1. if not leases[lease_id] → return 0
  2. HDEL leases[lease_id]
  3. used = max(0, used - 1)
  4. return 1
```

**单一事实源**：
- `max_concurrency`：DB 字段（durable config）
- `active_slots`：Redis 字段（Runtime scheduling state）
- DB + Redis **没有**双写

**Slot 提前 reservation**：

```
Resolver 选 Agent → 立即 tryAcquireSlot → 成功才 dispatch
  ↓
Agent ACCEPT
  ↓
E4 调 E7 API 创建 execution（slot lease 仍由 E4 持有，引用 collaboration_requests.slot_lease_id）
  ↓
E7 推 execution.result SUCCEEDED / FAILED → E4 释放 slot
  ↓
或：Agent REJECT / NEED_CONTEXT / timeout / 管理员 cancel → E4 释放 slot
```

### 4.3 Memory 写入门禁

```
Agent proposal（type / content / source 引用）
  ↓ permission check：write_memory → REQUIRE_APPROVAL
  ↓ 写 memory_proposals（status=PROPOSED）
  ↓ WS 推送给 project 内有 approve_memory 权限的人类
  ↓ Approve → 写 memory_items（status=APPROVED）→ 入库 + 索引
  ↓ Reject → memory_proposals.status=REJECTED
```

Source 三件套（`source_type` / `source_channel_id` / `source_message_seq`）强约束。

### 4.4 Work Management 抽象

```
WorkManagementProvider interface
  ├─ getCapabilities() → ProviderCapabilities
  ├─ listWorkItems(query) → WorkItemPage
  ├─ getWorkItem(ref) → WorkItem
  ├─ createWorkItem(input) → WorkItem
  ├─ updateWorkItem(ref, changes) → WorkItem
  ├─ addComment(ref, comment) → WorkComment
  └─ getStatusMapping() → CanonicalStatusCategory[]

BuiltInProvider（MVP）
  └─ work_items / work_item_projections 直接读写 PG

JiraProvider（V1+ E9）
  └─ Jira REST + Webhook → 维护 work_item_projections 缓存
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
| work_items | Built-in WorkItem | **E8 v0.4 新增** |
| work_item_projections | Jira 等外部 Provider 的本地缓存 | E8 |
| work_item_bindings | Project × WorkItemProvider 关联 | E8 |
| work_comments | WorkItem 评论 | E8 |
| work_relations | WorkItem 关系（blocks / relates_to） | E8 |
| work_management_connections | Provider 连接信息（OAuth token 等） | E8 |
| work_sync_audit | Provider 同步审计 | E8 |
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
  daily_limit_usd NUMERIC(10,2) DEFAULT 5.00,
  monthly_budget_usd NUMERIC(10,2) DEFAULT 50.00,
  created_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_agents_lifecycle_activity ON agents(lifecycle, activity);

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
                        CHECK (status IN ('PENDING','ACCEPTED','REJECTED','NEED_CONTEXT','EXECUTING','COMPLETED','FAILED','UNRESOLVED')),
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
  started_at          TIMESTAMPTZ,
  completed_at        TIMESTAMPTZ,
  failure_code        TEXT,
  failure_message     TEXT,
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_executions_agent ON agent_executions(agent_id, created_at DESC);
CREATE INDEX idx_executions_status ON agent_executions(status, created_at DESC);

-- execution_attempts
CREATE TABLE execution_attempts (
  id              UUID PRIMARY KEY,
  execution_id    UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_no      INT NOT NULL,
  runtime_session_id TEXT,
  status          TEXT NOT NULL,
  started_at      TIMESTAMPTZ,
  completed_at    TIMESTAMPTZ,
  error           TEXT,
  UNIQUE (execution_id, attempt_no)
);

-- execution_events
CREATE TABLE execution_events (
  id              BIGSERIAL PRIMARY KEY,
  execution_id    UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_id      UUID REFERENCES execution_attempts(id),
  event_type      TEXT NOT NULL,                       -- 'STDOUT' | 'PROGRESS' | 'TOOL_CALL' | 'LLM_TICK' | 'ARTIFACT' | 'ERROR'
  payload         JSONB NOT NULL,
  trace_id        TEXT,
  created_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_events_execution_time ON execution_events(execution_id, created_at);

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

-- work_items（Built-in Provider）
CREATE TABLE work_items (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id),
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
  created_by_type     TEXT NOT NULL,
  created_by_id       UUID NOT NULL,
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now()
);

-- work_item_projections（Jira 等 Provider 的本地缓存）
CREATE TABLE work_item_projections (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id),
  provider_key        TEXT NOT NULL,                    -- 'jira'
  external_ref        TEXT NOT NULL,                    -- 'PROJ-123'
  external_url        TEXT,
  type                TEXT NOT NULL,
  title               TEXT NOT NULL,
  status              TEXT NOT NULL,
  canonical_status_category TEXT NOT NULL,
  provider_status     TEXT,                             -- 原始 status 字面值
  assignee_type       TEXT,
  assignee_id         UUID,
  due_at              TIMESTAMPTZ,
  raw_payload         JSONB NOT NULL,                   -- 原始 provider DTO
  last_sync_at        TIMESTAMPTZ,
  last_sync_status    TEXT,                             -- 'OK' | 'FAILED' | 'CONFLICT'
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now(),
  UNIQUE (provider_key, external_ref)
);

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
CREATE TABLE work_management_connections (
  id              UUID PRIMARY KEY,
  provider_key    TEXT NOT NULL,                        -- 'jira'
  project_id      UUID NOT NULL REFERENCES projects(id),
  user_id         UUID NOT NULL REFERENCES users(id),
  access_token_encrypted  BYTEA NOT NULL,
  refresh_token_encrypted BYTEA,
  expires_at      TIMESTAMPTZ,
  meta            JSONB,
  created_at      TIMESTAMPTZ DEFAULT now()
);

-- permissions（v0.4 effect 改三态）
CREATE TABLE permissions (
  id           UUID PRIMARY KEY,
  scope_type   TEXT NOT NULL CHECK (scope_type IN ('PROJECT','CHANNEL')),
  scope_id     UUID NOT NULL,
  subject_type TEXT NOT NULL CHECK (subject_type IN ('USER','AGENT')),
  subject_id   UUID NOT NULL,
  perm_key     TEXT NOT NULL CHECK (perm_key IN (
                'read_message','write_message','write_memory',
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

**Resolver 过滤**：`lifecycle=ACTIVE ∩ activity ∈ {AVAILABLE, THINKING}`（OFFLINE/ERROR/WORKING/WAITING_CONTEXT 排除）

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

**关键原则**：BullMQ job 绝不能成为 Agent Execution 的事实源。BullMQ 只做 transport / scheduler（把任务推到 Runtime），`agent_executions` + `execution_attempts` + `execution_events` 才是事实源。

### 6.4 ERROR 触发（lifecycle 维度）

| 触发 | 行为 |
| --- | --- |
| Provider 5xx 连续 3 次 | activity=ERROR |
| Provider 401/403 | activity=ERROR + reason=auth_failed |
| 日/周限额触顶 | activity=ERROR + reason=rate_limit |
| Sandbox 启动失败（V3） | activity=ERROR |

ERROR 状态下 Agent **不进入 Resolver 候选**（lifecycle 仍是 ACTIVE，只是 activity 异常）。

### 6.5 Connector 协议（v1.1 — v0.3.1 协议边界拆开）

```jsonc
// 通用 envelope
{ "v": 1, "type": "hello|heartbeat|status|dispatch|event|result|error|resume", "id": "uuid", "ts": 0, "payload": {} }

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

{ "type": "resume", "payload": {
    "execution_id": "...", "last_event_seq": 42
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
- **v0.3.1 attempt ≠ WS session**：`execution.resume` 协议补发；reconnect 不新建 attempt

设计原则：Agent 无入站端口（NAT 穿透）；上下文注入走引用；Project 知识边界由服务端强制。

---

## 7. Permission Model 实现

- 7 个权限键
- 3 态：`check(subject, perm, scope) → ALLOW | DENY | REQUIRE_APPROVAL`
- 三层覆盖：默认矩阵 → Project 覆盖 → Channel 覆盖
- Redis 缓存 + `perm.changed` pub/sub 失效

### 7.1 默认矩阵

| 权限 | Human owner | Human member | Agent (lifecycle=ACTIVE) |
| --- | --- | --- | --- |
| read_message | ALLOW | ALLOW | ALLOW |
| write_message | ALLOW | ALLOW | ALLOW |
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
| 内部异步 | BullMQ |
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
| Channel 近期消息 | `timeline:{channel_id}` | 10min |
| 未读计数 | `unread:{member_id}:{channel_id}` | 24h |
| 权限矩阵 | `perm:{scope}:{subject}` | 5min |
| Mention 排序特征 | `mfeat:{project}:{agent_id}` | 1h |
| Mention 解析结果 | `mention:{id}` | 10min |
| WorkItem 缓存 | `workitem:{provider}:{ref}` | 5min |
| Provider token | `wmc:{provider}:{user_id}` | refresh |

---

## 11. 实施顺序（M1-M6 + V1+）

| 阶段 | 交付 | 对应 Epic |
| --- | --- | --- |
| M1 | monorepo + auth + Org/Team/Project/Member | E1 |
| M2 | Channel + Message + seq + WS 网关 | E3 |
| M3 | Agent + Credential + lifecycle/activity | E2 + E7 部分（Connector） |
| M4 | Trigger + CollaborationRequest + Resolver + Decision | E4 |
| M5 | Memory + 人审门禁 + 索引 | E5 |
| M6 | WorkItem + Built-in Provider + Work 页面 | E8 |
| M7 | Permission + Approval + 三态 | E6 |
| M8 | Agent Execution domain（attempts/events/artifacts） | E7 |
| M9 | Observability + 监控 + 压测 | E10 |
| V1+ | Jira Provider 适配器 | E9 |

---

## 12. 更新记录

| 版本 | 日期 | 变更 |
| --- | --- | --- |
| v0.1 | 2026-09-07 | 初稿：独立系统 + 3 Worker + Connector + 实施顺序 |
| v0.2 | 2026-09-08 | 原型评审修订：6 态 + 错误码 + Source 约束 + 集成扩展点 + ERROR 触发恢复 |
| v0.3 | 2026-09-08 | 架构评审推倒：删除 AgentBoard execution / 新增 Agent Execution domain（agent_executions + attempts + events + artifacts）/ 新增 Work Management 域（Provider 抽象 + Built-in + bindings + projections）/ CollaborationRequest 一等实体 / Permission effect 改 REQUIRE_APPROVAL / Agent lifecycle+activity 拆分 / 删除 can_execute/can_review / 移除 projects.integration_backend 与 issue_tracker / Trigger 多源汇聚 / Project 不再挂 Provider 字段 |
