# Detailed Design · 06 · WorkItem ↔ Provider Sync

> **v0.4.3 修正**：
> 1. **P1-6**：WorkItem 加 `binding_id` 字段（identity 用 binding 不用 provider_key）
> 2. **P1-7**：Webhook registration 从 Connection 拆出为独立 `work_management_webhooks` 表
> 前置：[00-overview.md](./00-overview.md) / E8 / E9 epic docs

## 0. 范围

- BuiltIn Provider（V1 source of truth = `work_items` 表）
- Jira Provider（V1+）
- Status 映射（动态拉）
- 双向同步 + 冲突检测
- Webhook 生命周期（独立表）
- Provider 路由（v0.4.3 改：CREATE 用 active binding；UPDATE 用 work_item.binding_id）

## 1. Provider 抽象

（同 v0.4.2，详见 E8）

## 2. Provider 路由（v0.4.3 修正 P1-6）

### 2.1 v0.4.2 的问题

```
CREATE: provider = project.active_binding.provider_key
UPDATE: provider = work_item.provider_key

问题：
- provider_key='jira' 不足以知道用哪个 Jira Connection
- 多个 Project 共享 Connection 时，Provider 用哪个 Project Mapping
- 切 Provider 后历史 WorkItem 路由不稳
```

### 2.2 v0.4.3 修复

```
CREATE: provider = project.active_binding
              ↓
       registry.get(binding.provider_key) + binding

UPDATE: provider = work_item.binding
              ↓
       registry.get(binding.provider_key) + binding
```

**关键**：`binding` 是路由的唯一事实源，包含 connection_id + external_project_ref + status mapping settings。

### 2.3 WorkItem 加 binding_id（v0.4.3）

```sql
ALTER TABLE work_items
  ADD COLUMN binding_id UUID NOT NULL REFERENCES work_item_bindings(id);

-- v0.4.3 改：唯一约束以 binding 维度
DROP INDEX IF EXISTS uq_work_items_provider_external;
CREATE UNIQUE INDEX uq_work_items_binding_external
  ON work_items(binding_id, external_ref)
  WHERE external_ref IS NOT NULL;
```

**为什么 binding_id 重要**：
- 同一 provider 跨多个 binding 时（如 Jira Site A 同时绑了 MateOS Project X 和 Project Y），WorkItem identity 不会撞
- 切 Provider 后，旧 WorkItem 仍引用旧 binding（binding 没动），路由稳定
- Connection 升级/降级时，只动 binding，不动历史 WorkItem

## 3. BuiltIn Provider

（同 v0.4.2）

```ts
class BuiltInProvider implements WorkManagementProvider {
  readonly key = 'builtin';

  async createWorkItem(input, binding) {
    const workItem = await db.insert('work_items', {
      ...input,
      binding_id: binding.id,
      provider_key: 'builtin',
      external_ref: null
    });
    return workItem;
  }
}
```

## 4. Jira Provider

### 4.1 OAuth 流程（同 v0.4.2）

### 4.2 状态映射（动态拉）

（同 v0.4.2）

### 4.3 createWorkItem（v0.4.3 改）

```ts
async createWorkItem(input, binding: WorkItemBinding) {
  // 1. POST /rest/api/3/issue
  //    body 必须含 binding.connection_id 找到的 tenant URL
  const tenantUrl = await getTenantUrl(binding.connection_id);
  const issue = await jiraApi(tenantUrl).createIssue({
    fields: {
      project: { key: binding.external_project_ref },
      summary: input.title,
      ...
    }
  });

  // 2. INSERT work_items
  const workItem = await db.insert('work_items', {
    ...input,
    binding_id: binding.id,             // v0.4.3 关键
    provider_key: 'jira',
    external_ref: issue.key,
    external_url: `${tenantUrl}/browse/${issue.key}`
  });
  return workItem;
}
```

### 4.4 updateWorkItem（v0.4.3 改：必须用 work_item.binding）

```ts
async updateWorkItem(ref, changes, binding) {
  // 注意：binding 是 ref 对应的 binding（从 work_item 读出），不是 active binding
  // 即使项目切到 BuiltIn，旧 Jira WorkItem 仍用旧 Jira binding 调用
  
  const workItem = await getWorkItemByRef(ref);
  const realBinding = workItem.binding;  // v0.4.3 关键

  const tenantUrl = await getTenantUrl(realBinding.connection_id);
  await jiraApi(tenantUrl).editIssue(workItem.external_ref, changes);

  await db.update('work_items', workItem.id, {
    ...changes,
    provider_updated_at: NOW(),
    provider_status: ...,
  });
}
```

## 5. 双向同步

### 5.1 MateOS → Jira（同 v0.4.2）

### 5.2 Jira → MateOS（webhook）

```python
# 收 webhook
async def jira_webhook(request):
    body = await request.body()
    event = json.loads(body)

    # v0.4.4 修正（对齐 Atlassian 实际协议）：
    #   X-Atlassian-Webhook-Identifier = **单次投递 ID**（标识一次投递、用于去重重试），
    #   **不是**注册订阅 ID；命中的动态订阅 ID 在 body.matchedWebhookIds[] 里。
    delivery_id = request.headers.get('X-Atlassian-Webhook-Identifier', '')
    matched_ids = event.get('matchedWebhookIds') or []

    # 先按命中的订阅 ID 定位本地注册记录（不是 connection）
    webhook = await db.get_webhook_by_external_id(matched_ids[0]) if matched_ids else None
    if not webhook:
        return 404

    # OAuth 2.0 动态 webhook 用 Bearer 校验：
    # 没有 X-Hub-Signature（那是 GitHub 的协议），也没有 webhook.secret 概念
    token = request.headers.get('Authorization', '').removeprefix('Bearer ').strip()
    if not hmac.compare_digest(token, webhook.auth_token):
        return 401

    # 幂等：Atlassian 会重试投递，同一 delivery_id 只处理一次
    if await has_seen_delivery(delivery_id):
        return 202
    await mark_delivery_seen(delivery_id)

    # 入 BullMQ
    await bullmq.enqueue('sync.jira.inbound', {
        'webhook_id': webhook.id,
        'binding_id': webhook.binding_id,        # v0.4.3 关键
        'delivery_id': delivery_id,
        'event': event
    })
    return 202
```

**v0.4.4 修正说明**：照旧写法实现会**拒绝全部合法回调**——`X-Atlassian-Webhook-Identifier` 每次投递都变，拿它去查本地注册记录必然 404；`X-Hub-Signature` 与 `webhook.secret` 在 Jira OAuth 2.0 webhook 上根本不存在，鉴权分支会 401。注册订阅时 Jira 返回的 `webhookRegistrationResult[]` 需落库保存（订阅 ID + `auth_token`），并保存 `expirationDateTime` 以便到期前刷新。

```python
# worker 处理
async def process_jira_inbound(webhook_id, binding_id, event):
    # v0.4.3 改：binding_id 决定路由（不依赖 connection）
    binding = await get_binding(binding_id)

    # 拉 Issue
    issue = await jiraApi(binding).getIssue(event.issue.key)

    # 算 payload_hash 防循环
    payload_hash = sha256(issue)
    if await has_recent_same_payload_hash(issue.key, payload_hash):
        return  # 跳过

    # 找 work_item
    workItem = await db.query("""
        SELECT * FROM work_items
        WHERE binding_id = $1 AND external_ref = $2
    """, binding_id, issue.key)

    if not workItem:
        return  # 未匹配

    # 更新
    await db.update('work_items', workItem.id, {
        'status': mapStatusFromJira(issue.fields.status.name, binding),
        'provider_status': issue.fields.status.name,
        'provider_updated_at': NOW(),
    })
```

## 6. Webhook 生命周期（v0.4.3 修复 P1-7）

### 6.1 v0.4.2 的问题

```
work_management_connections (org/owner 级)
  webhook_id
  webhook_expires_at
  webhook_last_refreshed_at
```

**问题**：
- Connection 是 tenant 维度（OAuth + tenant identity）
- Webhook 是 subscription 维度（filter by project / issue type）
- 一个 Connection 对应多个 binding → 应该多个 webhook subscription
- 挂在 Connection 上：JQL filter `project = 'PROJ'` 跨 Project 冲突

### 6.2 v0.4.3 修复：独立 work_management_webhooks 表

```sql
CREATE TABLE work_management_webhooks (
  id                    UUID PRIMARY KEY,
  binding_id            UUID NOT NULL REFERENCES work_item_bindings(id) ON DELETE CASCADE,
  -- v0.4.3 改：webhook 挂在 binding 维度
  connection_id         UUID NOT NULL REFERENCES work_management_connections(id),
  external_webhook_id   TEXT NOT NULL,        -- Jira 的 webhook id
  filter_jql            TEXT,                 -- 'project = "PROJ"' 等
  filter_events         JSONB,                -- ["jira:issue_updated", ...]
  expires_at            TIMESTAMPTZ NOT NULL,
  last_refreshed_at     TIMESTAMPTZ,
  refresh_status        TEXT,                 -- 'OK' | 'FAILED' | 'DISABLED'
  last_error            TEXT,
  created_at            TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_webhook_external ON work_management_webhooks(external_webhook_id);
CREATE INDEX idx_webhook_expires ON work_management_webhooks(expires_at)
  WHERE refresh_status = 'OK';
```

### 6.3 Connection / Binding / Webhook 关系

```
work_management_connections (tenant + OAuth)
  │
  ├── webhook #1: project=PROJ_A, filter=...
  │     └── 关联 binding (Project X → Jira PROJ_A)
  ├── webhook #2: project=PROJ_B, filter=...
  │     └── 关联 binding (Project Y → Jira PROJ_B)
  └── webhook #3: 全局所有 issue (无 filter)
        └── 关联多个 binding
```

**关键**：每个 binding 可独立注册 webhook（filter 精确），避免 webhook 跨 binding 误投递。

### 6.4 注册流程（v0.4.3 改：每个 binding 注册）

```python
async def register_webhook_for_binding(binding):
    tenant_url = await getTenantUrl(binding.connection_id)
    jira_token = await getToken(binding.connection_id)

    resp = await jiraApi(tenant_url, jira_token).registerWebhook({
        url: "https://api.mateos.com/sync/jira/webhook",
        webhooks: [{
            events: ['jira:issue_created', 'jira:issue_updated', ...],
            jqlFilter: f'project = "{binding.external_project_ref}"',
            fieldIdsFilter: ['status', 'assignee', ...]
        }]
    })

    webhook_id = resp.webhooks[0].id

    # v0.4.3：webhook 挂在 binding，不在 connection
    await db.insert('work_management_webhooks', {
        binding_id: binding.id,
        connection_id: binding.connection_id,
        external_webhook_id: webhook_id,
        filter_jql: f'project = "{binding.external_project_ref}"',
        filter_events: [...],
        expires_at: NOW() + 30 days,
        refresh_status: 'OK'
    })
```

### 6.5 定期 refresh（v0.4.3 改：按 webhook 维度）

```python
# 每日 0:00 UTC
async def refresh_expiring_webhooks():
    webhooks = await db.query("""
        SELECT * FROM work_management_webhooks
        WHERE refresh_status = 'OK'
          AND expires_at < NOW() + INTERVAL '7 days'
    """)
    for webhook in webhooks:
        try:
            await jiraApi(...).refreshWebhook(webhook.external_webhook_id)
            await db.update('work_management_webhooks', webhook.id, {
                expires_at: NOW() + 30 days,
                last_refreshed_at: NOW()
            })
        except Exception as e:
            await db.update('work_management_webhooks', webhook.id, {
                refresh_status: 'FAILED',
                last_error: str(e)
            })
            await notify(...)
```

## 7. 数据模型（v0.4.3 关键变化）

### 7.1 work_items（v0.4.3 加 binding_id）

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
  -- v0.4.3 改：binding_id 是路由事实源
  binding_id                  UUID NOT NULL REFERENCES work_item_bindings(id),
  provider_key                TEXT NOT NULL DEFAULT 'builtin',  -- 冗余字段，便于查询
  external_ref                TEXT,
  external_url                TEXT,
  provider_status             TEXT,
  provider_updated_at         TIMESTAMPTZ,
  provider_meta               JSONB,
  search_text                 tsvector,
  created_at                  TIMESTAMPTZ DEFAULT now(),
  updated_at                  TIMESTAMPTZ DEFAULT now()
);
-- v0.4.3 改：唯一约束
CREATE UNIQUE INDEX uq_work_items_binding_external
  ON work_items(binding_id, external_ref) WHERE external_ref IS NOT NULL;
```

### 7.2 work_management_webhooks（v0.4.3 新增）

见 §6.2。

### 7.3 work_item_bindings（同 v0.4.2）

### 7.4 work_management_connections（同 v0.4.2，删 webhook 字段）

```sql
-- v0.4.3 改：从 connection 删 webhook_* 字段
-- (webhook 信息搬到 work_management_webhooks 表)
```

## 8. 状态机

（同 v0.4.2）

## 9. E2E 验收点

```
e2e/06-workitem-sync/
  test_001_builtin_crud.json
    Given Built-in binding active
    When POST /work-items
    Then created in work_items with binding_id set, search_text populated

  test_002_provider_routing_create.json
    Given Jira binding active
    When POST /work-items
    Then BuiltInProvider/JiraProvider called with binding

  test_003_provider_routing_update.json                # v0.4.3 改
    Given Built-in work_item, Jira binding active
    When PATCH /work-items/:id
    Then BuiltInProvider.updateWorkItem (NOT Jira)
    And  uses work_item.binding (not active binding)

  test_004_jira_create_sync.json
    When JiraProvider.createWorkItem
    Then POST /issue + INSERT work_items (binding_id set) + sync_audit

  test_005_jira_webhook_inbound.json                   # v0.4.3 改
    When webhook with changed status
    Then UPDATE work_items (via work_management_webhooks → binding → work_item)
    And  sync_audit

  test_006_payload_hash_dedup.json
    When webhook with same payload_hash as last sync
    Then no-op

  test_007_status_mapping_dynamic.json
    When binding created
    Then GET Jira /project/{key}/statuses called (not hardcoded)

  test_008_webhook_per_binding.json                     # v0.4.3 新增
    Given 1 Connection, 2 Bindings (PROJ_A + PROJ_B)
    When register webhook for each binding
    Then 2 webhooks created, each with distinct JQL filter

  test_009_webhook_refresh.json
    Given webhook_expires_at < now + 7d
    When scheduler runs
    Then PUT /webhook/refresh + expires_at +30d

  test_010_connection_org_scope.json
    Given 2 projects, 1 Jira connection
    When project A creates WorkItem
    Then project B's work item is not affected

  test_011_change_binding_no_workitem_loss.json       # v0.4.3 新增
    Given work_item with binding_id=B1
    When project active binding changes to B2
    Then work_item still routed to B1 (not B2)

  test_012_conflict_detection.json
    Given local updated recently, webhook fires
    Then sync_audit.status=CONFLICT
```

## 10. 与其他设计的关系

- 详见 [04-resolver-and-routing.md](./04-resolver-and-routing.md)（Resolver 不涉及 WorkItem）
- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（work_item_ref optional 传入 execution）
