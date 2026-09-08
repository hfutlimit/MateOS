# E10 · Observability & Operations

| 字段 | 值 |
| --- | --- |
| Epic ID | E10 |
| 标题 | Observability & Operations |
| 阶段 | MVP（M9） |
| 上游 | PRD v0.4 / SYSTEM_DESIGN v0.3 §11 / v0.3 旧 E8 升级（编号顺延） |
| 下游 | 所有 epic（audit / 监控 / 通知） |
| 状态 | Draft（v0.4 重命名 E8→E10） |

## 1. 背景与动机

v0.4 重新切成 10 个 epic 后，原 v0.3 的 E8 Observability 顺延为 **E10**。功能范围不变：

- Audit logs 全量写所有写操作
- 通知中心（in-app，未来 Web Push / Email）
- OTel trace 贯穿 API → Worker → Runtime → Execution
- Grafana 面板
- 压测验证 1k WS 并发

## 2. 范围

### 2.1 In Scope

- `audit_logs`（append-only，保留 2 年）
- `notifications` 通知中心
- @all 仲裁（M9 收口）
- OTel trace
- Grafana 仪表盘
- 1k WS 压测
- 跨域 audit 触发点（v0.4 新增：collaboration_request / execution / work_item 三类操作均写 audit）

### 2.2 Out of Scope

- Web Push / Email / SMS（V2）
- SIEM 集成（V3+）
- 用户行为分析（V3+）

## 3. 数据模型

```sql
-- audit_logs（v0.4 新增：覆盖 v0.3 旧触发点）
CREATE TABLE audit_logs (
  id          BIGSERIAL PRIMARY KEY,
  actor_type  TEXT NOT NULL CHECK (actor_type IN ('USER','AGENT','SYSTEM','RUNTIME','PROVIDER')),
  actor_id    UUID,
  action      TEXT NOT NULL,
  target_type TEXT,
  target_id   UUID,
  detail      JSONB,
  trace_id    TEXT,
  ip          INET,
  user_agent  TEXT,
  created_at  TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_audit_actor_time ON audit_logs(actor_type, actor_id, created_at DESC);
CREATE INDEX idx_audit_target ON audit_logs(target_type, target_id);
CREATE INDEX idx_audit_action_time ON audit_logs(action, created_at DESC);

CREATE TABLE notifications (
  id            UUID PRIMARY KEY,
  recipient_type TEXT NOT NULL,
  recipient_id  UUID NOT NULL,
  type          TEXT NOT NULL,
  title         TEXT NOT NULL,
  body          TEXT,
  link          TEXT,
  read_at       TIMESTAMPTZ,
  created_at    TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_notif_recipient_unread ON notifications(recipient_type, recipient_id, read_at) WHERE read_at IS NULL;
```

### 3.1 v0.4 审计触发点扩展

| Epic | action（v0.4 新增） |
| --- | --- |
| E1 | `USER_REGISTERED` / `ORG_CREATED` / `ORG_MEMBER_INVITED` / `TEAM_CREATED` / `PROJECT_CREATED` / `PROJECT_MEMBER_INVITED` |
| E2 | `AGENT_CREATED` / `AGENT_PAUSED` / `AGENT_ACTIVATED` / `AGENT_DISABLED` / `CREDENTIAL_CREATED` / `CREDENTIAL_DELETED` / `AGENT_JOINED_PROJECT` |
| E3 | `CHANNEL_CREATED` / `MESSAGE_DELETED` / `ATTACHMENT_UPLOADED` |
| **E4** | **`TRIGGER_CREATED`** / **`COLLABORATION_REQUEST_CREATED`** / `MENTION_RESOLVED` / **`COLLABORATION_REQUEST_RESOLVED`** / `DECISION_PROPOSED` |
| E5 | `MEMORY_PROPOSAL_CREATED` / `MEMORY_PROPOSAL_APPROVED` / `MEMORY_PROPOSAL_REJECTED` |
| E7 | `AGENT_EXECUTION_STARTED` / **`AGENT_EXECUTION_COMPLETED`** / **`AGENT_EXECUTION_FAILED`** / `RUNTIME_CONNECTED` / `RUNTIME_DISCONNECTED` |
| **E8** | **`WORK_ITEM_CREATED`** / **`WORK_ITEM_STATUS_CHANGED`** / **`WORK_ITEM_ASSIGNED`** / **`PROVIDER_BINDING_CHANGED`** |
| E9 | `JIRA_OAUTH_CONNECTED` / **`JIRA_SYNC_OK`** / **`JIRA_SYNC_FAILED`** / **`JIRA_SYNC_CONFLICT`** |

## 4. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET | `/audit-logs` | 审计查询 | login |
| GET | `/notifications` | 我的通知 | login |
| GET | `/notifications/unread-count` | 未读数 | login |
| POST | `/notifications/:id/read` | 标已读 | login |
| GET | `/metrics` | Prometheus 抓取 | service |

### 4.2 WebSocket

- `notification.new`
- `notification.read`

## 5. 关键流程

### 5.1 Audit 写入

```
业务事务内 INSERT audit_logs
  → 提交事务
  → 大对象 detail 异步落 S3
```

### 5.2 通知聚合

```
触发源（如 E4 NEED_CONTEXT）：
  1. INSERT notifications
  2. WS 推 notification.new
  3. 未读数 +1（Redis 缓存）
```

### 5.3 OTel trace

```
请求进 API：
  → 解析 traceparent
  → 创建 span 'api.handle'
  → 业务调用：
    - orchestrator.resolve
    - collaboration_request.{state}
    - agent_execution.{state}
    - memory.{proposal|item}
    - work_item.{create|update}
  → trace_id 落 OTel collector
```

### 5.4 @all 仲裁（M9 收口）

```
- @all 触发 → Resolver 全部 lifecycle=ACTIVE Agent
- 成本计算：sum(token_estimation)
- 阈值超 50k tokens → 弹二次确认
```

## 6. UI

- **P9 通知中心**（待做）
- **P10 审计查询**（待做）
- **Grafana 面板**（运维）
- 顶栏通知铃铛（已有 v0.4 P5）

## 7. 验收标准

### 7.1 功能

- **F1** 所有写操作必写 audit_logs
- **F2** 通知中心未读数与顶栏一致
- **F3** WS 推 notification.new 后未读实时更新
- **F4** OTel trace 贯穿 API → Worker → Runtime → Execution
- **F5** **v0.4 新增** E8 WorkItem / E4 CollaborationRequest / E7 Execution 三类操作均写 audit
- **F6** @all 50k+ 弹二次确认
- **F7** 1k WS 压测 P95 < 1s

### 7.2 E2E

- `e2e/E10-001-audit-everywhere`
- `e2e/E10-002-notification-delivery`
- `e2e/E10-003-otel-trace`
- `e2e/E10-004-all-cost-warning`
- `e2e/E10-005-load-1k-ws`
- `e2e/E10-006-workitem-audit`（v0.4 新）
- `e2e/E10-007-execution-audit`（v0.4 新）

### 7.3 非功能

- audit_logs INSERT P99 < 20ms
- 通知中心 GET P99 < 100ms

## 8. 与其他 Epic 的关系

- **被依赖**：无（E10 是叶子）
- **依赖**：所有 epic 提供事件源
- **冲突裁决**：audit 触发点用单元测试枚举 + DB trigger 兜底

## 9. 风险与开放问题

- **R1**：audit_logs 写盘压力 → 按月分区 + 冷数据归档 S3
- **R2**：@all token 估算精度（V1 粗估，V2 实测校准）
- **R3**：v0.4 新增 3 个 epic（E4 / E7 / E8）的 audit 覆盖用 DB trigger + 单元测试双保险

## 10. 实施顺序（M9）

1. audit_logs 触发点扩展（E4 / E7 / E8）
2. notifications 表 + 聚合
3. OTel SDK 集成 + trace 出口
4. Prometheus metrics + Grafana 面板
5. @all 成本阈值
6. locust 压测脚本
7. P9 / P10 UI
8. E2E 套件
