# Detailed Design · 06 · WorkItem ↔ Provider Sync

> E8 Work Management Core + E9 Jira Provider + BuiltIn Provider 双向同步。
> 前置：[00-overview.md](./00-overview.md) / E8 / E9 epic docs

## 0. 范围

- BuiltIn Provider（V1 源 of truth = `work_items` 表）
- Jira Provider（V1+）
- Status 映射（动态拉，不写静态模板）
- 双向同步 + 冲突检测
- Webhook 生命周期（注册 + 30 天 refresh）
- Provider 路由（CREATE vs UPDATE）
- Org 级 Connection 复用

## 1. Provider 抽象

### 1.1 接口

```ts
// packages/contracts
type ProviderKey = string  // v0.4.2 去硬编码

interface WorkManagementProvider {
  readonly key: ProviderKey;
  getCapabilities(): ProviderCapabilities;
  getSelfMetadata(): Promise<ProviderMetadata>;
  listStatuses(binding: WorkItemBinding): Promise<ProviderStatus[]>;  // 动态
  listWorkItems(query: WorkItemQuery, binding: WorkItemBinding): Promise<WorkItemPage>;
  getWorkItem(ref: WorkItemRef, binding: WorkItemBinding): Promise<WorkItem>;
  createWorkItem(input: WorkItemInput, binding: WorkItemBinding): Promise<WorkItem>;
  updateWorkItem(ref: WorkItemRef, changes: WorkItemChanges, binding: WorkItemBinding): Promise<WorkItem>;
  addComment(ref: WorkItemRef, comment: WorkCommentInput, binding: WorkItemBinding): Promise<WorkComment>;
  listComments(ref: WorkItemRef, binding: WorkItemBinding): Promise<WorkComment[]>;
  getStatusMapping(binding: WorkItemBinding): Promise<CanonicalStatusMapping[]>;
}

interface WorkItemBinding {
  project_id: UUID;
  provider_key: string;
  connection_id?: UUID;
  external_project_ref?: string;  // Jira project key
  settings: Record<string, any>;   // 包括 status_mapping
}

type WorkItemRef =
  | { providerKey: 'builtin' | string, localId: UUID, externalRef?: undefined }  // BuiltIn
  | { providerKey: string, localId?: undefined, externalRef: string }  // Jira 等
```

### 1.2 ProviderRegistry

```ts
class ProviderRegistry {
  private providers: Map<string, WorkManagementProvider> = new Map();

  register(provider: WorkManagementProvider): void {
    this.providers.set(provider.key, provider);
  }

  get(key: ProviderKey): WorkManagementProvider {
    const p = this.providers.get(key);
    if (!p) throw new NoProviderError(key);
    return p;
  }

  list(): ProviderMetadata[] {
    return [...this.providers.values()].map(p => p.getSelfMetadata());
  }
}
```

**加新 Provider（如 Linear）零改动 Work Management Core**：

```ts
// bootstrap.ts
providerRegistry.register(new LinearProvider());
providerRegistry.register(new BuiltInProvider());
providerRegistry.register(new JiraProvider());
```

## 2. Provider 路由（v0.4.2 冻结）

### 2.1 CREATE 路由

```
POST /projects/:id/work-items
  → project_id 推 active binding (work_item_bindings WHERE is_active=true)
  → binding.provider_key 查 Provider
  → provider.createWorkItem(input, binding)
```

**关键**：CREATE **永远**走 project 的 active binding。

### 2.2 UPDATE 路由

```
PATCH /work-items/:id
  → work_item.provider_key 查 Provider（不看 active binding）
  → provider.updateWorkItem(ref, changes, binding)  // binding 仍传（status_mapping 等）
```

**关键**：切到 Jira 后改旧 Built-in WorkItem → 仍调 BuiltInProvider.updateWorkItem。

### 2.3 路由不变量

```
CREATE WorkItem:
  binding = work_item_bindings.getActive(project_id)  -- 必须有
  provider = providerRegistry.get(binding.provider_key)

UPDATE WorkItem (status / title / assignee / etc.):
  provider = providerRegistry.get(work_item.provider_key)
```

## 3. BuiltIn Provider

### 3.1 Source of Truth

`work_items` 单表。**BuiltIn 时 work_item = source of truth + local representation**。

### 3.2 操作

```python
class BuiltInProvider(WorkManagementProvider):
    key = 'builtin'

    async createWorkItem(input, binding):
        # 1. INSERT work_items
        # 2. 触发 indexing worker
        # 3. WS 推 work_item.created

    async updateWorkItem(ref, changes, binding):
        # 1. UPDATE work_items SET ...
        # 2. WS 推 work_item.updated
```

### 3.3 Status Mapping（BuiltIn 时一对一）

| BuiltIn status | canonical_status_category |
| --- | --- |
| OPEN | TODO |
| IN_PROGRESS | IN_PROGRESS |
| IN_REVIEW | IN_PROGRESS |
| DONE | DONE |
| CLOSED | DONE |

`canonical_status_category` 自动派生（V1 简化为应用层 trigger）。

## 4. Jira Provider

### 4.1 Source of Truth

Jira Cloud。MateOS 维护 `work_items` 表（provider_key='jira', external_ref='PROJ-123'）。

### 4.2 OAuth 流程

```
1. POST /orgs/:id/work-management/connections { provider: 'jira' }
   → 重定向到 Atlassian authorize URL
   → state = sign(org_id + user_id + nonce)

2. 用户授权 → 回调 /work-management/jira/callback?code=...
   → 校验 state
   → POST Atlassian /oauth/token → access_token + refresh_token
   → AES-256-GCM 加密存 work_management_connections
   → 注册 webhook（详见 §6）
   → WS 推 connection_created
```

### 4.3 状态映射（v0.4.2 改：动态拉）

```
1. 创建 binding 时:
   a) GET /rest/api/3/project/{key}/statuses
   b) 返回实际 status 列表（按 Issue Type 变）
   c) 写 binding.settings.status_options
   d) P11 UI 渲染：用户选映射
   e) 写 binding.settings.status_mapping: [
        { canonical: 'TODO', jira_values: ['To Do', 'Open'] },
        { canonical: 'IN_PROGRESS', jira_values: ['In Progress', 'In Review'] },
        { canonical: 'DONE', jira_values: ['Done', 'Closed', 'Resolved'] }
      ]
```

**绝不在 JiraProvider 类里写死 TODO → ['To Do', 'Open']**。

### 4.4 createWorkItem

```python
async createWorkItem(input, binding):
    # 1. POST /rest/api/3/issue
    #    { fields: { project: {key: binding.external_project_ref},
    #                summary: input.title,
    #                description: input.description (ADF format),
    #                issuetype: {name: 'Task'} } }
    # 2. 拿 issue.key ('PROJ-123')
    # 3. INSERT work_items (provider_key='jira', external_ref='PROJ-123', external_url=...)
    # 4. 写 work_management_connections.webhook_id 关联
    # 5. sync_audit (direction=OUT, status=OK, payload_hash=sha256(input))
```

### 4.5 updateWorkItem

```python
async updateWorkItem(ref, changes, binding):
    # 1. PUT /rest/api/3/issue/{ref.externalRef}
    # 2. UPDATE work_items SET provider_updated_at=now(), provider_status=...
```

**关键**：`ref.externalRef` 来自 work_item.external_ref，不来自 active binding（详见 §2.2）。

## 5. 双向同步

### 5.1 MateOS → Jira（已通过 Provider createWorkItem / updateWorkItem）

每次写 work_items 后：
1. 写 sync_audit (direction=OUT, status=OK, payload_hash=sha256(content))
2. WS 推 work_item.created / work_item.updated

### 5.2 Jira → MateOS（webhook）

```
1. POST /sync/jira/webhook
   Headers:
     X-Atlassian-Webhook-Identifier: <webhook_id>
     X-Hub-Signature: <HMAC>
   Body: Atlassian webhook event

2. E9 Webhook Receiver:
   a) 校验 webhook_id == work_management_connections.webhook_id
   b) 校验 HMAC signature
   c) 入 BullMQ sync.jira.inbound

3. Worker (sync.jira.inbound):
   a) 拉取 Issue 最新状态: GET /rest/api/3/issue/{key}
   b) 算 payload_hash = sha256(issue content)
   c) 比对 sync_audit 最近同 external_ref + direction=IN 的 payload_hash
      - 相同 → 跳过（防循环）
      - 不同 → 继续
   d) UPDATE work_items SET status=?, provider_status=?, provider_updated_at=now()
   e) sync_audit (direction=IN, status=OK, payload_hash=...)
   f) WS 推 work_item.updated
```

## 6. Webhook 生命周期

### 6.1 注册

```
1. POST {jira_base}/rest/api/3/webhook
   Body:
   {
     "url": "https://api.mateos.com/sync/jira/webhook",
     "webhooks": [{
       "events": [
         "jira:issue_created",
         "jira:issue_updated",
         "jira:issue_deleted",
         "comment_created",
         "comment_updated",
         "comment_deleted"
       ],
       "jqlFilter": "project = 'PROJ'",
       "fieldIdsFilter": ["status", "assignee", "summary", "description", "priority"]
     }]
   }

2. 拿 response.webhooks[0].id = "wh-12345"
3. UPDATE work_management_connections
   SET webhook_id='wh-12345',
       webhook_expires_at = NOW() + INTERVAL '30 days'
4. sync_audit
```

### 6.2 定期 refresh

**关键**：Jira Cloud 动态 webhook **30 天过期**（官方文档）。必须 scheduler 续期。

```python
# 每日 0:00 UTC 跑
async def refresh_expiring_webhooks():
    conns = await db.query("""
        SELECT id, provider_key, org_id, webhook_id, webhook_expires_at
        FROM work_management_connections
        WHERE webhook_id IS NOT NULL
          AND webhook_expires_at < NOW() + INTERVAL '7 days'
    """)
    for conn in conns:
        try:
            # PUT /rest/api/3/webhook/refresh
            await jira_api.refresh_webhook(conn.webhook_id)
            await db.update(conn.id, webhook_expires_at=NOW() + INTERVAL '30 days',
                            webhook_last_refreshed_at=NOW())
        except Exception as e:
            await notify_owner(conn.org_id, f'Jira webhook {conn.webhook_id} refresh failed: {e}')
            await alert(f'webhook_refresh_failed: connection={conn.id}')
```

### 6.3 Webhook 接收

```python
@app.post('/sync/jira/webhook')
async def jira_webhook(request):
    # 1. 校验 HMAC signature
    body = await request.body()
    signature = request.headers.get('X-Hub-Signature', '')
    webhook_id = request.headers.get('X-Atlassian-Webhook-Identifier')

    if not hmac.verify(body, signature, WEBHOOK_SECRET):
        return 401

    # 2. 校验 webhook_id 存在
    conn = await db.get_connection_by_webhook_id(webhook_id)
    if not conn:
        return 404

    # 3. 入 BullMQ
    await bullmq.enqueue('sync.jira.inbound', {
        'connection_id': conn.id,
        'event': json.loads(body)
    })

    return 202
```

## 7. 冲突检测

### 7.1 时间戳比较

```
work_items 表加 last_modified_at（V2）或依赖 provider_updated_at + updated_at

webhook 收到 Issue 更新时:
  if local.work_item.updated_at > remote.issue.updated:
    → 标 sync_audit.status='CONFLICT'
    → 通知 project owner 仲裁
  else:
    → 接受远端更新
```

**V1 简化**：基于 `provider_updated_at` vs `webhook.issue.updated`。详细比较留 V2。

## 8. 数据模型（v0.4.2 简化）

### 8.1 work_items（单一表）

```sql
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
  -- v0.4.2 改：ProviderKey = string
  provider_key                TEXT NOT NULL DEFAULT 'builtin',
  external_ref                TEXT,
  external_url                TEXT,
  -- v0.4.2 改：provider 状态字段直接放 work_items
  provider_status             TEXT,
  provider_updated_at         TIMESTAMPTZ,
  provider_meta               JSONB,
  -- v0.4.2 改：search_text 在 work_items 上
  search_text                 tsvector,
  created_at                  TIMESTAMPTZ DEFAULT now(),
  updated_at                  TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_work_items_project_status ON work_items(project_id, status);
CREATE INDEX idx_work_items_project_type ON work_items(project_id, type);
CREATE INDEX idx_work_items_search ON work_items USING gin(search_text);
CREATE UNIQUE INDEX uq_work_items_provider_external ON work_items(provider_key, external_ref) WHERE external_ref IS NOT NULL;
```

### 8.2 work_item_bindings（v0.4.2：active 唯一约束）

```sql
CREATE TABLE work_item_bindings (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  provider_key        TEXT NOT NULL,
  connection_id       UUID REFERENCES work_management_connections(id),  -- v0.4.2：可空（BuiltIn 不需要）
  external_project_ref TEXT,
  settings            JSONB,
  is_active           BOOLEAN NOT NULL DEFAULT true,
  UNIQUE (project_id, provider_key)
);
-- v0.4.2 关键：DB 强制 active 唯一
CREATE UNIQUE INDEX uq_project_active_work_provider
  ON work_item_bindings(project_id)
  WHERE is_active = true;
```

### 8.3 work_management_connections（v0.4.2：Org/Owner 级）

```sql
CREATE TABLE work_management_connections (
  id                      UUID PRIMARY KEY,
  provider_key            TEXT NOT NULL,
  org_id                  UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,  -- v0.4.2
  owner_user_id           UUID NOT NULL REFERENCES users(id),
  display_label           TEXT,
  access_token_encrypted  BYTEA,
  refresh_token_encrypted BYTEA,
  expires_at              TIMESTAMPTZ,
  meta                    JSONB,
  -- v0.4.2 新增：webhook 状态
  webhook_id              TEXT,
  webhook_expires_at      TIMESTAMPTZ,
  webhook_last_refreshed_at TIMESTAMPTZ,
  created_at              TIMESTAMPTZ DEFAULT now()
);
```

## 9. 状态机

```
        CREATE
          │
          ▼
        OPEN ─────► CLOSED
          │           ▲
          ▼           │
        IN_PROGRESS ──► DONE
          │           ▲
          ▼           │
        IN_REVIEW ─────┘
          │
          ▼
        CLOSED  (cancelled)
```

`canonical_status_category` 派生：
- OPEN, CLOSED(cancel) = TODO（CLOSED 区分 closed-by-done vs closed-by-cancel 通过 closed_meta 字段，V2）
- IN_PROGRESS, IN_REVIEW = IN_PROGRESS
- DONE, CLOSED(by done) = DONE

V1 简化为 `OPEN = TODO, IN_PROGRESS/IN_REVIEW = IN_PROGRESS, DONE/CLOSED = DONE`。

## 10. E2E 验收点

```
e2e/06-workitem-sync/
  test_001_builtin_crud.json
    Given Built-in binding active
    When POST /work-items
    Then created in work_items, search_text populated

  test_002_provider_routing_create.json
    Given Jira binding active
    When POST /work-items
    Then JiraProvider.createWorkItem called with binding

  test_003_provider_routing_update.json
    Given Built-in work_item, Jira binding active
    When PATCH /work-items/:id
    Then BuiltInProvider.updateWorkItem (NOT Jira)

  test_004_jira_create_sync.json
    When JiraProvider.createWorkItem
    Then POST /issue + INSERT work_items + sync_audit

  test_005_jira_webhook_inbound.json
    When webhook with changed status
    Then UPDATE work_items + sync_audit + WS 推 work_item.updated

  test_006_payload_hash_dedup.json
    When webhook with same payload_hash as last sync
    Then no-op (no UPDATE, no audit)

  test_007_status_mapping_dynamic.json
    When binding created
    Then GET Jira /project/{key}/statuses called (not hardcoded)

  test_008_webhook_refresh.json
    Given webhook_expires_at < now + 7d
    When scheduler runs
    Then PUT /webhook/refresh + expires_at +30d

  test_009_connection_org_scope.json
    Given 2 projects, 1 Jira connection
    When project A creates WorkItem
    Then project B's work item is not affected

  test_010_conflict_detection.json
    Given local updated recently, webhook fires
    When conflict detected
    Then sync_audit.status=CONFLICT, owner notified
```

## 11. 与其他设计的关系

- 详见 [04-resolver-and-routing.md](./04-resolver-and-routing.md)（Resolver 不涉及 WorkItem，但 Execution 可选引用 work_item_ref）
- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（work_item_ref optional 传入 execution）
