# E9 · Work Management Integrations

| 字段 | 值 |
| --- | --- |
| Epic ID | E9 |
| 标题 | Work Management Integrations |
| 阶段 | V1+（PRD v0.4 §10.2 / SD v0.3 §9.3 Jira Provider） |
| 上游 | E8（WorkItem 域 + Provider 抽象）/ PRD v0.4 / SD v0.3 |
| 下游 | 无（E9 是叶子） |
| 状态 | Draft（V1+ 阶段详细化） |

## 1. 背景与动机

v0.3 的 E9 错误地把"执行后端切换"和"项目管理切换"绑在一起，**永久不切**到任何外部执行后端（PRD v0.4 §8 Non Goals）。v0.4 拆成两个独立 epic：

- **E8 Work Management Core**（v0.4 新增 MVP）：WorkItem 域 + Built-in Provider
- **E9 Work Management Integrations**（V1+）：**只**做 WorkItem 的 Provider 适配，第一个实现是 **Jira**

未来扩展：Linear / GitHub Issues / Azure Boards / YouTrack / Asana —— 都是同样的 Provider 适配器模式。

## 2. 范围

### 2.1 In Scope（V1+）

- `JiraProvider`（实现 E8 `WorkManagementProvider` 接口）
- OAuth 3LO 流程
- MateOS WorkItem ↔ Jira Issue 双向同步
- 评论 / 状态变更双向同步
- Status mapping 配置 UI
- Webhook 接收 + 反向同步
- Sync 失败重试 + 冲突检测
- Provider 自描述（`getSelfMetadata`）用于 P4 / P11 动态表单渲染

### 2.2 Out of Scope

- Linear / GitHub Issues / Azure Boards（V2+ 单独 epic）
- Execution backend 切换（**永久 Non Goal**）
- 自托管 Jira（Data Center）（V2+）
- 自定义字段映射（V2+）

## 3. 数据模型

E8 已有：
- `work_items`（带 `provider_key` / `external_ref` / `external_url`）
- `work_item_projections`（统一客户端缓存）
- `work_item_bindings`（Project × Provider）
- `work_management_connections`（Provider 凭据）

E9 新增：

```sql
-- sync_audit（E8 在 M6 已建，E9 复用）
CREATE TABLE sync_audit (
  id              BIGSERIAL PRIMARY KEY,
  project_id      UUID NOT NULL,
  provider_key    TEXT NOT NULL,                 -- 'jira'
  direction       TEXT NOT NULL CHECK (direction IN ('OUT','IN')),
  entity_type     TEXT NOT NULL,                 -- 'WORK_ITEM' | 'WORK_COMMENT'
  entity_id       UUID NOT NULL,
  action          TEXT NOT NULL,                 -- 'create' | 'update' | 'comment' | 'status_change'
  external_id     TEXT,
  status          TEXT NOT NULL,                 -- 'OK' | 'FAILED' | 'CONFLICT'
  error_message   TEXT,
  payload_hash    TEXT,                          -- v0.4 新增：防重复同步
  created_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_sync_audit_project_time ON sync_audit(project_id, created_at DESC);
CREATE INDEX idx_sync_audit_entity ON sync_audit(entity_type, entity_id);
```

## 4. JiraProvider 实现

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
    return {
      key: 'jira',
      display_name: 'Jira',
      auth_type: 'oauth3lo',
      required_fields: [
        { key: 'site', label: 'Jira Site URL', type: 'url' },
        { key: 'project_key', label: 'Project Key', type: 'string' }
      ],
      // 状态映射：UI 动态生成
      status_mapping_template: [
        { canonical: 'TODO', jira_values: ['To Do', 'Open', 'Backlog'] },
        { canonical: 'IN_PROGRESS', jira_values: ['In Progress', 'In Review'] },
        { canonical: 'DONE', jira_values: ['Done', 'Closed', 'Resolved'] }
      ]
    };
  }

  async listWorkItems(query: WorkItemQuery): Promise<WorkItemPage> {
    // JQL: project = 'PROJ' AND status != Done
  }

  async createWorkItem(input: WorkItemInput): Promise<WorkItem> {
    // POST /rest/api/3/issue
    // 写 work_items(provider_key='jira', external_ref=issue.key)
  }

  // ... 其他方法
}
```

## 5. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| POST | `/projects/:id/work-management/jira/connect` | 启动 OAuth 3LO | project owner |
| GET | `/work-management/jira/callback` | OAuth 回调 | 公开（带 state） |
| POST | `/projects/:id/work-management/jira/disconnect` | 断开 | project owner |
| PATCH | `/projects/:id/work-management/bindings` | 切到 Jira | project owner |
| POST | `/sync/jira/webhook` | Jira webhook 入口 | 公开（带签名校验） |
| POST | `/sync/retry` | 重试失败的同步 | system |

## 6. 关键流程

### 6.1 OAuth 连接

```
1. POST /projects/:id/work-management/jira/connect
   → 重定向到 Atlassian authorize URL
   → state = sign(project_id + user_id + nonce)
2. 用户授权 → 回调 /work-management/jira/callback
   → 校验 state
   → 拿 access_token + refresh_token
   → 加密存 work_management_connections
3. 触发 getSelfMetadata → 写 binding.settings
```

### 6.2 WorkItem 双向同步

**MateOS → Jira**：
```
MateOS createWorkItem:
  1. POST Jira /rest/api/3/issue { fields: { project, summary, description, issuetype } }
  2. 写 work_items (provider_key='jira', external_ref='PROJ-123')
  3. 写 work_item_projections
  4. 写 sync_audit(direction=OUT, status=OK, payload_hash=sha256(content))
```

**Jira → MateOS（webhook）**：
```
POST /sync/jira/webhook
  1. 校验 X-Hub-Signature（HMAC）
  2. 入 BullMQ sync.jira.inbound
  3. Worker 拉 Issue 最新状态
  4. 比对 payload_hash → 相同跳过（防循环）
  5. 写 work_items 更新 + sync_audit(direction=IN, status=OK)
  6. WS 推 work_item.updated
```

### 6.3 冲突检测

```
双向同时更新：
  - 维护 last_modified_at
  - webhook 收到时检查 last_modified_at：本地更新晚则拒
  - 标 sync_audit.status=CONFLICT + 通知 project owner
```

### 6.4 失败重试

```
sync_audit.status=FAILED
  → 指数退避 1min / 5min / 30min / 2h / 12h
  → 5 次后告警
  → owner 可手动 POST /sync/retry
```

## 7. UI

- **P4 Project Dashboard**：Work Management 卡片（v0.4 改）—— 选 Jira 时展开 Site / Project Key / Status Mapping
- **P11 Project Settings - Work Management**：
  - Provider 单选
  - Jira 动态字段（site / project_key / status mapping 表）
  - Connection 状态（已连接 / token 过期 / 断开）
  - 最近 10 次 sync 记录（带重试按钮）

## 8. 验收标准

### 8.1 功能

- **F1** OAuth 3LO 全流程跑通，token 加密存储
- **F2** token 任何 GET 不返回明文
- **F3** MateOS WorkItem → Jira Issue 创建成功（含 deep link）
- **F4** Jira Issue 状态变更 → webhook → MateOS WorkItem 更新
- **F5** 评论双向同步
- **F6** **v0.4 新增** payload_hash 去重（防循环同步）
- **F7** 冲突检测：标 CONFLICT + 通知
- **F8** Change Provider 不影响历史 WorkItem（E8 F5 复用）
- **F9** 失败重试：指数退避

### 8.2 E2E

- `e2e/E9-001-jira-oauth`
- `e2e/E9-002-jira-workitem-create`
- `e2e/E9-003-jira-webhook-inbound`
- `e2e/E9-004-jira-comment-bidir`
- `e2e/E9-005-payload-hash-dedup`（v0.4 新）
- `e2e/E9-006-conflict-detection`
- `e2e/E9-007-change-provider-no-migration`
- `e2e/E9-008-retry-after-failure`

### 8.3 非功能

- OAuth callback P95 < 2s
- 同步失败重试：指数退避
- 1000 project × 每日 10 次同步 = 10k/日，BullMQ 无积压

## 9. 与其他 Epic 的关系

- **被依赖**：无（E9 是叶子）
- **依赖**：E8（Provider 抽象 + WorkItem 域 + binding 关联）
- **冲突裁决**：执行路径不切 Jira（Jira 仅 WorkItem Provider）；Jira webhook 只同步 WorkItem / Comment，不触达 Agent / Execution

## 10. 风险与开放问题

- **R1**：Jira OAuth refresh token 自动刷新
- **R2**：webhook 重放保护（signature + timestamp window）
- **R3**：OAuth 撤销（用户从 Atlassian 取消授权）— webhook 兜底
- **R4**：自托管 Jira（Data Center）vs Cloud API 差异——V1+ 仅 Cloud

## 11. 实施顺序（V1+，独立里程碑）

1. JiraProvider 实现（继承 E8 接口）
2. OAuth 3LO + token 加密
3. MateOS WorkItem → Jira Issue 单向创建
4. Jira webhook inbound + 反向同步
5. 评论双向同步 + 冲突检测
6. payload_hash 去重
7. P11 Work Management Settings UI
8. E2E 套件
