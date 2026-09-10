# E9 · Work Management Integrations

| 字段 | 值 |
| --- | --- |
| Epic ID | E9 |
| 标题 | Work Management Integrations |
| 阶段 | V1+（PRD v0.4 §10.2 / SD v0.9 §9.5 Jira Provider） |
| 上游 | E8（v0.9 WorkItem 域 + Provider 抽象 + Org 级 Connection） |
| 下游 | 无（E9 是叶子） |
| 状态 | Draft（v0.9 同步） |

## 1. 背景与动机

v0.4.2 E8 修订后，E9 同步收紧：
- `JiraProvider` 接口实现要带 `binding` 参数（不是全局）
- Connection 已是 Org/Owner 级，E9 不再绑 Project
- 删除旧的 `work_item_projections` 残留描述
- 删除 `JiraProvider.getSelfMetadata()` 静态状态模板
- 新增 webhook 注册 + 定期 refresh（官方 API）

## 2. 范围

### 2.1 In Scope

- `JiraProvider` 实现（v0.4.2：所有方法都接 `binding` 参数）
- OAuth 3LO 流程
- Connection 与 Project binding 分离（Connection 在 Org 级；binding 引用 connection）
- MateOS WorkItem ↔ Jira Issue 双向同步
- 评论 / 状态变更双向同步
- **v0.4.2 改** Jira status mapping 动态拉（`listStatuses(binding)` 调 Atlassian `GET /rest/api/3/project/{key}/statuses`）
- Webhook 注册 + 定期 refresh（官方 `PUT /rest/api/3/webhook/refresh`）
- Sync 失败重试 + 冲突检测
- Provider 自描述（`getSelfMetadata`）用于 P4 / P11 动态表单渲染——**只返回 capability 描述，不返回 status mapping**

### 2.2 Out of Scope

- Linear / GitHub Issues / Azure Boards（V2+）
- 自定义字段映射（V2+）
- 自托管 Jira（Data Center）（V2+）

## 3. 数据模型

E8 已有：
- `work_items`（v0.4.2 简化单表）
- `work_item_bindings`（Project × Provider + connection_id）
- `work_management_connections`（v0.4.2 Org/Owner 级，含 webhook 字段）

E9 复用 E8 表，无需新增。

## 4. JiraProvider 实现（v0.4.2 改）

```ts
class JiraProvider implements WorkManagementProvider {
  readonly key = 'jira';

  getCapabilities(): ProviderCapabilities {
    return {
      supportsComments: true,
      supportsRelations: true,
      supportsCustomFields: false,   // V2
      supportsWebhooks: true
    };
  }

  getSelfMetadata(): ProviderMetadata {
    // v0.4.2 改：只返回 capability，不返回静态 status 模板
    return {
      key: 'jira',
      display_name: 'Jira',
      auth_type: 'oauth3lo',
      required_fields: [
        { key: 'site', label: 'Jira Site URL', type: 'url' },
        { key: 'project_key', label: 'Project Key', type: 'string' }
      ]
    };
  }

  // v0.4.2 改：所有方法都接 binding（E8 已经定好）
  async listStatuses(binding: WorkItemBinding): Promise<ProviderStatus[]> {
    // GET /rest/api/3/project/{key}/statuses
    //   → 返回该 project + Issue Type 实际可用的 status
    //   → Atlassian 官方：状态按 project + Issue Type 变
    return await this.api(binding).listStatuses(binding.external_project_ref);
  }

  async listWorkItems(query: WorkItemQuery, binding: WorkItemBinding): Promise<WorkItemPage> {
    return await this.api(binding).listIssues({
      jql: `project = "${binding.external_project_ref}" AND ${queryToJql(query)}`
    });
  }

  async createWorkItem(input: WorkItemInput, binding: WorkItemBinding): Promise<WorkItem> {
    const issue = await this.api(binding).createIssue({
      fields: {
        project: { key: binding.external_project_ref },
        summary: input.title,
        description: input.description,
        issuetype: { name: input.type || 'Task' }
      }
    });
    return this.toWorkItem(issue, binding);
  }

  async updateWorkItem(ref: WorkItemRef, changes: WorkItemChanges, binding: WorkItemBinding): Promise<WorkItem> {
    // 必须用 ref.externalRef，不用 binding（按 E8 路由规则）
    await this.api(binding).editIssue(ref.externalRef, changes);
    // 写 work_items.provider_status / provider_updated_at
  }

  async addComment(ref, comment, binding): Promise<WorkComment> { /* ... */ }
  async listComments(ref, binding): Promise<WorkComment[]> { /* ... */ }

  async getStatusMapping(binding: WorkItemBinding): Promise<CanonicalStatusMapping[]> {
    // v0.4.2 改：动态返回（来自 binding.settings.status_mapping，用户配置）
    return binding.settings.status_mapping;
  }
}
```

### 4.1 Provider routing invariant（v0.4.2 与 E8 同步）

```ts
class ProviderRouter {
  forCreate(project: Project): WorkManagementProvider {
    const binding = workItemBindings.getActive(project.id);
    if (!binding) throw new NoActiveProvider();
    return providerRegistry.get(binding.provider_key);
  }

  forExisting(workItem: WorkItem): WorkManagementProvider {
    // 永远用 workItem.provider_key，不看 active binding
    return providerRegistry.get(workItem.provider_key);
  }
}
```

## 5. Webhook 生命周期（v0.4.2 新增）

### 5.1 注册

```
OAuth 3LO 完成后：
  POST {jira_base}/rest/api/3/webhook
  {
    url: "https://api.mateos.com/sync/jira/webhook",
    webhooks: [{
      events: ["jira:issue_created","jira:issue_updated","jira:issue_deleted","comment_created","comment_updated"],
      jqlFilter: "project = 'PROJ'",
      fieldIdsFilter: ["status","assignee","summary","description"]
    }]
  }
  → response.webhooks[0].id = wh-12345
  → 写 work_management_connections.webhook_id = 'wh-12345'
  → 写 work_management_connections.webhook_expires_at = now() + 30 days
```

### 5.2 定期 refresh（关键：Jira Cloud webhook 30 天过期）

```
Atlassian 官方：PUT /rest/api/3/webhook/refresh
  Headers: { "X-Atlassian-Webhook-Identifier": "wh-12345" }
  Body: { "webhookIds": [wh-12345] }
  → 续期成功 → 写 work_management_connections.webhook_last_refreshed_at = now()
```

```
Scheduler（V1+ E9 落地）：
  每日 0:00 UTC 扫所有 connection
    if webhook_expires_at < now() + 7 days:
      PUT /webhook/refresh
      → success: webhook_expires_at = now() + 30 days
      → failed: 写 alert + 通知 owner
```

### 5.3 Inbound 接收

```
POST /sync/jira/webhook
  Headers: { X-Hub-Signature, X-Atlassian-Webhook-Identifier }
  → 用 `matchedWebhookIds[0]` 定位本地注册记录（**不是** `X-Atlassian-Webhook-Identifier`，后者只标单次投递）
  → 校验 `Authorization: Bearer` 的 **JWT 签名**（HS256，key = app client secret）
  → `INSERT webhook_inbox(...) ON CONFLICT (provider_key, delivery_id) DO NOTHING` —— 去重与持久化同一事务
  → Worker 拉 Issue 最新状态
  → 比对 payload_hash → 相同跳过（防循环）
  → 写 work_items 更新（用 workItem.provider_key 路由）+ sync_audit
  → WS 推 work_item.updated
```

## 6. 关键流程

### 6.1 OAuth 连接

```
1. POST /projects/:id/work-management/jira/connect
   → 重定向到 Atlassian authorize URL
   → state = sign(org_id + user_id + nonce)
2. 用户授权 → 回调 /work-management/jira/callback
   → 校验 state
   → 拿 access_token + refresh_token
   → 加密存 work_management_connections（org_id 级别，不绑 project）
3. 用户在 P11 Project Settings 选择哪个 connection 绑到当前 project
4. POST /projects/:id/work-management/bindings { connection_id, external_project_ref: 'PROJ' }
   → 创建 work_item_bindings
   → 触发 getSelfMetadata + listStatuses
   → 写 binding.settings
5. 注册 webhook（§5.1）
```

### 6.2 双向同步

**MateOS → Jira**：
```
MateOS createWorkItem:
  1. POST Jira /rest/api/3/issue
  2. 写 work_items (provider_key='jira', external_ref='PROJ-123')
  3. 写 work_item_projections（v0.4.2 删了）—— 实际只更新 work_items 字段
  4. 写 sync_audit(direction=OUT, status=OK, payload_hash=sha256(content))
```

**Jira → MateOS（webhook）**：
```
POST /sync/jira/webhook
  → 校验 Bearer JWT 签名 + 用 `matchedWebhookIds` 定位订阅
  → `INSERT webhook_inbox` 落库（去重 + 持久化原子，v0.5）
  → Worker 拉 Issue 最新状态
  → 比对 payload_hash → 相同跳过
  → 通过 ProviderRouter.forExisting(workItem) 找 Provider
  → 写 work_items 更新
  → sync_audit(direction=IN, status=OK)
  → WS 推 work_item.updated
```

### 6.3 冲突检测

```
双向同时更新：
  - 维护 last_modified_at
  - webhook 收到时检查 last_modified_at：本地更新晚则拒
  - 标 sync_audit.status=CONFLICT + 通知 project owner
```

## 7. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| POST | `/orgs/:id/work-management/connections` | 创建 Provider Connection（v0.4.2 改：org 级） | org owner |
| GET | `/orgs/:id/work-management/connections` | 列出 | org member |
| DELETE | `/orgs/:id/work-management/connections/:cid` | 断开 | org owner |
| GET | `/work-management/jira/callback` | OAuth 回调 | 公开（带 state） |
| POST | `/projects/:id/work-management/bindings` | 创建 binding（引用 connection_id） | project owner |
| GET / PATCH | `/projects/:id/work-management/bindings` | 切换 Provider | project owner |
| POST | `/sync/jira/webhook` | Jira webhook 入口 | 公开（带 signature） |
| POST | `/sync/retry` | 重试失败的同步 | system |

## 8. 验收标准

### 8.1 功能

- **F1** OAuth 3LO 全流程跑通
- **F2** **v0.4.2 改** Connection 属于 Org/Owner，不绑 Project
- **F3** 一个 Org 多个 Jira Connection（不同 site / 不同 user）
- **F4** **v0.4.2 改** `JiraProvider` 所有方法都接 `binding` 参数
- **F5** **v0.4.2 改** `listStatuses(binding)` 动态拉（不依赖静态模板）
- **F6** **v0.4.2 改** Webhook 注册 + 定期 refresh（防 30 天过期）
- **F7** payload_hash 去重（防循环同步）
- **F8** 冲突检测：标 CONFLICT + 通知
- **F9** Change Provider 不影响历史 WorkItem
- **F10** 失败重试：指数退避

### 8.2 E2E

- `e2e/E9-001-jira-oauth`
- `e2e/E9-002-jira-workitem-create`
- `e2e/E9-003-jira-webhook-inbound`
- `e2e/E9-004-jira-comment-bidir`
- `e2e/E9-005-payload-hash-dedup`
- `e2e/E9-006-conflict-detection`
- `e2e/E9-007-change-provider-no-migration`
- `e2e/E9-008-retry-after-failure`
- `e2e/E9-009-connection-org-scope`（v0.4.2 新）—— Connection 跨 Project 复用
- `e2e/E9-010-webhook-refresh`（v0.4.2 新）—— 模拟 webhook 过期，scheduled refresh
- `e2e/E9-011-liststatuses-dynamic`（v0.4.2 新）—— status mapping 从 Jira 拉，非静态

## 9. 与其他 Epic 的关系

- **被依赖**：无（E9 是叶子）
- **依赖**：E8（v0.4.2 Provider 抽象 + WorkItem + Org 级 Connection）
- **冲突裁决**：Jira webhook 只同步 WorkItem/Comment，不触达 Agent / Execution

## 10. 风险与开放问题

- **R1**：OAuth refresh token 自动刷新
- **R2**：webhook 重放保护（signature + timestamp window）
- **R3**：OAuth 撤销（用户从 Atlassian 取消授权）—— webhook 失败时 fallback
- **R4**：自托管 Jira（Data Center）vs Cloud API 差异——V1+ 仅 Cloud

## 11. 实施顺序（V1+，独立里程碑）

1. **v0.4.2 改** Connection 实体从 Project 改为 Org/Owner
2. **v0.4.2 改** `JiraProvider` 实现接 `binding` 参数
3. **v0.4.2 改** `listStatuses(binding)` 动态拉
4. OAuth 3LO + token 加密
5. **v0.4.2 新增** webhook 注册 + 定期 refresh scheduler
6. MateOS WorkItem → Jira Issue 单向创建
7. Jira webhook inbound + 反向同步
8. 评论双向同步 + 冲突检测
9. P11 Work Management Settings UI（v0.4.2 改：选 Connection 而非建 Project 内 connection）
10. E2E 套件
