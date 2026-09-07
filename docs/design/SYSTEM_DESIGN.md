# MateOS 系统架构设计（SYSTEM_DESIGN v0.1）

| 文档信息 | 内容 |
| --- | --- |
| 上游文档 | docs/requirement/MateOS-总体需求文档.md（PRD v0.2） |
| 文档状态 | Draft |
| 版本 | v0.1 |
| 日期 | 2026-09-07 |
| 前提 | **MateOS 为独立自洽系统**：协作、决策、Agent 运行时与执行能力均为自身组件，不依赖外部执行平台；对外只暴露开放协议（见 §8） |

---

## 1. 架构总览

### 1.1 分层视图

```
┌─────────────────────────────────────────────────────┐
│  Client（Web / Desktop）                            │
│  Next.js SPA — REST + WebSocket                     │
└───────────────┬─────────────────────┬───────────────┘
                │ HTTPS /api/v1        │ WSS /ws
┌───────────────▼─────────────────────▼───────────────┐
│  Application 层（NestJS Modular Monolith）           │
│  auth │ org/team/project │ channel/message │ mention│
│  memory │ permission │ agent-registry │ task        │
└───────┬──────────────┬───────────────┬──────────────┘
        │               │               │
┌───────▼──────┐ ┌──────▼───────┐ ┌─────▼─────────────┐
│ Orchestrator │ │ Memory       │ │ Agent Runtime     │
│ Worker       │ │ Worker       │ │ Gateway + Sandbox │
│ (Resolver/   │ │ (索引/检索/   │ │ (Connector 协议、  │
│  Decision)   │ │  门禁)       │ │  容器沙箱)         │
└───────┬──────┘ └──────┬───────┘ └─────┬─────────────┘
        └───────────────┼───────────────┘
                        │
      ┌─────────────────▼──────────────────┐
      │  基础设施：PostgreSQL16(+pgvector)   │
      │  Redis 7 │ S3/MinIO │ Docker       │
      └────────────────────────────────────┘
```

### 1.2 演进策略：模块化单体优先

**MVP 不做微服务**。单体（NestJS 多模块）+ 独立 Worker 进程，按模块边界组织代码；触发以下任一条件再拆分：

- WebSocket 并发连接 > 5k → ws-gateway 独立部署
- Memory 索引/检索成为 CPU 热点 → memory-worker 独立扩容
- 团队 > 8 人且模块所有权清晰 → 按 §3 边界拆服务

理由：MVP 的瓶颈是交付速度和模型迭代，不是流量；单体 + 明确模块边界可以在不推翻架构的前提下平滑拆分。

---

## 2. 技术选型

| 领域 | 选型 | 理由 | 备选 |
| --- | --- | --- | --- |
| 前端 | **Next.js + TypeScript + antd 6** | 团队现有技术栈（Next16+antd6 经验），SSR 需求低但生态完整 | React SPA + Vite |
| 前端状态 | **Zustand + TanStack Query** | 轻量；Query 管服务端状态与 WS 缓存同步 | Redux Toolkit |
| 后端框架 | **NestJS (Node 22 LTS, TypeScript)** | 与前端同语言，DTO/协议类型可共享 monorepo 包；模块化天然匹配单体演进；Guard/Interceptor 匹配 Permission Model | FastAPI（Python）、Go chi（网关性能更强但开发效率低） |
| ORM | **Prisma** | Schema 即文档、迁移工具链成熟 | Drizzle（更轻，可作替代） |
| 主数据库 | **PostgreSQL 16** | 关系模型 + JSONB + 全文检索 + **pgvector** 一个库覆盖四类需求；MVP 少一个组件少一份运维 | MySQL+独立向量库 |
| 向量检索 | **pgvector**（ANN, cosine） | Memory 检索 MVP 体量（<1M chunks）足够；避免早期引入 Qdrant/Milvus | Qdrant（>10M 或需混合检索重排序时迁移） |
| 缓存/实时 | **Redis 7** | presence、pub/sub、限流、锁、timeline 缓存、任务队列（BullMQ）一站搞定 | KeyDB |
| 任务队列 | **BullMQ**（基于 Redis） | 单体内异步任务（Resolver、Decision 超时、Memory 索引）够用；带重试/延迟/限流 | RabbitMQ（跨系统多消费者时再引入） |
| 对象存储 | **S3 兼容（MinIO 自托管 / 云 S3）** | 附件、导出文件；预签名 URL 直传 | — |
| LLM 接入 | **Provider Adapter（OpenAI-compatible 统一网关）** | Credential 分离（PRD §4.3）；一行配置切换 OpenAI/Anthropic/Gemini | LiteLLM 代理 |
| 沙箱执行 | **Docker 容器隔离**（V3 启用） | Code Execution Non-Goal 直到 V3，但架构预留：gVisor/Firecracker 可后插 | — |
| 部署 | **Docker Compose（MVP）→ K8s** | MVP 单机/单云主机起步 | — |
| 可观测 | **OpenTelemetry + Prometheus + Grafana + Loki** | 标准 OTel 埋点，后端/WS/Worker 统一 trace | — |
| CI/CD | **GitHub Actions** | — | — |

---

## 3. 服务拆分

MVP 部署形态 = **1 个单体 + 3 个 Worker + 1 前端**，同一 monorepo（pnpm workspace / Turborepo）：

```
apps/
  web/            # Next.js 前端
  api/            # NestJS 单体（含 ws 模块）
packages/
  contracts/      # DTO/事件/协议类型 + zod schema（前后端共享）
  config/         # eslint/tsconfig 等
services/         # 从 api 拆出的独立进程（同一镜像不同启动参数）
  orchestrator/   # Mention Resolver、Decision 状态机、@all 仲裁
  memory/         # Memory 索引(embedding)、检索、审批流超时
  runtime/        # Agent Runtime Gateway（见 §6）
```

### 3.1 模块职责边界

| 模块 | 职责 | 不负责 |
| --- | --- | --- |
| auth/iam | 注册登录、JWT、org/team/project 成员管理 | Channel 级权限（归 permission） |
| channel | Channel CRUD、成员邀请、消息读写、已读、seq 分配 | Mention 解析（归 orchestrator） |
| mention | Mention 存储、@all 群发 | 候选排序与路由（归 orchestrator） |
| orchestrator | Mention Resolver 流水线、Decision 状态机、Delegate 路由、超时重路由 | 执行任务 |
| memory | 四类 Memory 的 CRUD、人审门禁、Source 溯源、embedding 索引与检索 | 消息存储 |
| permission | 权限矩阵存储与判定（sync API，供所有模块调用） | 权限的 UI 配置 |
| agent-registry | Agent CRUD、Credential 绑定、状态聚合（presence） | Agent 任务执行 |
| runtime | Connector 协议接入、任务下发、心跳保活、沙箱生命周期 | 业务决策 |
| task | 任务/子任务模型（V1 最小实现，V2 全量） | 代码执行（归 runtime sandbox） |

---

## 4. 核心流程设计

### 4.1 Mention Resolver 流水线（异步化）

```
用户发送 @backend-team 请审查支付 API
  ↓ api/channel 模块：消息落库（同步），生成 mention 行（status=RESOLVING）
  ↓ 入 BullMQ 队列 mention.resolve
  ↓ orchestrator Worker：
    ① 硬过滤：channel 成员资格 + permission（read/write）
    ② 候选排序：capability 匹配度（规则分） + 当前负载（status!=BUSY 加分）
       + 近期相关性（该 agent 在本 project 近 30 天被 accept 的比例）
    ③ 产出 Top-N，mention 行更新为 RESOLVED(targets=[...])
  ↓ 逐个向候选成员投递 decision 请求（WS 实时 + 队列兜底）
```

要点：**消息发送永远同步成功**（Mention 解析失败不影响消息送达）；解析结果通过 WS 事件 `mention.resolved` 回推前端高亮。

### 4.2 Decision 状态机

```
                    ┌──────────────────────────────┐
 mention.resolved ──► PENDING ─┬─► ACCEPTED ──► 创建 Task（V2 起进入 runtime 执行）
                               ├─► REJECTED（必须带 reason）
                               ├─► NEED_CONTEXT（列出 needs[]，阻塞等待补充）
                               └─► DELEGATED（V2；delegate_to 必须是合法成员，形成委托链）
 超时策略：PENDING 超 60s 未响应 → 取下一个候选重新投递；全部超时 → mention.status=UNRESOLVED，
 通知发起人
```

所有 Decision 落 `decision_records` 表（谁、对什么请求、什么决策、依据），这是审计与后续"Agent 表现评分"的数据底座。

### 4.3 Memory 写入门禁

```
Agent 产生 memory proposal（type/content/source 引用 channel_message id）
  ↓ permission 检查：write memory = request → 创建 memory_items(status=PROPOSED)
  ↓ WS 推送给 project 内有 approve 权限的 Human
  ↓ Human Approve → status=APPROVED，入队 memory.index → 切块 + embedding 写 memory_chunks
    Human Reject → status=REJECTED（保留记录，供 Agent 学习"什么不该提议"）
```

检索（V2）：`hybrid = pgvector ANN(top_k=50) + PG 全文(BM25 近似) → 按 project_id/type 过滤 → 轻量 rerank`；Personal Memory 只对 owner 生效，在检索层做成员级过滤。

---

## 5. 数据模型（PostgreSQL）

### 5.1 表清单与关键字段

| 表 | 关键字段 | 说明 |
| --- | --- | --- |
| users | id, email, password_hash, display_name | — |
| credentials | id, user_id, provider, secret_encrypted, meta | **加密存储（AES-256-GCM，KMS/信封加密）**，永不回显明文 |
| teams | id, org_id, name | — |
| projects | id, team_id, name, repo_url | 知识边界 |
| channels | id, project_id, name, last_seq | 通信边界 |
| agents | id, owner_user_id, credential_id, name, role, capabilities(jsonb), can_execute, can_review, status | 状态为缓存值，真源在 Redis presence |
| channel_members | channel_id, member_type(HUMAN\|AGENT), member_id, joined_at | 多态成员；唯一索引(channel_id, member_type, member_id) |
| messages | id, channel_id, seq, sender_type, sender_id, content, content_type, client_msg_id, created_at, deleted_at | **seq 为 channel 内单调递增**（用 channel 计数器表 + 事务分配），前端按 last_seq 断线续传；client_msg_id 唯一索引幂等去重；**按月分区** |
| mentions | id, message_id, mention_type(USER\|AGENT\|GROUP\|ALL), raw_text, status(RESOLVING\|RESOLVED\|UNRESOLVED), targets(jsonb) | — |
| decision_records | id, mention_id, agent_id, decision(ACCEPT\|REJECT\|NEED_CONTEXT\|DELEGATE), reason, needs(jsonb), delegate_to, expires_at, decided_at | Decision 状态机真源 |
| memory_items | id, project_id, owner_user_id(nullable), type(PERSONAL\|PROJECT\|DECISION\|KNOWLEDGE), content, status(PROPOSED\|APPROVED\|REJECTED), source_type, source_id, approved_by, created_at | Source 溯源（PRD FR-7） |
| memory_chunks | id, memory_id, chunk_text, embedding vector(1536), tsv | pgvector HNSW 索引(embedding)；tsv 为全文列 |
| permissions | id, scope_type(PROJECT\|CHANNEL), scope_id, subject_type, subject_id, perm_key, effect | 权限矩阵（§7 Permission Model） |
| tasks | id, project_id, title, status, created_from(decision_record_id), assignee_type, assignee_id | V1 最小：Accept 后可建任务占位；V2 接 runtime 执行 |
| attachments | id, message_id, s3_key, size, mime | — |
| audit_logs | id, actor_type, actor_id, action, target, detail(jsonb), created_at | append-only |

### 5.2 关键设计决策

- **Member 多态**：HUMAN/AGENT 统一为 `(member_type, member_id)` 二元组，避免双表 join 泛滥；在 packages/contracts 里封装 `MemberRef` 类型。
- **消息不可变**：编辑走 `message_edits` 版本表（V2），删除为软删；一切下游（Memory source、mention）通过 message id 引用，永不悬空。
- **seq 分配**：`channel_seq_counters(channel_id, next_seq)` 行级锁 + 事务，保证 channel 内严格有序，这是 WS `resume(last_seq)` 的基础。
- **检索过滤前置**：memory_chunks 必须带 project_id（冗余自 memory_items），向量检索时先按 project_id 过滤再 ANN，避免跨项目泄漏。

---

## 6. Agent Runtime 与 Connector 协议

MateOS 自研 Agent 运行时，采用 **出站连接（Agent 主动连入）** 模式：

```
外部/本地 Agent 进程（Coding-Agent、Review-Agent…）
        │  WSS 出站连接 + JWT(agent token)
        ▼
Agent Runtime Gateway（services/runtime）
  ├─ 注册/心跳：agent 上报 status(FREE/BUSY/THINKING/WAITING_CONTEXT/OFFLINE)
  ├─ 任务下发：Gateway → agent 的 dispatch 消息（见协议）
  ├─ 流式回传：agent 执行进度/结果/产物（diff、文件、日志）经 Gateway 广播到 Channel
  └─ 保活判定：心跳 >90s 丢失 → presence=OFFLINE
```

### Connector 协议（JSON envelope over WebSocket）

```jsonc
// 通用 envelope
{ "v": 1, "type": "hello|heartbeat|status|dispatch|progress|result|error",
  "id": "uuid", "ts": 0, "payload": { } }

// dispatch（任务下发）
{ "type": "dispatch", "payload": {
    "task_id": "…", "channel_id": "…",
    "context": {                       // Project 知识边界注入
      "memory_refs": ["mem:…"],        // 检索命中的 memory id（含 source）
      "recent_messages": ["msg:…"],    // channel 上下文窗口
      "permissions": { "can_execute": true, "can_review": false }
    },
    "deadline_s": 600, "idempotency_key": "…" } }

// decision（Agent 对 mention 的回应，同 §4.2 状态机）
{ "type": "result", "payload": {
    "task_id": "…", "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason": "…", "needs": ["api spec", "db design"], "delegate_to": "agent:…" } }
```

设计原则：

- **Agent 无入站端口**：所有连接由 Agent 侧发起，天然穿透 NAT/防火墙，本地 Agent 与云端 Agent 统一接入。
- **上下文注入走引用**：Gateway 按 dispatch 中的 memory_refs/messages 拉取内容注入 prompt，Agent 侧不做 DB 直连——Project 知识边界由服务端强制。
- **沙箱隔离**（V3）：can_execute=true 的任务在一次性 Docker 容器执行，资源限额（CPU/内存/网络/时长），产物只经 S3 回传。

---

## 7. Permission Model 实现

- 权限键：`read_message / write_message / write_memory / execute_code / create_pr / approve_memory / manage_channel`
- 判定为**同步纯函数**：`check(subject: MemberRef, perm: PermKey, scope: ScopeRef) → ALLOW | REQUEST | DENY`，NestJS Guard + 模块内直接调用，不跨网络。
- 存储三层覆盖：**默认矩阵（代码内置）→ Project 覆盖 → Channel 覆盖**，合并顺序 Channel > Project > 默认。
- 缓存：合并结果按 `(scope, subject)` 维度缓存 Redis（TTL 5min + 事件失效），变更时发 `perm.changed` pub/sub 精确失效。

---

## 8. 协议汇总

| 边界 | 协议 | 说明 |
| --- | --- | --- |
| Client ↔ API | HTTPS REST `/api/v1`（JWT Bearer） | CRUD 与查询；分页 cursor-based |
| Client ↔ Gateway | **WSS + JSON event envelope** | 事件：`message.created / message.seq_sync / mention.resolved / decision.proposed / agent.status_changed / memory.proposed / presence.updated`；连接级 `resume(last_seq)` |
| 内部异步 | **BullMQ（Redis）** | mention.resolve / memory.index / decision.timeout 等队列；死信队列 + 告警 |
| 内部广播 | **Redis Pub/Sub** | WS 多实例 fanout、权限失效事件 |
| Agent ↔ Runtime | **WSS Connector 协议**（§6） | 对外开放、版本化（v1），第三方 Agent 可按协议接入 |
| LLM 调用 | OpenAI-compatible HTTP | Provider Adapter 统一，流式 SSE 转发 |
| 对象存储 | S3 API（预签名 URL 直传/下载） | 服务端只发票据不代理大流量 |

WS envelope 规范：所有事件含 `{v, type, id, ts, payload}`；客户端 ACK 按 channel 维度推进 `last_seq`；服务端事件幂等（事件 id 去重窗口 5min）。

---

## 9. 缓存设计（Redis）

| 用途 | Key 模式 | 类型 | TTL | 失效策略 |
| --- | --- | --- | --- | --- |
| Agent presence | `presence:{agent_id}` | Hash(status, since, node) | 90s（心跳续期） | 心跳过期自动失效 |
| Agent 状态广播 | `pubsub:presence` | Pub/Sub | — | 变更即发，WS 层推前端 |
| Channel 近期消息 | `timeline:{channel_id}` | ZSet(message_id, seq) | 10min | 新消息 ZADD；LRU 淘汰 |
| 未读计数 | `unread:{member_id}:{channel_id}` | String(INCR) | 24h | 已读事件 DEL + 异步落库 |
| 权限矩阵 | `perm:{scope_type}:{scope_id}:{subject_type}:{subject_id}` | Hash | 5min | `perm.changed` 事件精确失效 |
| Mention 排序特征 | `mfeat:{project_id}:{agent_id}` | Hash(accept_rate, load) | 1h | decision 落库后增量更新 |
| 限流 | `rl:{principal}:{route}` | Sorted Set 滑窗 | 窗口期 | — |
| 分布式锁 | `lock:{resource}` | SET NX PX | ≤30s | Memory 审批、seq 相关临界区 |
| Session/刷新令牌 | `rt:{user_id}:{jti}` | String | 30d | 登出/吊销 DEL |
| Memory 热点上下文 | `mctx:{project_id}:{agent_id}` | String(JSON) | 15min | memory_items APPROVED 事件失效 |

原则：**Redis 只放可重建数据**（presence、计数、缓存、锁），持久真源一律在 PG；缓存 miss 的重建路径必须存在且幂等。

---

## 10. 数据库与存储设计

### PostgreSQL

- **版本/拓扑**：PG16 单实例（云 RDS 或自建+流复制），MVP 不上分库分表；预留逻辑库 `mateos`。
- **分区**：`messages` 按月声明式分区（`PARTITION BY RANGE(created_at)`），默认建未来 3 个月分区，pg_cron 自动预建。
- **索引要点**：`messages(channel_id, seq)` 唯一；`messages(channel_id, created_at desc)` 时间线；`memory_chunks` HNSW(vector_cosine_ops)；`mentions(message_id)`；`decision_records(mention_id)`。
- **全文检索**：消息与 memory 的 tsvector 列 + GIN 索引，中文用 `zhparser`/`pg_jieba` 扩展（部署镜像内置）。
- **备份**：每日全量快照 + WAL 连续归档（PITR）；恢复演练纳入季度例行。
- **保留策略**：消息永久（软删）；`audit_logs` 2 年；REJECTED 的 memory_items 保留 1 年（供 Agent 学习）。

### 对象存储（S3/MinIO）

- 桶规划：`mateos-attachments`（附件）、`mateos-artifacts`（V3 执行产物）、`mateos-backups`（逻辑导出）。
- 上传走预签名 URL 直传，服务端校验 mime/大小（≤50MB）；下载预签名 15min。

### Redis

- 独立实例，`maxmemory-policy=volatile-lru`（仅缓存键参与淘汰，锁/计数键不过期淘汰）。
- AOF everysec 持久化（锁与计数可承受秒级丢失）。

---

## 11. 非功能设计

| 维度 | 方案 |
| --- | --- |
| 可用性 | MVP：单可用区，RDS 多可用区部署，API ≥2 副本 + Nginx/LB；目标 99.5% |
| 扩展性 | WS 网关无状态化（presence 走 Redis，跨节点 fanout 走 pub/sub）→ 水平加节点 |
| 安全 | JWT 双令牌；Credential AES-256-GCM 信封加密；Agent token 独立签名域；全部写操作审计；速率限制（登录 5/min，消息 60/min） |
| 成本 | LLM 调用按 credential 归属 user 计量（llm_calls 表：tokens/耗时/成本），按用户/项目报表 |
| 可观测 | OTel trace 贯穿 API→Worker→Runtime；每条消息带 trace_id；Grafana 面板：WS 连接数、Resolver P95、Decision 超时率、Memory 审批积压 |

---

## 12. 实施顺序（对齐 PRD MVP）

| 阶段 | 交付 | 对应 PRD |
| --- | --- | --- |
| M1 基座 | monorepo 脚手架、auth/JWT、org/team/project/channel CRUD、PG+Redis 部署 | MVP: User/Team/Project/Channel |
| M2 通信 | 消息收发（seq/幂等/分区表）、WS 网关与 resume、附件直传 | MVP: Chat |
| M3 成员与 Agent | Agent CRUD、Credential 加密、presence、channel 邀请 | MVP: Agent/成员 |
| M4 Mention | mention 存储、Resolver Worker、Decision 状态机（Accept/Reject/Need Context，Delegate 留枚举）、权限 Guard | MVP: Mention/Decision/Permission |
| M5 打磨 | @all 仲裁、通知、审计、监控面板、压测（1k WS 并发） | MVP 收尾 |
| V2 起 | Memory 门禁与检索、Delegate 路由、Runtime 任务执行 | PRD V2 |

---

## 13. 开放问题

- [ ] 中文分词扩展选型（zhparser vs pg_jieba）需在 M2 前用真实语料对比
- [ ] Agent token 的签发/吊销生命周期（设备绑定 vs 长期密钥）
- [ ] WS 消息压缩阈值（permessage-deflate 开销 vs 收益）
- [ ] embedding 模型与维度（1536 起，换模型需重建索引，预留 version 字段）
- [ ] Decision 超时 60s 的默认值需用真实使用数据校准

---

## 14. 更新记录

| 版本 | 日期 | 变更 |
| --- | --- | --- |
| v0.1 | 2026-09-07 | 初稿：独立系统架构（NestJS 单体 + 3 Worker）；技术选型、模块拆分、Mention Resolver/Decision/Memory 流程、PG+pgvector 数据模型、Redis 缓存矩阵、Connector 开放协议、实施顺序 |
