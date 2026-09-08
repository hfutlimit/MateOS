# E8 · Work Management Core

| 字段 | 值 |
| --- | --- |
| Epic ID | E8 |
| 标题 | Work Management Core |
| 阶段 | MVP（M6） |
| 上游 | PRD v0.4 §2.4 / §5 FR-7 / SYSTEM_DESIGN v0.3 §9 Work Management 域 |
| 下游 | E9（Jira Provider）/ E7（Execution 可选引用 WorkItem）/ E4（Trigger from WorkItem V1+） |
| 状态 | Draft（v0.4 新增） |

## 1. 背景与动机

v0.4 把"项目管理"从 v0.3 的 E9（混入 Agent execution 的复合 epic）拆成两个独立 epic：
- **E8 Work Management Core**（本 epic）：WorkItem 域 + Provider 抽象 + Built-in 默认实现
- **E9 Work Management Integrations**：Jira Provider 适配器（V1+）

**WorkItem 与 Execution 完全独立**——WorkItem 不必有 Execution（人类编辑文档）；Execution 不必属于 WorkItem（`@backend 看下代码`）。Execution 通过 `work_item_ref` optional 引用 WorkItem。

## 2. 范围

### 2.1 In Scope

- `WorkManagementProvider` interface 抽象
- `BuiltInProvider`（MVP 默认实现）
- `WorkItem` CRUD（type / title / description / status / assignee / due / source）
- `WorkComment`（评论）
- `WorkRelation`（blocks / relates_to / parent_of）
- `work_item_bindings`（Project × Provider 关联）
- `work_item_projections`（Provider 缓存，Built-in 也用一份作为客户端缓存）
- `work_management_connections`（Provider 凭据存储，Built-in 留接口）
- 状态映射（canonical_status_category）
- Provider 切换语义：Change Provider（仅影响新）+ Migrate Existing WorkItems（独立 Wizard）
- P-Work 页面（MVP）

### 2.2 Out of Scope

- 第三方 Provider 实现（E9 Jira / V1+ Linear / GitHub Issues / Azure Boards）
- Sprint / Backlog（V1 简化为扁平列表，V2 引入）
- 自定义字段映射（V2+）
- WorkItem 历史迁移（Migrate Wizard，V2）

## 3. 数据模型

```sql
-- WorkItem（v0.4：Built-in 时 source of truth，Provider 时是本地缓存）
CREATE TABLE work_items (
  id                          UUID PRIMARY KEY,
  project_id                  UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  type                        TEXT NOT NULL CHECK (type IN ('TASK','STORY','BUG','EPIC')),
  title                       TEXT NOT NULL,
  description                 TEXT,
  status                      TEXT NOT NULL DEFAULT 'OPEN'
                              CHECK (status IN ('OPEN','IN_PROGRESS','IN_REVIEW','DONE','CLOSED')),
  canonical_status_category   TEXT NOT NULL DEFAULT 'TODO'
                              CHECK (canonical_status_category IN ('TODO','IN_PROGRESS','DONE')),
  assignee_type               TEXT CHECK (assignee_type IN ('HUMAN','AGENT')),
  assignee_id                 UUID,
  due_at                      TIMESTAMPTZ,
  created_by_type             TEXT NOT NULL,    -- 'HUMAN' | 'AGENT'
  created_by_id               UUID NOT NULL,
  provider_key                TEXT NOT NULL DEFAULT 'builtin',  -- 'builtin' | 'jira'
  external_ref                TEXT,             -- provider=non-builtin 时填
  external_url                TEXT,
  created_at                  TIMESTAMPTZ DEFAULT now(),
  updated_at                  TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_work_items_project_status ON work_items(project_id, status);
CREATE INDEX idx_work_items_project_type ON work_items(project_id, type);
CREATE INDEX idx_work_items_assignee ON work_items(assignee_type, assignee_id) WHERE assignee_id IS NOT NULL;
CREATE INDEX idx_work_items_due ON work_items(due_at) WHERE due_at IS NOT NULL;
CREATE UNIQUE INDEX uq_work_items_provider_external ON work_items(provider_key, external_ref) WHERE external_ref IS NOT NULL;

-- work_item_projections（v0.4：Built-in 也用，统一缓存）
-- v0.3 的 work_item_projections 仅用于 Provider 缓存；v0.4 统一为所有 WorkItem 的客户端缓存
CREATE TABLE work_item_projections (
  id                          UUID PRIMARY KEY,
  work_item_id                UUID NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
  project_id                  UUID NOT NULL,
  -- 展示相关字段
  type                        TEXT NOT NULL,
  title                       TEXT NOT NULL,
  status                      TEXT NOT NULL,
  canonical_status_category   TEXT NOT NULL,
  provider_key                TEXT NOT NULL,
  assignee_type               TEXT,
  assignee_id                 UUID,
  due_at                      TIMESTAMPTZ,
  -- 检索索引
  search_text                 tsvector,
  last_refreshed_at           TIMESTAMPTZ DEFAULT now(),
  UNIQUE (work_item_id)
);
CREATE INDEX idx_wip_search ON work_item_projections USING gin(search_text);

-- work_item_bindings（v0.4 替代 v0.3 的 projects.issue_tracker）
CREATE TABLE work_item_bindings (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  provider_key        TEXT NOT NULL,            -- 'builtin' | 'jira'
  connection_id       UUID,                     -- work_management_connections.id
  external_project_ref TEXT,                   -- Jira project key（'PROJ'）
  settings            JSONB,                    -- status mapping 等
  is_active           BOOLEAN NOT NULL DEFAULT true,
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now(),
  UNIQUE (project_id, provider_key)
);

-- work_management_connections（Provider 凭据）
CREATE TABLE work_management_connections (
  id                      UUID PRIMARY KEY,
  provider_key            TEXT NOT NULL,         -- 'jira' | 'builtin' (builtin 留空)
  project_id              UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  user_id                 UUID NOT NULL REFERENCES users(id),  -- 谁授权的
  access_token_encrypted  BYTEA,                -- Built-in 不需要
  refresh_token_encrypted BYTEA,
  expires_at              TIMESTAMPTZ,
  meta                    JSONB,
  created_at              TIMESTAMPTZ DEFAULT now()
);

-- work_comments
CREATE TABLE work_comments (
  id            UUID PRIMARY KEY,
  work_item_id  UUID NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
  author_type   TEXT NOT NULL CHECK (author_type IN ('HUMAN','AGENT','SYSTEM')),
  author_id     UUID,
  body          TEXT NOT NULL,
  created_at    TIMESTAMPTZ DEFAULT now()
);

-- work_relations
CREATE TABLE work_relations (
  id              UUID PRIMARY KEY,
  from_work_item_id UUID NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
  to_work_item_id   UUID NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
  relation_type     TEXT NOT NULL CHECK (relation_type IN ('BLOCKS','BLOCKED_BY','RELATES_TO','PARENT_OF','CHILD_OF')),
  created_at        TIMESTAMPTZ DEFAULT now(),
  UNIQUE (from_work_item_id, to_work_item_id, relation_type),
  CHECK (from_work_item_id <> to_work_item_id)
);
```

### 3.1 Provider 抽象

```ts
// packages/contracts
interface WorkManagementProvider {
  readonly key: 'builtin' | 'jira' | 'linear' | 'github_issues';  // V1+ 扩展

  getCapabilities(): ProviderCapabilities;
  getSelfMetadata(): Promise<ProviderMetadata>;

  listWorkItems(query: WorkItemQuery): Promise<WorkItemPage>;
  getWorkItem(ref: WorkItemRef): Promise<WorkItem>;
  createWorkItem(input: WorkItemInput): Promise<WorkItem>;
  updateWorkItem(ref: WorkItemRef, changes: WorkItemChanges): Promise<WorkItem>;

  addComment(ref: WorkItemRef, comment: WorkCommentInput): Promise<WorkComment>;
  listComments(ref: WorkItemRef): Promise<WorkComment[]>;

  getStatusMapping(): CanonicalStatusMapping[];
}

type ProviderCapabilities = {
  supportsComments: boolean;
  supportsRelations: boolean;
  supportsCustomFields: boolean;
  supportsWebhooks: boolean;
};

type WorkItemRef = 
  | { provider: 'builtin', work_item_id: UUID }
  | { provider: 'jira', external_ref: string };
```

**业务层只依赖 `WorkManagementProvider` 接口**；`if (provider === 'jira') ...` 永不允许。

### 3.2 BuiltInProvider（V1 默认）

- Source of truth = `work_items` 表
- 默认实现，无需 OAuth
- Status mapping 一对一：Jira status = canonical
- WorkItem lifecycle：OPEN → IN_PROGRESS → IN_REVIEW → DONE → CLOSED

## 4. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET | `/projects/:id/work-items` | 列表（带过滤） | project member |
| GET | `/work-items/:id` | 详情 | project member |
| POST | `/projects/:id/work-items` | 创建 | project member |
| PATCH | `/work-items/:id` | 修改 | creator / assignee / project owner |
| POST | `/work-items/:id/transition` | 状态机迁移 | 视 target 状态 |
| GET / POST | `/work-items/:id/comments` | 评论 | project member |
| POST | `/work-items/:id/relations` | 创建关联 | project member |
| GET | `/work-items/search?q=...` | 搜索 | project member |
| GET | `/projects/:id/work-management/bindings` | 列出绑定 | project owner |
| POST / PATCH | `/projects/:id/work-management/bindings` | 切换 Provider | project owner |
| GET | `/projects/:id/work-management/providers` | 列出可用 Provider 能力 | project member |

### 4.2 WebSocket

- `work_item.created` / `work_item.updated` / `work_item.status_changed`
- `work_item.comment_added`

## 5. 关键流程

### 5.1 创建 WorkItem

```
POST /projects/:id/work-items
  → 查 binding：project_id → work_item_bindings where is_active=true
  → workManagementProviderRegistry.get(binding.provider_key) 拿实例
  → provider.createWorkItem(input)
    - builtin: INSERT work_items + work_item_projections + status mapping
    - jira: POST Jira /issue + INSERT work_item_projections 缓存
  → 写 audit
  → WS 推 work_item.created
```

### 5.2 状态机迁移

```
work_items.status 严格按规则迁移：
  OPEN ──► IN_PROGRESS ──► IN_REVIEW ──┬─► DONE
                                        └─► IN_PROGRESS (review 退回)
  IN_PROGRESS ──► CLOSED (cancel)
  DONE ──► CLOSED
```

`canonical_status_category` 同步：
- OPEN / TODO = TODO
- IN_PROGRESS / IN_REVIEW = IN_PROGRESS
- DONE / CLOSED = DONE

### 5.3 切换 Provider 语义（关键）

**两阶段语义**：

1. **Change Provider**（即时生效，仅影响新 WorkItem）
   - PATCH /work-management/bindings { provider_key: 'jira' }
   - 更新 binding.is_active 切换
   - **不影响**历史 WorkItem

2. **Migrate Existing WorkItems**（独立 Wizard，V2 实现）
   - 列出当前 provider_key=builtin 的所有 WorkItem
   - 用户选择：保留在 Built-in / 导出 CSV / 同步到新 Provider
   - V1 简化为"Change Provider"按钮 + 说明文字

### 5.4 WorkItem 状态映射（Status Mapping）

| Built-in | Jira (V1+) | Linear (V2+) | canonical_status_category |
| --- | --- | --- | --- |
| OPEN | To Do | Backlog | TODO |
| IN_PROGRESS | In Progress | In Progress | IN_PROGRESS |
| IN_REVIEW | In Review | In Review | IN_PROGRESS |
| DONE | Done | Done | DONE |
| CLOSED | Closed | Cancelled | DONE |

provider_status 字面值保留在 `work_item_projections.provider_status`（v0.4 简化，存到 work_items.external_meta）。

## 6. UI

### 6.1 页面

- **P-Work**（M6 新增 MVP）：列表 + 详情 + 新建
- **P4 Project Dashboard**：Work Management 卡片（v0.4 改"外部协作" → "Work Management"，Built-in + Jira V1+ 动态表单）
- **P11 Project Settings - Work Management**（v0.4 新增）：Provider 切换 + Status mapping + Connection

### 6.2 关键组件

- `<WorkItemList>`：表格（type / status / title / assignee / due / updated）
- `<WorkItemCard>`：详情页主卡
- `<ProviderBadge>`：显示当前 Provider（builtin / jira）
- `<WorkItemStatus>`：canonical status + provider status 双显
- `<WorkItemBindingForm>`：动态表单（按 Provider 渲染字段）

## 7. 验收标准

### 7.1 功能

- **F1** WorkItem CRUD 全通（OPEN → IN_PROGRESS → IN_REVIEW → DONE → CLOSED）
- **F2** **v0.4 新增** `work_item_bindings` 关联 Project × Provider
- **F3** Project 默认 binding = builtin，无需配置
- **F4** business code 不写 `if (provider === 'jira') ...`（lint 规则 / code review 守门）
- **F5** Change Provider 立即影响新 WorkItem；历史 WorkItem 保留在原 provider
- **F6** canonical_status_category 自动从 status 派生
- **F7** WorkItem 关联 Execution（v0.4 改）：execution.work_item_ref optional 引用

### 7.2 E2E

- `e2e/E8-001-workitem-lifecycle`
- `e2e/E8-002-default-binding-builtin`
- `e2e/E8-003-change-provider-doesnt-migrate-history`（v0.4 新）—— 切到 Jira 后创建新 WorkItem 不影响历史
- `e2e/E8-004-provider-isolation`（v0.4 新）—— 业务代码 lint 通过
- `e2e/E8-005-status-mapping`（v0.4 新）

### 7.3 非功能

- WorkItem 列表 P95 < 200ms（1000 条 / project）
- search P95 < 500ms（10k WorkItem / project）

## 8. 与其他 Epic 的关系

- **被依赖**：E9（Jira Provider 适配）/ E4（WorkItem 状态变化 → Trigger，V1+）/ E7（execution.work_item_ref optional）
- **依赖**：E1（Project + Member）/ E6（assignee 操作权限）
- **冲突裁决**：WorkItem 与 Execution 完全独立（PRD v0.4 §3.2 / SD v0.3 §1.2）

## 9. 风险与开放问题

- **R1**：Migrate Wizard 何时做？— V1 简化"V1+ 提供导出 CSV"
- **R2**：V1 仅 Built-in 时 Status mapping 是否真需要？— 仍定义（V1+ 接入 Jira 时直接用）
- **R3**：WorkItem assignee 是 Agent 时，Agent lifecycle=PAUSED 后如何显示？— 列表显示 lifecycle 徽标，状态点同步

## 10. 实施顺序（M6）

1. Provider 抽象 + BuiltInProvider（packages/contracts）
2. work_items / work_item_projections / work_item_bindings 表
3. Project 默认 binding = builtin 初始化
4. WorkItem CRUD + status machine
5. 评论 + 关联
6. P-Work 页面（MVP）
7. P4 Project Dashboard / P11 Settings 集成
8. E2E 套件
