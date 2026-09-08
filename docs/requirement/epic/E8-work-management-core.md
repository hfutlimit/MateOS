# E8 · Work Management Core

| 字段 | 值 |
| --- | --- |
| Epic ID | E8 |
| 标题 | Work Management Core |
| 阶段 | MVP（M6） |
| 上游 | PRD v0.4 §2.4 / §5 FR-7 / SYSTEM_DESIGN v0.3 §9 / v0.4.1 协议收口 |
| 下游 | E9（Jira Provider）/ E7（Execution 可选引用 WorkItem）/ E4（Trigger from WorkItem V1+） |
| 状态 | Draft（v0.4.1 简化版） |

## 1. 背景与动机

v0.4 把 WorkItem 域抽出来。**v0.4.1 关键收口**：

1. **删除 `work_item_projections` 表**——`work_items` 直接作为 canonical local representation（一张表解决，避免双模型漂移）
2. **ProviderKey 去硬编码**——`type ProviderKey = string` + ProviderRegistry 模式
3. **DB invariant 冻结**：`work_item_bindings` 同 project 同 active 唯一
4. **Provider 路由规则冻结**——CREATE 用 `project.activeBinding`；UPDATE 用 `work_item.provider_key`
5. **Jira status mapping 动态**——`listStatuses(binding)` 从 Provider 拉（不要 Provider 级静态模板）

## 2. 范围

### 2.1 In Scope

- **`WorkManagementProvider` interface**（`ProviderKey = string`，无硬编码枚举）
- `ProviderRegistry`（provider_key → Provider 实例）
- `BuiltInProvider`（MVP 默认实现）
- `WorkItem` 一张表（含 provider_key / external_ref / external_url / provider_status / provider_meta / search_text）
- `WorkComment` / `WorkRelation`
- `work_item_bindings`（Project × Provider 关联）+ **DB UNIQUE partial index** 强制 active 唯一
- **v0.4.2 改** `work_management_connections` 解耦为 Org/Owner 级（多 Project 复用同一 connection）
- **v0.4.2 新增** connection webhook 状态字段
- 状态映射（canonical_status_category）
- Provider 切换语义：Change Provider（仅影响新 WorkItem）+ Migrate Existing WorkItems（独立 Wizard，V2）
- **v0.4.1 改** 路由规则：CREATE vs UPDATE 各自依据
- P-Work 页面（MVP）

### 2.2 Out of Scope

- 第三方 Provider 实现（E9 Jira / V1+ Linear / GitHub Issues / Azure Boards）
- Sprint / Backlog（V2）
- 自定义字段映射（V2+）
- WorkItem 历史迁移（Migrate Wizard，V2）

## 3. 数据模型（v0.4.1 简化）

```sql
-- WorkItem（v0.4.1 单一表：Built-in 是 source of truth；Jira 时也是本地同步表示）
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
  created_by_type             TEXT NOT NULL,
  created_by_id               UUID NOT NULL,
  -- v0.4.1 改：provider_key = string（去硬编码）
  provider_key                TEXT NOT NULL DEFAULT 'builtin',
  external_ref                TEXT,            -- provider=non-builtin 时填
  external_url                TEXT,
  -- v0.4.1 改：provider 状态字段直接放 work_items（不另开 projections 表）
  provider_status             TEXT,            -- 原始 status 字面值（如 Jira 'In QA'）
  provider_updated_at         TIMESTAMPTZ,     -- 上次 provider 同步时间
  provider_meta               JSONB,
  -- v0.4.1 改：search_text 直接在 work_items 上
  search_text                 tsvector,
  created_at                  TIMESTAMPTZ DEFAULT now(),
  updated_at                  TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_work_items_project_status ON work_items(project_id, status);
CREATE INDEX idx_work_items_project_type ON work_items(project_id, type);
CREATE INDEX idx_work_items_assignee ON work_items(assignee_type, assignee_id) WHERE assignee_id IS NOT NULL;
CREATE INDEX idx_work_items_due ON work_items(due_at) WHERE due_at IS NOT NULL;
CREATE INDEX idx_work_items_search ON work_items USING gin(search_text);
CREATE UNIQUE INDEX uq_work_items_provider_external ON work_items(provider_key, external_ref) WHERE external_ref IS NOT NULL;

-- work_item_bindings：Project × Provider 关联（v0.4.1 改：active 唯一）
CREATE TABLE work_item_bindings (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  provider_key        TEXT NOT NULL,             -- 'builtin' | 'jira' | 'linear' | ...（由 ProviderRegistry 决定）
  connection_id       UUID,                      -- work_management_connections.id
  external_project_ref TEXT,                     -- Jira project key 等
  settings            JSONB,                     -- status mapping 配置等
  is_active           BOOLEAN NOT NULL DEFAULT true,
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now(),
  UNIQUE (project_id, provider_key)
);
-- v0.4.1 新增：DB 层强制 active 唯一
CREATE UNIQUE INDEX uq_project_active_work_provider
  ON work_item_bindings(project_id)
  WHERE is_active = true;

-- v0.4.2 改：ProviderConnection 从 Project 解耦（Org/Owner 级，多 Project 复用同一个 Jira Site）
-- 之前：work_management_connections 绑定 project_id + user_id
-- 之后：ProviderConnection 是 org/owner 级；work_item_bindings 引用 connection_id
CREATE TABLE work_management_connections (
  id                      UUID PRIMARY KEY,
  provider_key            TEXT NOT NULL,                    -- 'jira' | 'linear' | ...
  -- v0.4.2 改：connection 属于 org/owner，不再绑 project
  org_id                  UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
  owner_user_id           UUID NOT NULL REFERENCES users(id),
  -- 一个 org 可以有多个 connection（不同 Jira site / 不同 user 授权）
  display_label           TEXT,                             -- 'Atlassian Production' | 'Acme Jira Sandbox'
  access_token_encrypted  BYTEA,
  refresh_token_encrypted BYTEA,
  expires_at              TIMESTAMPTZ,
  meta                    JSONB,                            -- site URL、scopes 等
  -- v0.4.2 新增：webhook 状态（E9 用）
  webhook_id              TEXT,
  webhook_expires_at      TIMESTAMPTZ,
  webhook_last_refreshed_at TIMESTAMPTZ,
  created_at              TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_wmc_org ON work_management_connections(org_id);
CREATE INDEX idx_wmc_owner ON work_management_connections(owner_user_id);
CREATE INDEX idx_wmc_provider ON work_management_connections(provider_key);

-- work_item_bindings：Project × Connection 关联（v0.4.2 改：引用 connection_id 而非 project 限定 token）
CREATE TABLE work_item_bindings (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  -- v0.4.2 改：connection_id 可空（BuiltInProvider 不需要 connection）
  -- v0.4.2 改：provider_key 保留是为了快速索引；connection 才是真正的 token 持有者
  provider_key        TEXT NOT NULL,
  connection_id       UUID REFERENCES work_management_connections(id),
  external_project_ref TEXT,                     -- Jira project key（如 'PROJ'）
  settings            JSONB,                     -- status mapping 等
  is_active           BOOLEAN NOT NULL DEFAULT true,
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now(),
  UNIQUE (project_id, provider_key)
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

### 3.1 v0.4.1 不变量（DB 强制）

| 不变量 | 实现 |
| --- | --- |
| Project 同 active provider 唯一 | `UNIQUE INDEX uq_project_active_work_provider ON work_item_bindings(project_id) WHERE is_active = true` |
| (provider_key, external_ref) 唯一 | `UNIQUE INDEX uq_work_items_provider_external ON work_items(provider_key, external_ref) WHERE external_ref IS NOT NULL` |
| (from, to, relation_type) 唯一 | `work_relations` UNIQUE |
| from ≠ to | `work_relations` CHECK |

## 4. Provider 抽象（v0.4.1 改：去硬编码）

```ts
// packages/contracts（v0.4.1 改）
type ProviderKey = string;   // v0.4.1：去硬编码；'builtin' / 'jira' / 'linear' / 任何未来 key

interface WorkManagementProvider {
  readonly key: ProviderKey;
  getCapabilities(): ProviderCapabilities;
  getSelfMetadata(): Promise<ProviderMetadata>;
  // v0.4.1 改：listStatuses 接受 binding（不是全局模板）
  listStatuses(binding: WorkItemBinding): Promise<ProviderStatus[]>;
  listWorkItems(query: WorkItemQuery, binding: WorkItemBinding): Promise<WorkItemPage>;
  getWorkItem(ref: WorkItemRef, binding: WorkItemBinding): Promise<WorkItem>;
  createWorkItem(input: WorkItemInput, binding: WorkItemBinding): Promise<WorkItem>;
  updateWorkItem(ref: WorkItemRef, changes: WorkItemChanges, binding: WorkItemBinding): Promise<WorkItem>;
  addComment(ref: WorkItemRef, comment: WorkCommentInput, binding: WorkItemBinding): Promise<WorkComment>;
  listComments(ref: WorkItemRef, binding: WorkItemBinding): Promise<WorkComment[]>;
  getStatusMapping(binding: WorkItemBinding): Promise<CanonicalStatusMapping[]>;
}

type WorkItemRef =
  | { providerKey: 'builtin', localId: UUID }
  | { providerKey: ProviderKey, externalRef: string };

interface ProviderRegistry {
  register(provider: WorkManagementProvider): void;
  get(key: ProviderKey): WorkManagementProvider | undefined;
  list(): ProviderMetadata[];
}
```

**ProviderRegistry 由 bootstrap 注入**——Built-in 在 MVP 启动时 register('builtin')；E9 Jira 在绑连接时 register('jira')。**加 YouTrack / Azure Boards 时不改 Work Management Core 任何文件**。

## 5. 关键流程

### 5.1 Provider 路由规则（v0.4.1 冻结）

```
CREATE WorkItem:
  provider = providerRegistry.get(activeBinding.provider_key)
  → 必须有 active binding；否则 422 "no active Work Management Provider"

UPDATE WorkItem (status / title / assignee / etc.):
  provider = providerRegistry.get(work_item.provider_key)
  → 永远用 work_item.provider_key，不看 active binding
  → 切到 Jira 后旧 Built-in WorkItem 仍走 BuiltInProvider
```

### 5.2 Change Provider（v0.4.1 改）

```
POST /projects/:id/work-management/bindings { provider_key: 'jira' }
  1. 旧 active binding (builtin) is_active=false
  2. 新 active binding (jira) is_active=true
  3. DB UNIQUE uq_project_active_work_provider 阻止两步内并发
  4. 写 audit
  5. 新 WorkItem 走 jira provider；旧 WorkItem 仍走 builtin

Migrate Wizard（V2）：
  - 列出 provider_key=builtin 的所有 WorkItem
  - 用户选择：保留 / 导出 CSV / 推送到新 Provider
  - 推到新 Provider：write_items（带 external_ref 关联） + work_item_bindings 切换 + 旧 binding inactive
```

### 5.3 Jira Status Mapping（v0.4.1 改：动态拉）

```
初始化 binding 时：
  1. OAuth + GET /rest/api/3/project/{key}/statuses
  2. 写入 binding.settings.status_options
  3. P11 UI 渲染动态选择：
     OPEN / IN_PROGRESS / IN_REVIEW / DONE / CLOSED
     ↓ 映射候选
     Jira 实际 status (按 Issue Type 列出)
  4. 用户选好映射后写 binding.settings.status_mapping
```

**不要在 JiraProvider 类里写死 TODO → ['To Do', 'Open'] 静态模板**。Atlassian 官方说状态按项目 + Issue Type 变（[Atlassian 开发者中心](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-projects/)）。

## 6. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET / POST | `/projects/:id/work-items` | 列表 / 创建 | project member |
| GET / PATCH | `/work-items/:id` | 详情 / 修改 | creator / assignee / project owner |
| POST | `/work-items/:id/transition` | 状态机迁移 | 视目标状态 |
| GET / POST | `/work-items/:id/comments` | 评论 | project member |
| POST | `/work-items/:id/relations` | 创建关联 | project member |
| GET | `/work-items/search?q=...` | 搜索（在 work_items.search_text 上） | project member |
| GET | `/projects/:id/work-management/bindings` | 列出 binding | project owner |
| POST / PATCH | `/projects/:id/work-management/bindings` | 切换 Provider | project owner |
| GET | `/work-management/providers` | 列出已注册 Provider | project member |

## 7. 验收标准

### 7.1 功能

- **F1** WorkItem CRUD 全通
- **F2** `work_item_bindings` 同 project 同 active **DB UNIQUE 唯一**
- **F3** Project 默认 binding = builtin
- **F4** **v0.4.1 改** 业务代码不写 `if (provider === 'jira') ...`（lint + code review 守门）
- **F5** **v0.4.1 改** ProviderKey = string（注册到 ProviderRegistry，无硬编码）
- **F6** **v0.4.1 改** 路由规则：CREATE → active binding；UPDATE → work_item.provider_key
- **F7** **v0.4.1 改** Change Provider 不影响历史 WorkItem（独立路由）
- **F8** canonical_status_category 自动从 status 派生
- **F9** WorkItem 关联 Execution：execution.work_item_ref optional 引用
- **F10** **v0.4.1 改** `search_text` 在 work_items 上，搜索性能 P95 < 500ms

### 7.2 E2E

- `e2e/E8-001-workitem-lifecycle`
- `e2e/E8-002-default-binding-builtin`
- `e2e/E8-003-change-provider-no-migration`（v0.4.1 改：旧 WorkItem 仍走 builtin）
- `e2e/E8-004-update-routing-uses-workitem-provider`（v0.4.1 新）—— 切到 Jira 后改旧 Built-in WorkItem → 仍调 BuiltInProvider
- `e2e/E8-005-active-binding-unique`（v0.4.1 新）—— 并发切 Provider 失败
- `e2e/E8-006-provider-registry-open-extension`（v0.4.1 新）—— 注册 YouTrackProvider 不改 Core

### 7.3 非功能

- WorkItem 列表 P95 < 200ms
- search P95 < 500ms（10k WorkItem）

## 8. 与其他 Epic 的关系

- **被依赖**：E9（Jira Provider 适配）/ E4（WorkItem 状态变化 → Trigger，V1+）/ E7（execution.work_item_ref optional）
- **依赖**：E1（Project + Member）/ E6（assignee 操作权限）

## 9. 风险与开放问题

- **R1**：Migrate Wizard 何时做？— V1 简化为"Change Provider 按钮 + 文字说明"；Migrate 留 V2
- **R2**：Jira Status Mapping 动态拉的实时性（每次 binding 变更重拉？）→ V1 在 binding 创建时拉 + 缓存
- **R3**：WorkItem assignee 是 Agent 时 Agent lifecycle=PAUSED → 列表显示 lifecycle 徽标，状态点同步

## 10. 实施顺序（M6）

1. **v0.4.1 改** ProviderKey = string + ProviderRegistry 模式
2. **v0.4.1 改** work_items 加 provider_status / provider_updated_at / provider_meta / search_text
3. **v0.4.1 改** work_item_bindings 加 DB UNIQUE partial index
4. **v0.4.1 改** 路由规则：CREATE vs UPDATE 分离
5. BuiltInProvider 实现
6. work_item_bindings 默认初始化
7. P-Work 页面
8. P4 / P11 Settings 集成
9. E2E 套件
