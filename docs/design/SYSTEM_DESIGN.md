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
  ├─ status: PENDING | ACCEPTED | REJECTED | NEED_CONTEXT | EXECUTING | COMPLETED | FAILED
  ├─ deadline_s
  └─ idempotency_key

↓ Resolver（如未指定 target_agent_id）
  ① 硬过滤：lifecycle=ACTIVE ∩ activity ∈ {AVAILABLE, THINKING} ∩ permission 允许
  ② Capability ranking：capability_match + load + accept_rate_30d
  ③ 产出 Top-N 候选

↓ 派发到 Agent（Runtime 路径或 Built-in WorkItem 执行）
  ① 写 decision_records
  ② WS 推 CollaborationRequest.resolved
  ③ 决策 Accept → 创建 Execution（E7）| Reject / Need Context → 落 decision + 通知发起人
```

### 4.2 Decision 状态机

```
CollaborationRequest.status
  PENDING ─┬─► ACCEPTED ──► 触发 Execution（E7，绑定 work_item_ref?）
           ├─► REJECTED（reason 必填）
           ├─► NEED_CONTEXT（needs[] 必填，阻塞等待补充）
           └─► EXECUTING（Execution 已创建）
                ├─► COMPLETED
                └─► FAILED
```

`decision_records` 表（事实源）——所有 Decision 必带 `analysis{capability, context_score, permission}`，UI 决策卡片从 decision_records 投影生成（**不复制**）。

PENDING 超时（默认 60s）→ 取下一个候选；全部超时 → CollaborationRequest.status=UNRESOLVED + 通知发起人。

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

### 6.5 Connector 协议（v1）

```jsonc
// 通用 envelope
{ "v": 1, "type": "hello|heartbeat|status|dispatch|progress|result|error", "id": "uuid", "ts": 0, "payload": {} }

// dispatch
{ "type": "dispatch", "payload": {
    "execution_id": "...",                          // ← 新增（v0.4）
    "collaboration_request_id": "...",              // ← 新增
    "work_item_ref": {...}?,                        // ← optional
    "context": { "memory_refs": [...], "recent_messages": [...], "permissions": {...} },
    "deadline_s": 600, "idempotency_key": "..."
}}

// result
{ "type": "result", "payload": {
    "execution_id": "...",
    "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason": "...", "needs": [...],
    "analysis": { "capability": true, "context_score": 88, "permission": true },
    "output": { "markdown": "...", "code": "...", "tokens_in": 0, "tokens_out": 0, "latency_ms": 0 }
}}
```

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

### 9.1 Provider 抽象

```ts
interface WorkManagementProvider {
  key: 'builtin' | 'jira' | 'linear' | 'github_issues';   // future
  getCapabilities(): ProviderCapabilities;
  listWorkItems(query: WorkItemQuery): Promise<WorkItemPage>;
  getWorkItem(ref: WorkItemRef): Promise<WorkItem>;
  createWorkItem(input: WorkItemInput): Promise<WorkItem>;
  updateWorkItem(ref: WorkItemRef, changes: WorkItemChanges): Promise<WorkItem>;
  addComment(ref: WorkItemRef, comment: WorkCommentInput): Promise<WorkComment>;
  getStatusMapping(): CanonicalStatusMapping[];        // provider status → canonical category
  getSelfMetadata(): Promise<ProviderMetadata>;         // 用于 UI 渲染动态配置
}
```

**业务层**：`workManagementProviderRegistry.get(binding.provider_key)` 拿到实例后调用，**永不分 provider 类型**。

### 9.2 Built-in Provider

- Source of truth = `work_items` 表
- 默认实现，V1 内置
- WorkItem lifecycle：OPEN → IN_PROGRESS → IN_REVIEW → DONE → CLOSED
- Project 默认绑定 builtin，无需配置

### 9.3 Jira Provider（V1+ E9）

- Source of truth = Jira Cloud
- MateOS 维护 `work_item_projections` 本地缓存
- 通过 webhook 接收 Jira 状态变化
- 双绑去重：payload_hash 防重复同步
- 冲突：CONFLICT + 通知 owner 仲裁

### 9.4 Provider 切换语义

**Change Provider**（仅影响新 WorkItem） + **Migrate Existing WorkItems**（独立 Wizard）：
- 切换 provider_key 立即生效
- 历史 WorkItem 迁移走向导，可选导出 CSV / 单向同步到新 Provider
- V1 简化为仅 Change Provider；Migrate 留 V2

### 9.5 状态映射（Status Mapping）

```
Jira:        "To Do"      "In Progress"  "In Review"  "Done"  "Closed"
             ↓             ↓              ↓            ↓       ↓
Canonical:   TODO          IN_PROGRESS    IN_PROGRESS  DONE    DONE

Linear:      "Backlog"     "In Progress"  "In Review"  "Done"  "Cancelled"
             ↓             ↓              ↓            ↓       ↓
Canonical:   TODO          IN_PROGRESS    IN_PROGRESS  DONE    CLOSED
```

provider status 字面值保留（不丢失信息），canonical category 用于跨 Provider 聚合与统计。

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
