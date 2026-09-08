# E8 · Observability & Operations

| 字段 | 值 |
| --- | --- |
| Epic ID | E8 |
| 标题 | Observability & Operations |
| 阶段 | MVP（M6） |
| 上游 | PRD v0.3 §6 审计 / SYSTEM_DESIGN v0.2 §11 非功能设计 / §8 事件 |
| 下游 | 所有 epic（audit / 监控 / 通知） |
| 状态 | Draft |

## 1. 背景与动机

MateOS 是"人类始终握有闸门"的多 Agent 协作系统，**审计 + 监控 + 通知**是闸门生效的保障。本 epic：

- Audit logs 全量写所有写操作
- 通知中心（in-app + 未来 Web Push / Email）
- OTel trace 贯穿 API → Worker → Runtime
- Grafana 面板：WS 连接数 / Resolver P95 / Decision 超时率 / Memory 审批积压
- 压测验证 1k WS 并发

## 2. 范围

### 2.1 In Scope

- Audit logs（append-only，保留 2 年）
- 通知中心（in-app，未读/已读）
- @all 仲裁（M5 之前为简化，V1 全部投递；M6 加成本阈值）
- OTel trace（API + Worker + Runtime）
- Grafana 仪表盘（5 个核心面板）
- 压测工具 + 1k WS 并发场景

### 2.2 Out of Scope

- Web Push / Email / SMS 通知（V2）
- SIEM 集成（V3+）
- 用户行为分析 / funnel（V3+）
- 自动告警（V2 引入，先用 Grafana alert）

## 3. 数据模型

```sql
-- audit_logs（append-only）
CREATE TABLE audit_logs (
  id          BIGSERIAL PRIMARY KEY,
  actor_type  TEXT NOT NULL CHECK (actor_type IN ('USER','AGENT','SYSTEM','RUNTIME')),
  actor_id    UUID,
  action      TEXT NOT NULL,              -- 'PROJECT_CREATED' | 'AGENT_ACTIVATED' | 'MEMORY_APPROVED' | ...
  target_type TEXT,                       -- 'project' | 'channel' | 'memory_item' | 'agent' | 'message' | ...
  target_id   UUID,
  detail      JSONB,                      -- 变更前/后快照、reason 等
  trace_id    TEXT,                       -- OTel trace_id
  ip          INET,
  user_agent  TEXT,
  created_at  TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_audit_actor_time ON audit_logs(actor_type, actor_id, created_at DESC);
CREATE INDEX idx_audit_target ON audit_logs(target_type, target_id);
CREATE INDEX idx_audit_action_time ON audit_logs(action, created_at DESC);
-- 保留 2 年（按月分区 + 冷数据归档 S3）

-- notifications
CREATE TABLE notifications (
  id            UUID PRIMARY KEY,
  recipient_type TEXT NOT NULL CHECK (recipient_type IN ('USER','AGENT')),
  recipient_id  UUID NOT NULL,
  type          TEXT NOT NULL,             -- 'AGENT_NEED_CONTEXT' | 'MEMORY_PENDING' | 'AGENT_ERROR' | ...
  title         TEXT NOT NULL,
  body          TEXT,
  link          TEXT,                      -- 跳转 URL
  read_at       TIMESTAMPTZ,
  created_at    TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_notif_recipient_unread ON notifications(recipient_type, recipient_id, read_at) WHERE read_at IS NULL;
CREATE INDEX idx_notif_created ON notifications(created_at DESC);

-- metrics（Prometheus 抓取，DB 不存原始数据）
-- 落 Prometheus TSDB + Grafana dashboard
```

### 3.1 audit 触发点

| 模块 | 动作 |
| --- | --- |
| E1 | `USER_REGISTERED` / `PROJECT_CREATED` / `MEMBER_INVITED` / `MEMBER_REMOVED` |
| E2 | `AGENT_CREATED` / `AGENT_PAUSED` / `AGENT_ACTIVATED` / `AGENT_DELETED` / `CREDENTIAL_CREATED` / `CREDENTIAL_DELETED` / `AGENT_JOINED_PROJECT` / `AGENT_LEFT_PROJECT` |
| E3 | `CHANNEL_CREATED` / `MESSAGE_DELETED` / `ATTACHMENT_UPLOADED` |
| E4 | `MENTION_RESOLVED` / `DECISION_PROPOSED` / `MENTION_UNRESOLVED` |
| E5 | `MEMORY_PROPOSED` / `MEMORY_APPROVED` / `MEMORY_REJECTED` |
| E7 | `RUNTIME_CONNECTED` / `RUNTIME_DISCONNECTED` / `RUNTIME_ERROR` |

### 3.2 通知类型

| type | 触发 | 接收方 |
| --- | --- | --- |
| `AGENT_NEED_CONTEXT` | E4 NEED_CONTEXT 决策 | mention 发起人 |
| `MEMORY_PENDING` | E5 新 PROPOSED | project owner |
| `AGENT_ERROR` | E7 ERROR 触发 | agent owner |
| `AGENT_OFFLINE` | E7 心跳过期 | agent owner |
| `COLLAB_INVITE` | E1 邀请 | 被邀请人 |
| `MENTION_UNRESOLLED` | E4 全部超时 | mention 发起人 |

## 4. API

### 4.1 REST

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET | `/audit-logs?actor_id=&action=&target_type=&target_id=&since=&until=&cursor=` | 审计查询（分页） | login |
| GET | `/notifications` | 我的通知（未读优先） | login |
| GET | `/notifications/unread-count` | 未读数 | login |
| POST | `/notifications/:id/read` | 标已读 | login |
| POST | `/notifications/read-all` | 全部已读 | login |

### 4.2 WebSocket

- `notification.new` — 新通知推送
- `notification.read` — 已读事件

### 4.3 Metrics Endpoint

- `GET /metrics`（Prometheus 抓取端点，独立端口或 path）
- OTel trace exporter：HTTP/protobuf

### 4.4 错误码

- 403 非 owner 查他人 audit（个人动作可查，组织级需 admin）
- 422 时间范围 > 90 天单次查询

## 5. 关键流程

### 5.1 Audit 写入（同步 + 异步混合）

```
业务操作：
1. 业务事务内 INSERT audit_logs（actor, action, target, detail）
2. 提交事务
3. 异步（如 detail 包含大对象）落 S3
```

### 5.2 通知聚合

```
触发源（如 E4 NEED_CONTEXT）：
1. INSERT notifications
2. WS 推 notification.new 给该 user 所有在线 tab
3. 未读数 +1（Redis cache 增量）
4. 若 user 在线 → 顶栏 +1 + 角标
```

### 5.3 OTel trace 贯穿

```
请求进 API：
1. 解析 traceparent header
2. 创建 span 'api.handle'
3. 业务调用：
   - Resolver: span 'orchestrator.resolve'
   - Decision: span 'runtime.dispatch'
   - Memory: span 'memory.write'
4. 每个 span 带：channel_id, project_id, agent_id, user_id, mention_id
5. trace 落 OTel collector → Jaeger / Tempo
```

### 5.4 @all 仲裁（M6 补完）

```
- @all 触发 → Resolver 全部 AVAILABLE + THINKING agent
- 成本计算：sum(token_estimation per agent)
- 若 total > threshold（如 50k tokens）→ 发预警通知给发起人
  - "本次 @all 预计 32k tokens，确认发送？" 二次确认
- 阈值从配置读，V1 写死 50k，V2 动态
```

### 5.5 压测（M6 必做）

```
locust 脚本（tests/perf/）：
- 1000 个 WS 客户端连接
- 每 30s 心跳
- 100 个并发 mention 触发
- 监控：WS 连接数 / Resolver P95 / 错误率 / Redis QPS
- 通过条件：P95 < 1s，0 错误
```

## 6. UI

### 6.1 页面

- **P9 通知中心**（待做）：列表 + 全部已读 + 按类型分组
- **P10 审计查询**（待做）：admin 可见
- **Grafana 面板**（运维）：WS / Resolver / Decision / Memory / Runtime 五个

### 6.2 顶栏

- 通知铃铛（已有 v0.4 P5 原型）：未读数 + 红点
- 点击下拉：最近 5 条 + 跳转 P9

## 7. 验收标准

### 7.1 功能

- **F1** 所有写操作必写 audit_logs（用单元测试枚举 + DB trigger 兜底）
- **F2** 通知中心未读数与顶栏铃铛一致
- **F3** WebSocket 推 notification.new 后未读数实时更新
- **F4** OTel trace 贯穿 API → Worker → Runtime，trace_id 能在 Jaeger 看到全链路
- **F5** @all 在 50k tokens 阈值时弹二次确认
- **F6** 1k WS 压测：P95 < 1s，0 错误
- **F7** 审计查询支持时间范围 + 多字段过滤

### 7.2 E2E

- `e2e/E8-001-audit-everywhere`：枚举所有写操作端点，验证 audit 落
- `e2e/E8-002-notification-delivery`：触发 → 通知入库 → WS 推 → 顶栏 +1
- `e2e/E8-003-otel-trace`：发起请求 → Jaeger 查到完整 trace
- `e2e/E8-004-all-cost-warning`：@all 触发 50k+ → 弹确认
- `e2e/E8-005-load-1k-ws`：1k WS 压测通过

### 7.3 非功能

- audit_logs INSERT P99 < 20ms
- 通知中心 GET P99 < 100ms
- WS 通知广播 P99 < 200ms
- Grafana 面板加载 < 2s

## 8. 与其他 Epic 的关系

- **被依赖**：所有 epic 都触发 audit
- **依赖**：所有 epic 都提供事件源
- **冲突裁决**：audit 触发点用单元测试枚举 + DB trigger 兜底（不可绕过）

## 9. 风险与开放问题

- **R1**：audit_logs 写盘压力 → 按月分区 + 冷数据归档 S3
- **R2**：通知聚合粒度（M5 提到的"驳回记忆只移除目标卡片并生成系统事件"）→ E5 触发 + E8 通知合并
- **R3**：@all 仲裁的 token 估算精度 → V1 粗估（capability 数 × 1k），V2 实测校准
- **R4**：压测环境（V1 用 staging，需独立部署）

## 10. 实施顺序（M6）

1. audit_logs 表 + 触发点枚举（每个 epic 提交时必须新增 audit）
2. notifications 表 + 通知聚合
3. OTel SDK 集成 + trace 出口
4. Prometheus metrics + Grafana 面板
5. @all 成本阈值 + 二次确认
6. locust 压测脚本
7. 1k WS 压测验收
8. P9 / P10 UI（前端）
9. E2E 套件
