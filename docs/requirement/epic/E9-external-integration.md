# E9 · External Integration Extensions

| 字段 | 值 |
| --- | --- |
| Epic ID | E9 |
| 标题 | External Integration Extensions |
| 阶段 | V1+（PRD §10，MVP 不实现真实同步） |
| 上游 | PRD v0.3 §10 / SYSTEM_DESIGN v0.2 §8.1 / §10 |
| 下游 | E2（执行后端切换）、E4（协作 request 协议）、E5（记忆双向同步） |
| 状态 | Draft（V1+ 阶段详细化） |

## 1. 背景与动机

MateOS 自洽运行（SYSTEM_DESIGN §1.1），但要融入现有工具链必须有**可选**的扩展点。两条扩展：
- **执行后端切换**：默认 MateOS Runtime；可切到 AgentBoard，由 AgentBoard 负责长时任务 / PR 流转
- **项目管理切换**：默认仅在 MateOS 内展示协作请求；可切到 AgentBoard Issue / Jira Issue，实现双向同步

两条都是**默认关闭**的 Project 级开关。MVP 不实现真实同步逻辑，只预留数据模型、协议规范和 UI 占位（v0.4 P4 已落地「外部协作」卡）。

## 2. 范围

### 2.1 In Scope（V1+ 实际实现）

- §10.1 执行后端切换：MateOS Runtime ↔ AgentBoard
  - Decision Accept 后按 `projects.integration_backend` 选择 dispatch 路径
  - AgentBoard 协作协议（`collab.request` / `collab.status` / `collab.result`）
  - 结果回写 Channel 消息流
- §10.2 项目管理切换：None / AgentBoard Issue / Jira Issue
  - 协作请求同步到 Issue
  - 评论 / 决策回写 Issue 评论
  - 状态双向同步
- Project 设置页扩展（外部集成开关 + OAuth 连接）

### 2.2 Out of Scope

- Linear / Asana / Trello 等其他工具（V3+）
- 双向评论中 @ mention 解析（V1+ 简化：纯文本）
- 自定义字段映射（V2+）
- Issue → MateOS Memory 自动回写（V2+）

## 3. 数据模型

```sql
-- projects 表已包含（E1 §3）：
--   integration_backend TEXT  -- 'mateos' | 'agentboard'
--   issue_tracker        TEXT  -- 'none' | 'agentboard' | 'jira'
--   issue_tracker_meta   JSONB

-- external_links（实体 ↔ 外部系统映射表）
CREATE TABLE external_links (
  id              UUID PRIMARY KEY,
  entity_type     TEXT NOT NULL CHECK (entity_type IN ('TASK','MEMORY','REQUEST','CHANNEL')),
  entity_id       UUID NOT NULL,
  external_system TEXT NOT NULL,           -- 'agentboard' | 'jira'
  external_id     TEXT NOT NULL,           -- 'AB-1234' | 'PROJ-567'
  external_url    TEXT NOT NULL,
  meta            JSONB,                   -- 状态/时间戳等
  synced_at       TIMESTAMPTZ,
  last_sync_status TEXT,                   -- 'OK' | 'CONFLICT' | 'DELETED'
  last_sync_error TEXT,
  created_at      TIMESTAMPTZ DEFAULT now(),
  UNIQUE (entity_type, entity_id, external_system)
);
CREATE INDEX idx_external_links_lookup ON external_links(external_system, external_id);
CREATE INDEX idx_external_links_entity ON external_links(entity_type, entity_id);

-- external_integration_tokens（OAuth 凭据，加密存储）
CREATE TABLE external_integration_tokens (
  id              UUID PRIMARY KEY,
  project_id      UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  system          TEXT NOT NULL,           -- 'jira' | 'agentboard'
  user_id         UUID NOT NULL REFERENCES users(id),  -- 谁授权的
  access_token_encrypted  BYTEA NOT NULL,
  refresh_token_encrypted BYTEA,
  expires_at      TIMESTAMPTZ,
  scopes          TEXT,
  meta            JSONB,                  -- Jira project key、AgentBoard workspace id 等
  created_at      TIMESTAMPTZ DEFAULT now(),
  UNIQUE (project_id, system, user_id)
);

-- sync_audit（同步操作审计）
CREATE TABLE sync_audit (
  id              BIGSERIAL PRIMARY KEY,
  project_id      UUID NOT NULL,
  system          TEXT NOT NULL,
  direction       TEXT NOT NULL CHECK (direction IN ('OUT','IN')),
  entity_type     TEXT NOT NULL,
  entity_id       UUID NOT NULL,
  action          TEXT NOT NULL,           -- 'create' | 'update' | 'comment' | 'status_change'
  external_id     TEXT,
  status          TEXT NOT NULL,           -- 'OK' | 'FAILED' | 'CONFLICT'
  error_message   TEXT,
  payload_hash    TEXT,                    -- 防重复同步
  created_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_sync_audit_project_time ON sync_audit(project_id, created_at DESC);
```

## 4. API

### 4.1 REST

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET | `/projects/:id/integration` | 当前集成配置 | project owner |
| PATCH | `/projects/:id/integration` | 切换执行后端 | project owner |
| POST | `/projects/:id/integration/jira/connect` | Jira OAuth 启动 | project owner |
| GET | `/projects/:id/integration/jira/callback` | OAuth 回调 | 公开（带 state） |
| POST | `/projects/:id/integration/agentboard/connect` | AgentBoard 接入（API key） | project owner |
| POST | `/projects/:id/integration/agentboard/disconnect` | 断开 | project owner |
| GET | `/external-links?entity_type=&entity_id=` | 查外部链接 | project member |
| POST | `/sync/retry` | 重试失败的同步 | system |
| POST | `/sync/webhook/jira` | Jira webhook 入口 | 公开（带签名校验） |

### 4.2 WebSocket

- `external.sync.completed` — 同步完成广播
- `external.sync.failed` — 同步失败（顶栏 +1 通知）

### 4.3 Webhook 入口

```yaml
POST /sync/webhook/jira
  headers:
    X-Atlassian-Webhook-Identifier: ...
    X-Hub-Signature: hmac_sha256
  body: Atlassian webhook payload
  → 入 BullMQ sync.jira.inbound
```

## 5. 关键流程

### 5.1 启用 AgentBoard 执行后端

```
1. PATCH /projects/:id/integration { integration_backend: 'agentboard' }
2. PATCH /projects/:id/integration/agentboard/connect
   { workspace_id, api_key }
3. 服务端：
   a) 校验 API key 调 AgentBoard /api/v1/ping
   b) 加密存 external_integration_tokens
   c) 写 audit + sync_audit
4. 后续 decision ACCEPT 走 collab.request 而非 runtime dispatch
```

### 5.2 Decision Accept → AgentBoard dispatch（§10.1）

```
1. E4 Decision Accept
2. 检查 projects.integration_backend:
   - 'mateos' → E7 Runtime dispatch
   - 'agentboard' → collab.request
3. collab.request（WSS push）：
   { type: 'collab.request', payload: {
       request_id, from: { channel_id, actor: 'agent:backend' },
       task: { title, description, context_refs: [mem:..., msg:...] },
       sla: { deadline_s: 3600 } } }
4. AgentBoard 处理：
   a) 创建 Issue（issue_tracker=agentboard 时）
   b) 派发到 Worker
5. AgentBoard 回调 collab.status / collab.result
6. MateOS 落 decision + 更新 message
7. 写 external_links（MateOS task ↔ AgentBoard issue id）
8. 写 sync_audit
```

### 5.3 Jira 集成（§10.2）

```
OAuth 流程（3LO）：
1. POST /projects/:id/integration/jira/connect
   → 重定向到 Atlassian authorize URL，state 带 project_id + user_id 签名
2. 用户授权后回调 /integration/jira/callback
   → 拿 access_token + refresh_token
   → 加密存
3. 写 sync_audit(status=OK, direction=OUT, action='connect')

协作请求同步：
1. MateOS 创建 Task → 同步到 Jira Issue
   a) POST {jira_base}/rest/api/3/issue
      { fields: { project, summary, description (含 MateOS deep link), issuetype: { name: 'Task' } } }
   b) 写 external_links(entity_type=TASK, external_id=PROJ-123)
   c) 写 sync_audit(OK)

评论 / 决策回写：
1. MateOS Decision ACCEPTED → 在 Jira Issue 加评论
   a) POST {jira_base}/rest/api/3/issue/{id}/comment
      { body: ADF 格式含 MateOS decision + link }
   b) 写 sync_audit(OK)

状态同步：
1. Jira Issue 状态变更 → webhook 推 /sync/webhook/jira
2. 校验签名 → 入 BullMQ
3. Worker 拉 Issue 最新状态 → 写 MateOS Task 状态
4. 写 sync_audit(direction=IN, OK)
```

### 5.4 双向同步去重

```
内容指纹：content_hash = sha256(content + metadata)
- 同步前：算 content_hash 写 external_links.meta.content_hash
- 收到 webhook：对比 content_hash；相同则跳过（避免循环）
- 冲突：payload_hash 不同 + 双向都有更新 → last_sync_status=CONFLICT + 通知 owner 仲裁
```

## 6. UI

### 6.1 页面

- **P11 项目集成设置**（V1+）：P4 Dashboard 底部「外部协作」卡升级为完整设置页
  - 执行后端：MateOS Runtime（默认）/ AgentBoard（OAuth 接入）
  - 项目跟踪：无（默认）/ AgentBoard Issue / Jira Issue
  - 同步状态面板（最近 N 次同步 + 失败重试）
  - 字段映射（V2）

### 6.2 P4 Dashboard 卡片

v0.4 原型已落地「外部协作」卡（占位），V1+ 升级为可点击的设置入口。

### 6.3 状态

- 同步成功：tab 通知「同步到 Jira Issue PROJ-123」+ 跳转链接
- 同步失败：tab 通知「同步失败：网络超时」+ 重试按钮
- 冲突：弹窗「外部 Issue 状态与 MateOS Task 不一致，选择保留哪边」

## 7. 验收标准

### 7.1 功能（V1+）

- **F1** OAuth 流程跑通：Jira 授权后 callback 拿到 access/refresh token
- **F2** token 加密存储；任何 GET 不返回明文
- **F3** MateOS Task → Jira Issue 单向创建成功（含 deep link）
- **F4** Jira Issue 状态变更通过 webhook 反向同步到 MateOS Task
- **F5** 评论双向同步
- **F6** 同步去重：同一内容 fingerprint 跳过
- **F7** 冲突检测：双方同时改 → 标 CONFLICT + 通知
- **F8** AgentBoard collab 协议跑通：request → status → result 全链路
- **F9** Project 关闭集成：token 删除 + 同步历史保留

### 7.2 E2E（V1+ 阶段建立）

- `e2e/E9-001-jira-oauth`：3LO 全流程，token 加密落库
- `e2e/E9-002-jira-task-sync`：MateOS Task → Jira Issue 创建
- `e2e/E9-003-jira-webhook-inbound`：模拟 Jira webhook → MateOS Task 状态更新
- `e2e/E9-004-jira-comment-bidir`：决策评论双向写入
- `e2e/E9-005-agentboard-collab`：request → result 端到端
- `e2e/E9-006-sync-dedup`：同 fingerprint 不重复同步
- `e2e/E9-007-integration-toggle`：切回 none 后所有 token 删除

### 7.3 非功能

- OAuth callback P95 < 2s
- 同步失败重试：指数退避，1min / 5min / 30min / 2h / 12h
- 1000 project × 每日 10 次同步 = 10k/日，BullMQ 无积压

## 8. 与其他 Epic 的关系

- **被依赖**：E2（运行时切换）、E4（协作 request）、E5（记忆可同步到外部）
- **依赖**：E1（project + member）、E2（agent + runtime）
- **冲突裁决**：所有外部集成**默认关闭**，V1+ 启用；MVP 仅在 P4 留占位

## 9. 风险与开放问题

- **R1**：Jira OAuth refresh token 过期处理 — V1+ 写自动刷新中间件
- **R2**：AgentBoard collab.* 协议的幂等键与外部 Issue 双绑（SD §13 开放）— V1+ 设计
- **R3**：Jira webhook 重放保护（signature + timestamp window）— V1+ 落地
- **R4**：OAuth 授权撤销（用户从 Atlassian 取消授权）— V1+ webhook 兜底
- **R5**：自托管 Jira（Data Center）vs Cloud API 差异 — V1+ 仅 Cloud

## 10. 实施顺序（V1+，独立里程碑）

1. external_links + external_integration_tokens + sync_audit 表
2. Jira OAuth 3LO + token 加密
3. MateOS Task → Jira Issue 单向创建
4. Jira webhook inbound + 反向同步
5. 评论双向同步 + 冲突检测
6. AgentBoard collab 协议对接
7. P11 项目集成设置页
8. E2E 套件（含 7 个 case）
