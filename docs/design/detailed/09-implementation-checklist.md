# Detailed Design · 09 · Implementation Checklist

> **v0.4.3 修正**（P2）：重排依赖图，避免隐藏循环；E10 基础设施从 M1 起就存在。
> 前置：所有 detailed 设计

## 0. 范围

- 里程碑 M1-M9 + V1+ E9
- 依赖图（v0.4.3 修正）
- 实施顺序
- DoD
- 风险

## 1. 依赖图（v0.4.3 修正：去掉循环）

```
        M1 (E1 Identity)
        / | \
       /  |  \
      ↓   ↓   ↓
     M2   M3a M4a
   (E3 Channel) (E2 Agent) (E6 Perm)
     ↓    ↓    ↓
     ↓   M3b   ↓
     ↓  (E7 Connector/Transport)
     ↓    ↓    ↓
     ↓   M4b (E4 Resolver)
     ↓    ↓      ↓
     ↓   M5 (E5 Memory)   ← E4 包含 M4a 依赖
     ↓    ↓
     ↓   M6 (E8 Work Management)  ← E4 包含 M4a
     ↓    ↓
     ↓   V1+: E9 (Jira Provider)
     ↓
     M7 (E7 Execution Domain)   ← M3b (transport) + M4b (resolver)
     ↓
     M8 (E10 Observability)    ← 基础设施从 M1 起就有
     ↓
     M9 (集成 + 压测)
```

**v0.4.3 关键修正**：
- M3b（Connector 基础）是 transport 层（提供 `collaboration.request` 等）
- M4b（E4 Resolver）依赖 M3b
- M7（E7 Execution Domain）依赖 M3b + M4b
- **不**再有"Resolver 调 create_execution → E4 / E7 协议混"循环

## 2. E10 Observability 基础从 M1 起就有

```
从 M1 开始就建立：
  - 结构化日志 (pino / winston)
  - trace_id 生成 + 透传
  - audit_logs 写入基础设施
  - metrics 规范（counter / histogram / gauge 命名）
  - 错误分类 taxonomy

从 M1 开始每个 M 都写 audit，框架统一
完整 Dashboard 留到 M8
```

**v0.4.3 关键**：
- 不等 M8 才补 tracing
- 否则前面 6 个 milestone 写完后再补会非常痛苦

## 2.1 首个纵向闭环的 Agent 端（v0.4.5 提案 · 待拍板）

M1 的验收不能卡在「Agent 进程从哪来」这个产品级问题上（V1 禁止 `execute_code`，PRD §8 Non Goals）。提案：

- **M1 用 stub Agent 作为测试替身**，但必须**完整实现 Connector 协议**：`hello` / `heartbeat` / `collaboration.decision` / `execution.dispatch` / `execution.event`（带 `provider_event_id` + seq）/ `execution.result` / `execution.resume_request`+`resume_ack`。产出内容是假的，协议行为是真的。
- 这样 Runtime Gateway、attempt 状态机、lease 取放与重建、event 幂等、resume 连续位点、outbox relay 全部被真实验证；换成真 Agent 时 Runtime 侧零改动。
- **不建议**「人类在界面手写产出」当作闭环验收：那条路径绕过 dispatch / attempt / lease / resume，等于没验证 Execution 域。
- 「真 Agent 由谁提供（fork 上游 / 用户自部署 / 其他）」仍是独立拍板项，不因 M1 用 stub 而被决定。

## 3. 关键设计决策点（实施前必须确认）

（同 v0.4.2）

### 3.1 框架选型

```yaml
backend:
  framework: NestJS (Node 22 LTS, TypeScript)
  orm: Prisma + PostgreSQL 16
  queue: BullMQ + Redis 7
  test: Vitest + pytest

frontend:
  framework: Next.js + TypeScript
  ui: antd 6
  state: Zustand + TanStack Query
  realtime: WebSocket client
```

### 3.2 部署

（同 v0.4.2）

### 3.3 安全清单

（同 v0.4.2）

## 4. 仓库结构

（同 v0.4.2）

```
mateos/
├── apps/
│   ├── web/                  # Next.js 前端
│   └── api/                  # NestJS 单体
├── packages/
│   ├── contracts/            # 共享 DTO + zod schema
│   │   ├── auth/
│   │   ├── project/
│   │   ├── channel/
│   │   ├── agent/
│   │   ├── execution/
│   │   ├── memory/
│   │   ├── work-item/
│   │   ├── permission/       # 8 键 + propose_memory
│   │   └── provider/        # WorkManagementProvider interface
│   └── config/              # ESLint / tsconfig
├── services/
│   ├── orchestrator/        # E4 Resolver
│   ├── memory/              # E5 索引
│   └── runtime/             # E7 Runtime Gateway + Connector transport
├── tests/
│   ├── e2e/                 # pytest -m e2e
│   ├── perf/                # locust 压测
│   └── unit/                # vitest
├── docs/
│   ├── requirement/         # PRD + epic
│   ├── design/              # SD + detailed
│   └── UI design/           # UI DS + 原型
└── .github/
    └── workflows/
        └── ci.yml
```

## 5. 测试策略

| 层 | 工具 | 覆盖 |
| --- | --- | --- |
| 单元 | Vitest | ≥ 80% |
| 集成 | Vitest + docker | DB/Redis/S3 mock |
| E2E | pytest | ~60 个 |
| 压测 | locust | 1k WS |
| 契约 | pact | E4↔E7 / Client↔API |

### 5.1 E2E 套件 v0.4.3 汇总

| epic | tests | 关键 |
| --- | --- | --- |
| 01 single-agent | 5 | 含 P0-1 execution-after-accept、outbox-durability |
| 02 interrupt-cancel | 9 | 含 P1-3 cancel 不抹 ACCEPTED |
| 03 ws-resume | 9 | 含 P1-1 restart 区分、P1-2 seq gap |
| 04 resolver-routing | 13 | 含 per-lease ZSET、promote lease |
| 05 memory-approval | 9 | 含 P1-8 propose_memory、P1-9 approve race、P1-10 accessible_project_ids |
| 06 workitem-sync | 12 | 含 P1-6 binding_id、P1-7 webhook 表 |
| 07 permission | 9 | 含 P1-8 propose_memory vs write_memory |
| 08 error-retry | 10 | 含 P1-4 max_attempts、P1-5 health |
| **总计** | **76** | |

## 6. 实施顺序

### 6.1 阶段 1（M1-M3）—— 骨架

```
M1: E1 Identity & Workspace
  1. NestJS + Prisma + PG
  2. users / organizations / organization_members (v0.4.2 删 owner_id)
  3. /auth/* (JWT 双令牌 + refresh rotation)
  4. /orgs / /teams / /projects CRUD
  5. /me + 改密
  6. 基础设施：结构化日志 + trace_id + audit_logs + metrics
  7. E2E 注册→登录→create project

M2: E3 Channel & Messaging
  1. channels / channel_members / messages / channel_seq_counters
  2. seq 分配事务
  3. /channels/:id/messages (POST + GET + since_seq=)
  4. WS 网关 + message.created 广播
  5. resume(last_seq) 协议
  6. /attachments/presign + S3
  7. P5 Channel 前端
  8. E2E send / receive / resume / attachment

M3a: E2 Agent Registry
  1. credentials / agents / agent_project_membership / agent_tokens
  2. /credentials + /agents CRUD
  3. /agents/:id/pause / activate / disable (lifecycle)
  4. /agents/:id/health POST { health: 'HEALTHY' }  # v0.4.3 新增
  5. /agents/:id/tokens 签发 + revoke
  6. P2 / P3 前端
  7. E2E lifecycle / double approval

M3b: E7 Connector (transport 基础)
  1. WSS Gateway (NestJS)
  2. hello / heartbeat / status
  3. E2 lifecycle 变化 → agents.activity / health 写库
  4. v0.4.3 改：新增 collaboration.request 消息类型
  5. E2E connect / heartbeat / collaboration_request_transport
```

### 6.2 阶段 2（M4-M5）

```
M4a: E6 Authorization
  1. permissions 表 + 8 键（v0.4.3 propose_memory）
  2. checkPermission (sync 纯函数)
  3. Redis 缓存 + pub/sub 失效
  4. PermissionGuard (只决 ALLOW/DENY)
  5. policy.evaluate 业务接口
  6. /permissions CRUD
  7. E2E default matrix / channel override / cache invalidation

M4b: E4 Resolver
  1. triggers / collaboration_requests / decision_records
  2. Resolver Worker (BullMQ)
  3. 能力排序
  4. Redis Lua: tryAcquirePendingDecision / promoteLease / releaseLease / renewExecutionLease
  5. 90s 超时重路由
  6. E4 推 collaboration.request WS  # v0.4.3 新增
  7. /collab/:id/cancel
  8. outbox_events 表 + worker
  9. P5 mention 胶囊 + 命中分数气泡
  10. E2E resolver rank / slot race / promote / lifecycle pause

M5: E5 Shared Memory
  1. memory_proposals / memory_items / memory_chunks / memory_review_actions
  2. /memory-proposals CRUD (propose / approve / reject / edit-approve / withdraw)
  3. Source 三件套 DB CHECK
  4. Policy.evaluate('propose_memory') 走 E5 流程
  5. accessible_project_ids() 统一权限查询
  6. UNIQUE(memory_items.proposal_id) 防 race
  7. 消息流 MEMORY_REQUEST projection
  8. P6 审批中心
  9. P7 Memory 文档
  10. 索引 worker (tsv)
  11. E2E propose / approve / reject / source / cross-project / personal / race
```

### 6.3 阶段 3（M6-M8）

```
M6: E8 Work Management Core
  1. work_items (v0.4.3 加 binding_id) / work_item_bindings
  2. work_management_connections (v0.4.2 org 级)
  3. work_management_webhooks (v0.4.3 新增独立表)
  4. WorkManagementProvider interface + BuiltInProvider
  5. ProviderRegistry
  6. routing: CREATE → active binding; UPDATE → work_item.binding  # v0.4.3 改
  7. 状态机迁移
  8. UNIQUE INDEX (work_item_bindings active)
  9. UNIQUE(work_items(binding_id, external_ref))  # v0.4.3 改
  10. P-Work 页面
  11. P11 Settings
  12. E2E CRUD / change binding no loss / active unique

M7: E7 Agent Execution Domain (完整)
  1. agent_executions / execution_attempts / execution_events / execution_artifacts
  2. ALTER TABLE execution_attempts ADD COLUMN dispatch_sent_at / dispatch_acked_at / last_persisted_seq
  3. ALTER TABLE agents ADD COLUMN health
  4. execution.dispatch 协议 (v0.4.3 加 collab_request_id 可选)
  5. E4 → E7 内部 API: POST /internal/agent-executions
  6. event 流式 (v0.4.3 contiguous cursor)
  7. result envelope + terminal CAS
  8. resume_request / resume_ack (v0.4.3 contiguous)
  9. attempt ≠ WS session
  10. promoteLease pending_decision → execution
  11. Error 重试 (max_attempts=4)
  12. E2E dispatch / event gap / resume / terminal CAS / cancel-result race / agentHealth

M8: E10 Observability & Operations (完整 Dashboard)
  1. Grafana 面板
  2. @all 成本阈值
  3. locust 压测
  4. P9 通知中心
  5. P10 审计
  6. E2E audit / notification / trace / load
```

### 6.4 阶段 4（M9 + V1+）

```
M9: 集成 + 压测
  1. 76 个 E2E 全过
  2. 1k WS 压测：P95 < 1s
  3. 故障注入：DB 断 / Redis 断 / Provider 5xx
  4. 安全审计
  5. 性能优化
  6. MVP 部署文档

V1+: E9 Jira Provider
  1. JiraProvider 实现
  2. OAuth 3LO + token 加密
  3. work_management_webhooks 注册 + 30 天 refresh
  4. 双向同步 + 冲突检测
  5. 状态映射动态拉
  6. E2E
```

## 7. 每个里程碑的"完成定义"（DoD）

（同 v0.4.2，case 数更新为 v0.4.3 后的 76 个）

## 8. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
| --- | --- | --- | --- |
| Redis Cluster 部署复杂度 | 高 | 高 | ZSET 脚本 key 必须带 hash tag |
| LLM Provider API 变化 | 中 | 中 | Provider Adapter 抽象 |
| WS 大规模并发 | 中 | 高 | M8 压测 |
| User adoption | 中 | 中 | P-Work 渐进推广 |
| Sandbox 执行（V3） | 低 | 低 | V1 暂不实现 |

## 9. v0.4.3 关键变更（必须落地的 schema migration）

```sql
-- M3a (E2) 时机
ALTER TABLE agents
  ADD COLUMN health TEXT NOT NULL DEFAULT 'HEALTHY'
    CHECK (health IN ('HEALTHY', 'DEGRADED', 'UNHEALTHY'));

-- M3b (E7) 时机
ALTER TABLE execution_attempts
  ADD COLUMN dispatch_sent_at TIMESTAMPTZ,
  ADD COLUMN dispatch_acked_at  TIMESTAMPTZ,
  ADD COLUMN last_persisted_seq  BIGINT NOT NULL DEFAULT 0;

-- M4a (E6) 时机
-- (8 键 permissions, no schema change if 7→8 兼容)

-- M4b (E4) 时机
ALTER TABLE collaboration_requests
  ADD COLUMN lease_type TEXT NOT NULL DEFAULT 'pending_decision'
    CHECK (lease_type IN ('pending_decision', 'execution'));

-- M5 (E5) 时机
ALTER TABLE memory_items
  ADD CONSTRAINT uq_memory_items_proposal UNIQUE (proposal_id);

-- M6 (E8) 时机
ALTER TABLE work_items
  ADD COLUMN binding_id UUID NOT NULL REFERENCES work_item_bindings(id);
DROP INDEX IF EXISTS uq_work_items_provider_external;
CREATE UNIQUE INDEX uq_work_items_binding_external
  ON work_items(binding_id, external_ref) WHERE external_ref IS NOT NULL;

-- Webhook 独立表
CREATE TABLE work_management_webhooks (
  id                  UUID PRIMARY KEY,
  binding_id          UUID NOT NULL REFERENCES work_item_bindings(id) ON DELETE CASCADE,
  connection_id       UUID NOT NULL REFERENCES work_management_connections(id),
  external_webhook_id TEXT NOT NULL,
  filter_jql          TEXT,
  filter_events       JSONB,
  expires_at          TIMESTAMPTZ NOT NULL,
  last_refreshed_at   TIMESTAMPTZ,
  refresh_status      TEXT,
  last_error          TEXT,
  created_at          TIMESTAMPTZ DEFAULT now()
);

-- outbox_events（所有 epic 共用）
CREATE TABLE outbox_events (
  id              UUID PRIMARY KEY,
  aggregate_type  TEXT NOT NULL,
  aggregate_id    UUID NOT NULL,
  event_type      TEXT NOT NULL,
  payload         JSONB NOT NULL,
  idempotency_key TEXT UNIQUE,
  published_at    TIMESTAMPTZ,
  attempt_count   INT NOT NULL DEFAULT 0,
  next_attempt_at TIMESTAMPTZ DEFAULT NOW(),
  last_error      TEXT,
  created_at      TIMESTAMPTZ DEFAULT now()
);
```

## 10. 与其他设计的关系

- 详见所有 [01](./01-single-agent-task-lifecycle.md) - [08](./08-error-and-retry.md) 详细设计
- 详见 epic 文档 `docs/requirement/epic/`
