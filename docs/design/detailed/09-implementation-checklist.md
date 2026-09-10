# Detailed Design · 09 · Implementation Checklist

> **v0.4.3 修正**（P2）：重排依赖图，避免隐藏循环；E10 基础设施从 M1 起就存在。
> 前置：所有 detailed 设计

## 0. 范围

- **实施切法 S1 / S2 / S3（v0.6 拍板，D7）** + 能力域标签 M1–M9 / V1+ 对照
- 每刀的阶段清单、DoD、风险

## 1. 实施切法（v0.6 拍板：S1/S2/S3 竖切）

> **M1–M9 退化为「能力域标签」**（用于指向各 detailed 分册），**不再表示实施顺序**。实施顺序 = 先跑通 S1 一条端到端闭环，再叠 S2、S3。

```
        S1 「一句话 → 产出」        ← 第一刀，必须最先跑通
        @mention → CollaborationRequest → Resolver → Decision
                 → Execution → event/result → AGENT_OUTPUT 回帖 → audit
             ↓（复用 S1 的 outbox / permission / WS / audit 底座）
        S2 「记忆闸门」
        propose_memory → 人审（Needs You → Approval）→ memory_items → 检索 → dispatch 注入
             ↓
        S3 「工作推进」
        WorkItem + Built-in Provider → Work 页面 → WorkItem ↔ Execution 关联
             ↓
        V1+: E9 Jira Provider
```

| 刀 | 端到端验收（一句话） | 包含的既有能力域 | 显式排除 |
| --- | --- | --- | --- |
| **S1** | 在 Channel 里 `@backend 看下这段代码`，Agent 接受、执行、产出回到消息流，全程可审计 | E1 最小（Org/Team/Project/Channel/Member）、E3 Channel+Message 最小、E2 Agent+Credential、E7 Connector transport **+ Execution 域主干**、E4 Resolver+Decision、E6 最小（默认矩阵）、E10 基础（结构化日志 / trace_id / audit / metrics）；Agent 端用 **stub** | Memory 审批、Work Management、Jira、通知中心、Dashboard、@all 仲裁、1k 压测 |
| **S2** | Agent 申请记忆 → 人类在 **Needs You → Approval** 批准 → 下一次 dispatch 能注入这条记忆 | E5 全量 + E6 的 `propose_memory` / `approve_memory` + Needs You 的 Approval 分类（v0.7：不再有独立审批中心页面） | Work Management、Jira |
| **S3** | PO 建 WorkItem → @agent 执行 → 结果回帖并更新 WorkItem 状态 | E8 WorkItem 域 + Built-in Provider + Work 页面 + WorkItem ↔ Execution 关联（含独立 Execution 的 UI 出口） | Jira Provider（V1+）、Mission / WorkUnit（`future/`） |

**能力域标签对照**：

| 标签 | 能力域 | 落在哪一刀 |
| --- | --- | --- |
| M1 | E1 Identity | S1 |
| M2 | E3 Channel + WS + outbox relay | S1 |
| M3a | E2 Agent Registry | S1 |
| M3b | E7 Connector transport | S1 |
| M4a | E6 Permission（8 键 + 三态） | S1（默认矩阵最小集）/ S2（propose / approve 路由） |
| M4b | E4 Collaboration & Resolver | S1 |
| M5 | E5 Memory | S2 |
| M6 | E8 Work Management | S3 |
| M7 | E7 Execution 域完整（resume / retry / artifacts） | S1（主干）/ S3（与 WorkItem 关联） |
| M8 | E10 Observability 完整（OTel/Prom/Grafana/Loki） | S1 之后按需 |
| M9 | 集成 + 压测 | S3 之后 |

**v0.6 关键**：

- **S1 必须最先跑通**——跑不通 S1，S2/S3 都没有意义；写代码前不再新增设计文档。
- 每刀自带 E10 基础（日志 / trace / audit / metrics），不再等 M8。
- S1 的 Agent 端用 **stub**（完整实现 Connector 协议、产出为假），见 §2.1 与 [10-agent-stub-and-sdk.md](./10-agent-stub-and-sdk.md)。
- 依赖关系（能力域内部）不变：M3b 是 transport 层 → M4b 依赖 M3b → Execution 域依赖 M3b + M4b；**不存在**「Resolver 调 create_execution → E4/E7 协议混」循环。
- e2e 策略：S1 只跑 **主链路 ≤ 15 条**；76 条全量属 S3 之后的收尾，不阻塞 S1 验收。

## 2. E10 Observability 基础从 M1 起就有

```
从 M1 开始就建立：
  - 结构化日志（.NET：Serilog / Microsoft.Extensions.Logging；v0.5 由 pino/winston 更正）
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

## 2.1 S1 的 Agent 端：stub（v0.6 已转正）

**结论已拍板**：S1 用 stub Agent 作为测试替身，但必须**完整实现 Connector 协议**——产出内容是假的，协议行为是真的。

- 规范与行为矩阵见 **[10-agent-stub-and-sdk.md](./10-agent-stub-and-sdk.md)**（协议清单、正常/拒收/崩溃/断线/重复 dispatch 五类行为、S1 验收用例）。
- 这样 Runtime Gateway、attempt 状态机、lease 取放与重建、event 幂等、resume 连续位点、outbox relay 全部被真实验证；换成真 Agent 时 Runtime 侧零改动。
- **不建议**「人类在界面手写产出」当作闭环验收：那条路径绕过 dispatch / attempt / lease / resume，等于没验证 Execution 域。
- 「真 Agent 由谁提供（fork 上游 / 用户自部署 / 其他）」仍是独立开放项，不因用 stub 而被决定。

## 3. 关键设计决策点（实施前必须确认）

（同 v0.4.2）

### 3.1 框架选型

```yaml
# v0.5 拍板（2026-09-10）：后台 = .NET，BullMQ 从 Current Design 移除
backend:
  runtime: .NET 10 LTS
  framework: ASP.NET Core (C#)
  orm: EF Core 10 (Npgsql) + 显式 SQL 迁移
  async: PostgreSQL transactional outbox + BackgroundService relay (SKIP LOCKED)
  test: xUnit + Testcontainers（PG/Redis 真实实例）

frontend:
  framework: Next.js + TypeScript
  ui: antd 6
  state: Zustand + TanStack Query
  realtime: WebSocket client

contracts:
  # 协议层技术中立：Connector envelope / REST 契约只定义语义，不绑定实现语言
  spec: OpenAPI 3.1 + JSON Schema
  codegen: 前端 TS 类型由 OpenAPI 生成；后端 C# DTO 由同一份契约校验
```

### 3.2 部署

（同 v0.4.2）

### 3.3 安全清单

（同 v0.4.2）

## 4. 仓库结构（v0.7 随 .NET 收口）

```
mateos/
├── apps/
│   ├── web/                  # Next.js 前端
│   └── api/                  # ASP.NET Core 单体（v0.5）
├── contracts/                # 协议 SSOT（v0.7：不再是 zod 实体）
│   ├── openapi/              # OpenAPI 3.1（REST 契约）
│   └── schemas/              # JSON Schema（Connector envelope / 领域 DTO）
│       ├── collaboration/
│       ├── execution/
│       ├── memory/
│       ├── work-item/
│       └── permission/       # 8 键 + propose_memory
├── src/                      # .NET solution
│   ├── MateOS.Api/           # 宿主 + 模块 + WS 中间件
│   ├── MateOS.Domain/        # 领域模型 / 不变量
│   ├── MateOS.Application/   # 用例 / Resolver / Execution 编排
│   ├── MateOS.Infrastructure/# EF Core + outbox relay + Redis + Provider
│   └── MateOS.Contracts/     # 由 contracts/schemas 生成的 C# DTO（校验一致性）
├── services/
│   └── agent-stub/           # S1 的 stub Agent（v0.7，见 detailed/10）
├── tests/
│   ├── MateOS.UnitTests/         # xUnit
│   ├── MateOS.IntegrationTests/  # xUnit + Testcontainers（PG/Redis）
│   └── e2e/                      # xUnit + WebApplicationFactory + Testcontainers
├── web-tests/                # 前端：Vitest（unit）+ Playwright（e2e）
├── docs/
│   ├── requirement/ / design/ / UI design/
└── .github/workflows/ci.yml
```

## 5. 测试策略（v0.7 随 .NET 收口）

| 层 | 工具 | 覆盖 |
| --- | --- | --- |
| 后端 单元 | **xUnit** | 领域逻辑 / 状态机 / 不变量 ≥ 80% |
| 后端 集成 | **xUnit + Testcontainers（PostgreSQL / Redis 真实实例）** | Repository / outbox relay / Lua 脚本 |
| 后端 E2E | **xUnit + `WebApplicationFactory` + Testcontainers** | 主链路（S1 ≤ 15 条） |
| 前端 单元 | Vitest（**仅 Next.js 前端**） | 组件 / hook |
| 前端 E2E | Playwright | P0/P5 主流程 |
| 契约 | OpenAPI schema validation（必要时 PactNet） | Client↔API / E4↔E7 |
| 压测 | k6 或 locust | 1k WS（S3 之后） |

> **v0.7 更正**：此前的 `pytest / vitest / locust / pact` 组合是 NestJS 时代的残留；Vitest **只保留给前端**，后端一律 xUnit 系。`zod schema` 不再作为契约事实源，契约 = `contracts/openapi` + `contracts/schemas`，TS 与 C# 均由此生成/校验。

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

## 6. 能力域清单（编号为域标签）

> **v0.6**：下面的 M 编号是**能力域标签**，不是实施顺序。实施顺序按 §1 的 **S1 → S2 → S3** 组织：
> **S1** 取本清单里 M1 / M2 / M3a / M3b / M4a（默认矩阵最小集）/ M4b / M7（主干）+ E10 基础；
> **S2** 取 M5 + M4a（propose / approve 路由）；
> **S3** 取 M6 + M7（WorkItem 关联）；
> **S1 之后按需** 取 M8 / M9。

### 6.1 阶段 1（M1-M3）—— 骨架

```
M1: E1 Identity & Workspace
  1. ASP.NET Core + EF Core + PG（v0.5）
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
  1. WSS Gateway（ASP.NET Core WebSocket 中间件，v0.5）
  2. hello / heartbeat / status / execution.dispatch_ack（v0.5 新增）
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
  2. Resolver Worker (outbox relay consumer，v0.5 改名)
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
  6b. v0.5：`type` 4 类 CHECK + 生成列 `scope_type`（PERSONAL|PROJECT）+ `search_text` 列与 GIN 索引；
      PERSONAL 行 `project_id` 为 NULL，检索按 scope 分支（不会跨 Project 漏读个人记忆）
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
  3. k6 或 locust 压测
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

## 7. DoD

**v0.6 改**：DoD 以**刀**为单位，不再以 M 为单位。

| 刀 | DoD（全部满足才算完成） |
| --- | --- |
| **S1** | 主链路 e2e ≤ 15 条全绿；`@mention → 产出回帖` 可重复跑通（含 stub Agent 的拒收 / 崩溃 / 重连分支）；audit + trace_id 贯穿；无人工在 DB 里手改状态 |
| **S2** | propose → 人审 → 检索 → dispatch 注入闭环；跨 Project 零泄漏（含"同 Team 不同 Project"用例）；PERSONAL 记忆跨 Project 可被自己读到 |
| **S3** | WorkItem CRUD + Built-in Provider + Work 页面；WorkItem ↔ Execution 双向可见；切 Provider 不丢历史 |

76 条全量 e2e 属 S3 之后的收尾目标（v0.4.3 制定的 76 条清单仍然有效，只是不再作为任一单刀的阻塞条件）。

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

-- webhook_inbox（v0.5：Jira webhook 收件箱，去重与持久化同一事务）
CREATE TABLE webhook_inbox (
  id            BIGSERIAL PRIMARY KEY,
  provider_key  TEXT NOT NULL,
  delivery_id   TEXT NOT NULL,
  webhook_id    UUID NOT NULL,
  binding_id    UUID NOT NULL,
  payload       JSONB NOT NULL,
  status        TEXT NOT NULL DEFAULT 'PENDING'
                CHECK (status IN ('PENDING','PROCESSED','DEAD')),
  attempt_count INT NOT NULL DEFAULT 0,
  last_error    TEXT,
  received_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  processed_at  TIMESTAMPTZ,
  UNIQUE (provider_key, delivery_id)
);

-- outbox_events（所有 epic 共用）
-- v0.5：权威 DDL 在 SYSTEM_DESIGN §5.2（此处不再重复列定义，仅登记迁移项）
--   关键差异：新增 status ∈ {PENDING,PUBLISHED,DEAD}；relay 用
--   WHERE status='PENDING' AND next_attempt_at <= now() ORDER BY next_attempt_at FOR UPDATE SKIP LOCKED 领取
```

## 10. 与其他设计的关系

- 详见所有 [01](./01-single-agent-task-lifecycle.md) - [08](./08-error-and-retry.md) 详细设计
- 详见 epic 文档 `docs/requirement/epic/`
