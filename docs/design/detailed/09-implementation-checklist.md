# Detailed Design · 09 · Implementation Checklist

> M1-M9 实施顺序、依赖、里程碑。
> 前置：所有详细设计 + epic docs

## 1. 里程碑

| 阶段 | 范围 | 依赖 | 预计工时 | 阻塞 |
| --- | --- | --- | --- | --- |
| **M1** | E1 Identity & Workspace | — | 5-7 天 | — |
| **M2** | E3 Channel & Messaging | M1（project_id, member） | 7-10 天 | M1 |
| **M3a** | E2 Agent Registry | M1（owner_user_id, project） | 5-7 天 | M1 |
| **M3b** | E7 Connector（基础） | M3a | 3-5 天 | M3a |
| **M4a** | E6 Authorization | M1（subject） | 5-7 天 | M1 |
| **M4b** | E4 Resolver | M3b（agent runtime）, M4a（permission） | 7-10 天 | M3b + M4a |
| **M5** | E5 Shared Memory | M2（channel）, M4a | 5-7 天 | M2 + M4a |
| **M6** | E8 Work Management Core | M1, M4a | 5-7 天 | M1 + M4a |
| **M7** | E7 Agent Execution Domain | M3b + M4b | 5-7 天 | M3b + M4b |
| **M8** | E10 Observability & Operations | 所有 | 7-10 天 | M1-M7 |
| **M9** | 集成测试 + 压测 | M1-M8 | 5-7 天 | M1-M8 |
| **V1+** | E9 Jira Provider | M6 | 5-7 天 | M6 |

**总计 MVP 估算**：~60-90 工作日

## 2. 依赖图

```
        M1 (E1)
        / | \
       /  |  \
      ↓   ↓   ↓
     M2   M3a M4a
     ↓    ↓    ↓
     ↓   M3b   ↓
      \  /  \ /
       ↓    ↓
       M4b (E4)
        |
        ↓
       M5 (E5)
        |
        ↓
       M6 (E8)
        |  \
        |   \→ V1+: E9
        ↓
       M7 (E7)
        |
        ↓
       M8 (E10)
        |
        ↓
       M9 (集成 + 压测)
```

## 3. 关键设计决策点（实施前必须确认）

### 3.1 框架选型

```yaml
backend:
  framework: NestJS (Node 22 LTS, TypeScript)
  orm: Prisma + PostgreSQL 16
  queue: BullMQ + Redis 7
  test: Vitest (单元) + pytest -m e2e (E2E)

frontend:
  framework: Next.js + TypeScript
  ui: antd 6
  state: Zustand + TanStack Query
  realtime: WebSocket client
```

### 3.2 部署

```yaml
mvp: Docker Compose（单主机）
production: K8s（V1+）

services:
  api: NestJS
  web: Next.js
  workers:
    - orchestrator (M4)
    - memory (M5)
    - runtime (M3b + M7)

infra:
  postgres: 1 primary + 1 replica
  redis: 1 primary + 2 replicas (cluster mode)
  object-storage: S3 / MinIO
```

### 3.3 安全清单

- [ ] 凭据 AES-256-GCM 信封加密（KMS）
- [ ] JWT 双令牌（access 15min / refresh 30d）
- [ ] Agent token 短期（V1: 30d，可手动 revoke）
- [ ] WS Origin 检查
- [ ] CORS 配置
- [ ] Rate limiting（login 5/min, message 60/min）
- [ ] Audit logs 全量（保留 2 年）
- [ ] OTel trace 全链路
- [ ] Sentry 错误追踪

## 4. 仓库结构

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
│   │   ├── permission/
│   │   └── provider/        # WorkManagementProvider interface
│   └── config/              # ESLint / tsconfig
├── services/
│   ├── orchestrator/        # E4 Resolver + Decision + 内部 API
│   ├── memory/              # E5 索引 worker
│   └── runtime/             # E7 Runtime Gateway
├── tests/
│   ├── e2e/                 # pytest -m e2e
│   ├── perf/                # locust 压测
│   └── unit/                # vitest
├── docs/
│   ├── requirement/         # PRD + epic
│   ├── design/              # SD + detailed
│   └── UI design/           # UI DS + tokens.css + 原型
└── .github/
    └── workflows/
        └── ci.yml
```

## 5. 测试策略

| 层 | 工具 | 覆盖目标 |
| --- | --- | --- |
| 单元 | Vitest | 业务逻辑 ≥ 80% |
| 集成 | Vitest + docker-compose | DB / Redis / S3 mock |
| E2E | pytest + 真实 API + PG | 关键用户流程 |
| 压测 | locust | 1k WS 并发，1000 mentions/min |
| 契约 | pact | E4 ↔ E7 协议、Client ↔ API |

### 5.1 E2E 套件（M9 落地）

每个 epic 有 5-13 个 E2E 测试（见各详细设计 §E2E 验收点）。汇总：
- 01-single-agent-lifecycle: 4 tests
- 02-interrupt-cancel: 8 tests
- 03-ws-resume: 6 tests
- 04-resolver-routing: 8 tests
- 05-memory-approval: 8 tests
- 06-workitem-sync: 10 tests
- 07-permission-orchestration: 8 tests
- 08-error-retry: 8 tests

**总计 ~60 个 E2E 测试**。

### 5.2 压测目标

| 指标 | 目标 |
| --- | --- |
| WS 并发连接 | 1000 个 Agent |
| Mention 解析 P95 | < 1s |
| Resolver 选 Agent P95 | < 500ms |
| tryAcquireSlot P95 | < 5ms |
| 消息流 WS 广播 P95 | < 300ms |
| Memory 检索 P95 | < 500ms |
| 1k Execution 创建/小时 | 0 错误 |

## 6. 实施顺序建议

### 6.1 阶段 1（M1-M3）—— MVP 骨架

```
M1: E1 Identity & Workspace
  1. NestJS + Prisma + PG 项目脚手架
  2. users / organizations / organization_members (v0.4.2 删 owner_id) / teams / team_members / projects / project_members
  3. /auth/* (register / login / refresh / logout)
  4. /me + 改密
  5. /orgs / /teams / /projects CRUD
  6. /projects/:id/members 邀请（含 Agent slot 占位，Agent 实体 M3 补）
  7. E2E 注册→登录→创建 Project→邀请成员

M2: E3 Channel & Messaging
  1. channels / channel_members / messages (5 形态, v0.4.1 projection) / channel_seq_counters
  2. seq 分配事务
  3. /channels/:id/messages (POST + GET + since_seq=)
  4. WS 网关 + message.created 广播
  5. resume(last_seq) 协议
  6. /attachments/presign + S3 直传
  7. P5 Channel 前端
  8. E2E send / receive / resume / attachment

M3a: E2 Agent Registry
  1. credentials / agents 表 (v0.4.2 无 active_slots) / agent_project_membership / agent_tokens
  2. /credentials + /agents CRUD
  3. /projects/:id/agents/:aid 邀请 (双批准)
  4. /agents/:id/pause / activate / disable (lifecycle)
  5. /agents/:id/tokens 签发
  6. P2 / P3 前端
  7. E2E agent lifecycle / double approval

M3b: E7 Connector (基础)
  1. WSS Gateway (NestJS)
  2. hello / heartbeat / status
  3. 触发 E2 lifecycle 变化 → activity 写库
  4. E2E connect / heartbeat
```

### 6.2 阶段 2（M4-M5）—— 协作核心

```
M4a: E6 Authorization
  1. permissions 表 + 7 键 + 3 态
  2. checkPermission (sync 纯函数)
  3. Redis 缓存 + pub/sub 失效
  4. PermissionGuard (NestJS)
  5. policy.evaluate 业务接口
  6. /permissions CRUD
  7. E2E default matrix / channel override / cache invalidation

M4b: E4 Resolver
  1. triggers / collaboration_requests / decision_records
  2. Resolver Worker (BullMQ)
  3. 能力排序算法
  4. Redis Lua tryAcquireSlot / releaseSlot
  5. 60s 超时重路由
  6. E4 → E7 内部 API: POST /internal/agent-executions
  7. /collab/:id/cancel
  8. P5 mention 胶囊 + 命中分数气泡
  9. E2E resolver rank / timeout / lifecycle pause / slot race

M5: E5 Shared Memory
  1. memory_proposals / memory_items / memory_chunks / memory_review_actions
  2. /memory-proposals CRUD (propose / approve / reject / edit-approve / withdraw)
  3. Source 三件套 DB CHECK
  4. Policy.evaluate('write_memory') 走 E5 流程
  5. 消息流 MEMORY_REQUEST projection
  6. P6 审批中心
  7. P7 Memory 文档
  8. 索引 worker (tsv)
  9. E2E propose / approve / reject / source / cross-project / personal
```

### 6.3 阶段 3（M6-M8）—— 完整执行域

```
M6: E8 Work Management Core
  1. work_items (v0.4.2 单表) / work_item_projections (deleted) / work_item_bindings
  2. work_management_connections (v0.4.2 org 级)
  3. WorkManagementProvider interface + BuiltInProvider
  4. ProviderRegistry
  5. routing: CREATE vs UPDATE
  6. 状态机迁移
  7. UNIQUE INDEX (work_item_bindings active)
  8. P-Work 页面
  9. P11 Settings (Work Management 入口)
  10. E2E CRUD / change provider no migrate / active unique

M7: E7 Agent Execution Domain (完整)
  1. agent_executions / execution_attempts / execution_events / execution_artifacts
  2. execution.dispatch 协议
  3. E4 → E7 内部 API
  4. event 流式 (STDOUT / PROGRESS / TOOL_CALL / LLM_TICK / ARTIFACT / ERROR)
  5. provider_event_id + UNIQUE(attempt_id, ...)
  6. result envelope + terminal CAS
  7. resume_request / resume_ack (v0.4.2 反向)
  8. attempt ≠ WS session
  9. E2E dispatch / event idempotency / resume / terminal CAS / cancel-result race

M8: E10 Observability & Operations
  1. audit_logs 全量触发
  2. notifications 通知中心
  3. OTel SDK 集成
  4. Prometheus metrics + Grafana
  5. @all 成本阈值
  6. locust 压测脚本
  7. P9 通知中心 / P10 审计
  8. E2E audit / notification / trace / load
```

### 6.4 阶段 4（M9）—— 集成 + 压测

```
M9: 集成 + 压测
  1. 1k WS 压测
  2. Mention 解析 P95 < 1s
  3. 端到端 60 个 E2E 全过
  4. 故障注入测试（DB 断、Redis 断、Provider 5xx）
  5. 安全审计
  6. 性能优化
  7. MVP 部署文档
```

### 6.5 阶段 5（V1+）—— Jira Provider

```
V1+: E9 Jira Provider
  1. JiraProvider 实现
  2. OAuth 3LO + token 加密
  3. webhook 注册 + refresh
  4. 双向同步 + 冲突检测
  5. E2E
```

## 7. 每个里程碑的"完成定义"（DoD）

| 阶段 | DoD |
| --- | --- |
| M1 | 5 个 E2E 全过；register → create project → invite member 流程跑通；PG schema 迁移成功；JWT 双令牌 + refresh rotation |
| M2 | 8 个 E2E 全过；send/receive/resume/attachment 跑通；WS 网关稳定；1k 并发消息 P95 < 300ms |
| M3 | 6+4 个 E2E 全过；Agent lifecycle × activity 6 态切换；token 签发 + revoke |
| M4 | 8+8 个 E2E 全过；Mention 解析 < 1s；slot race 0；cancel propagation 正确 |
| M5 | 8 个 E2E 全过；人审门禁；Source 三件套强约束；Personal / cross-project 隔离 |
| M6 | 10 个 E2E 全过；WorkItem CRUD；Provider 路由；active unique |
| M7 | 13 个 E2E 全过；end-to-end task lifecycle；resume；CAS；error retry |
| M8 | 5 个 E2E + 1k WS 压测 + 0 audit 遗漏 |
| M9 | 60 个 E2E 全过 + 1k WS P95 < 1s + 安全审计通过 |
| V1+ | 10 个 E2E + Jira 真实环境跑通 |

## 8. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
| --- | --- | --- | --- |
| Redis Cluster 部署复杂度 | 高 | 高 | 提前 P2 验证；Lua 脚本 key 必须带 hash tag |
| LLM Provider API 变化 | 中 | 中 | Provider Adapter 抽象；新 Provider 1 天接入 |
| WS 大规模并发性能 | 中 | 高 | M8 压测；网关水平扩展 |
| 用户接受度（业务层 workflow 改造） | 中 | 中 | P-Work 渐进推广；Built-in 默认 |
| Sandbox 执行（V3） | 低 | 低 | V1 暂不实现，架构预留 |

## 9. 启动方式

如确认按此计划开工：

1. **先做基础设施**：monorepo 脚手架 + Prisma + PG + Redis + S3 + Docker Compose
2. **M1（E1）开始编码**——按 6.1 节的 checklist
3. 每完成一个 M，按 DoD 验收
4. 每完成一个 M，commit + push + 触发 CI
5. 关键架构变更（capacity 原子化、resume 反向等）需在 commit 前 review

## 10. 与其他设计的关系

- 详见 [00-overview.md](./00-overview.md)（架构总览）
- 所有 M1-M8 的详细设计见 [01](./01-single-agent-task-lifecycle.md) - [08](./08-error-and-retry.md)
- Epic 文档见 `docs/requirement/epic/`
