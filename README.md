# MateOS

> AI-native team operating system where humans and AI teammates collaborate through shared channels, memory, and work.

MateOS 是一个面向软件开发团队的 AI 原生团队协作平台（V1 = AI Engineering Team Workspace）。人类成员与 AI Agent 成员在共享 Channel 中沟通、推进工作、沉淀记忆；人类始终握有闸门，且**只在需要判断、授权或补充信息时才被要求介入**。

## Current Design Baseline

| 项 | 内容 |
| --- | --- |
| Last updated | 2026-09-11 |
| Product | **MateOS V1 = AI Engineering Team Workspace** |
| Backend | ASP.NET Core / .NET 10 |
| Frontend | Next.js + TypeScript |
| Storage | PostgreSQL 16 + pgvector |
| Async | PostgreSQL transactional outbox（`SKIP LOCKED` relay） |
| Cache / Presence | Redis 7 |
| Execution | MateOS Agent Runtime（自研，永久自洽） |
| Implementation | **S1 → S2 → S3 vertical slices** |
| Stage | **S1+S2+S3 后端主链路已落地 + M3b Phase 2/3 + 协议 SSOT**（M1+M2+M2-WS+M3a+M3b+M4a+M4b+M7+E5+E6 + Needs You + Memory 写链路 + E8 Work Delivery + E7 dispatch 实时推送 + Agent 侧 CR 收单 / 决策 / resume 续传 + `contracts/` schema 校验）：最近一笔 `10fb1d5`，**643 测试全过（572 单元 + 71 集成）**；User Promise「@Agent → 工作 → 记忆 → 审批」+「WorkItem → Agent 推进 → 产出回流」双闭环。**S1 DoD（`detailed/09` §7）剩余硬缺口：`services/agent-stub` + `tests/e2e`** |

> 本表是唯一需要维护的「技术基线」。**不要在此堆叠文档版本号**：PRD / SYSTEM_DESIGN / UI DS 的版本只在各自文件头与更新记录里维护；detailed / epic / 原型不单独发行版本号，用日期 + commit 追溯。避免出现「文件头 v0.5、changelog v0.7、commit v0.8」这类交叉版本噪音。

## Product Promise

> **把工作交给 AI 团队，MateOS 负责持续推进；只有需要人的判断、授权或补充信息时才打扰用户。**

三条 Primary User Journey（完整定义见 PRD §1.2 / §1.3）：

| Journey | 用户看到的一句话 |
| --- | --- |
| **Ask the Team** | 在 Channel 提出需求 → @Agent → 合适的 Agent 接手 → 持续工作 → 结果回到原 Channel |
| **Needs You** | Agent 遇到需要人的判断 → 说明发生了什么 / 为什么需要你 / 建议怎么做 / 影响什么 → 你一键决策 → Agent 自动继续 |
| **Deliver Work** | WorkItem → Agent 接手 → 执行 → 产出 → Work 向前推进 |

**唯一的产品体验约束**：

> 用户永远不需要理解 CollaborationRequest / Execution / Attempt / Event / Lease / Provider / fencing 才能完成操作。
> 系统内部可以越来越专业；**界面应该随着系统能力增强反而越来越简单**。

## 用户可见的信息架构（MVP 冻结）

```
MateOS
├── Needs You        ← 默认入口：Decision / Information / Approval / Problems
├── Channels         ← 消息流 + Topics
├── Work             ← Work List + Agent-native Work Detail
├── Team             ← Agent 列表 + Agent Detail（用户层 / 诊断层分离）
└── Settings         ← Project / Members / Work Management / Credentials / Policies
```

- **Memory 不是一级入口**：从 Project / Channel / Work / Agent 上下文进入。
- **没有独立的 Approval Center**：Memory / Work / Permission 审批统一收进 `Needs You → Approval`。
- **Agent-centric ≠ Fleet-centric**：领域主语是 Agent，但用户入口由 Human Attention 驱动。

## Single Source of Truth

- **Current Design 只有两处**：`docs/requirement/MateOS-总体需求文档.md`（PRD）+ `docs/design/SYSTEM_DESIGN.md`。其他一切（detailed / epic / 原型 / domain-model）都是它们的展开，**冲突时以这两份为准并回改子文档**。
- `docs/design/detailed/00–10` = Current implementation spec。
- `docs/design/future/` = **V2 愿景，不作 S1–S3 依据**（`autonomous-delivery/` 已迁入并冻结）。
- `docs/spec/domain-model-v0.3.md` = 领域模型 + 不变量，并含 **User-facing Vocabulary**（内部模型 ↔ 用户语言）。
- `docs/review/` = 评审结论与待拍板项，不是 spec。
- **`contracts/` = wire 层事实源**（JSON Schema + 待落的 OpenAPI）。它与上面两份**不同层级**：
  PRD / SYSTEM_DESIGN 管「语义该是什么」，`contracts/` 管「字节长什么样」。
  两份设计文档在 wire 形状上互相矛盾时，裁决记录在 `contracts/README.md` §4（含逐项依据），
  并有一条测试把 C# 契约记录与 schema 对拍——**不要把这类裁决散回散文里**。

## 文档结构

```
docs/
├── requirement/
│   ├── MateOS-总体需求文档.md        # PRD（单一事实源；含 User Promise / Journey / FR-10 Needs You）
│   └── epic/                         # E1–E10 Epic（PRD 的展开）
├── design/
│   ├── SYSTEM_DESIGN.md              # 单一事实源（含 UI Projection & Human Attention Model）
│   ├── MateOS-architecture.html      # 架构图（只读展示）
│   ├── detailed/                     # Current implementation spec
│   │   ├── 00-overview.md … 09-implementation-checklist.md
│   │   └── 10-agent-stub-and-sdk.md  # S1 的 stub Agent + SDK 契约
│   └── future/                       # V2 愿景，不作 S1–S3 依据
├── spec/
│   └── domain-model-v0.3.md          # 领域模型 + 不变量（I1–I10）+ 用户词表
├── review/                           # 评审结论与待拍板项
└── UI design/
    ├── MateOS-UI-Design-System-v0.2.md  # DS（文件名保留 v0.2）
    ├── tokens.css                    # 设计令牌单一事实来源
    ├── P0-inbox.html                 # Needs You（默认入口）
    ├── P0-team.html                  # Team（Agent 列表）
    ├── P3-agent-card.html            # Agent Detail（用户层 / 诊断层）
    ├── P4-project-dashboard.html     # PO Delivery Dashboard
    ├── P5-channel-prototype.html     # Channel 主界面
    ├── P5b-topic-channel.html        # Topic 线程
    └── P-work.html                   # Work List + Work Detail
```

## 核心概念

- **Organization → Team → Project → Channel → Member(Human|Agent)** — 五层实体模型
- **Channel = communication boundary, Project = knowledge boundary** — 通信与知识边界分离
- **Agent 三维状态**（内部概念，UI 用颜色 + 文字徽标双编码）：`lifecycle(ACTIVE/PAUSED/DISABLED) × activity(6 态) × health(HEALTHY/DEGRADED/UNHEALTHY)`
- **Mention Resolver** — `@成员 / @能力组 / @all`；候选 = `lifecycle=ACTIVE ∩ 有空闲 slot ∩ 有效权限 ALLOW`（**activity 不参与调度**）
- **Decision** — `Accept / Reject / Need Context`（MVP）。**Delegate 与自动 Review 链 = Future**，V1 不在原型里假装已实现
- **Shared Memory 人审门禁** — Agent 申请（`propose_memory`）→ 人类批准（`approve_memory`）→ 入共享记忆；每条记忆必带 Source 溯源
- **执行层永久自洽** — 只有 MateOS Runtime；与 AgentBoard 集成永久移出 scope（PRD §8）。Work Management 是**独立 Provider 域**（Built-in 默认；Jira = V1+ 可选），**不是执行后端**
- **Human Attention 是 read projection** — Needs You 由 CR.NEED_CONTEXT / MemoryProposal.PENDING / Execution 失败 / Agent 异常 / WorkItem 阻塞 / 预算阈值汇聚而成，**不是新的事实源**

## 技术选型

- 前端：Next.js + TypeScript + antd 6 + Zustand + TanStack Query
- 后端：**ASP.NET Core（.NET 10 LTS, C#）** — 模块化单体 + `BackgroundService` relay
- ORM / 迁移：EF Core（Npgsql）+ 显式 SQL 迁移
- 数据库：PostgreSQL 16 + pgvector
- 缓存：Redis 7（presence / pub/sub / 限流 / slot lease）
- 异步：**PostgreSQL transactional outbox + `SKIP LOCKED` relay**（无独立 MQ）
- 契约：**JSON Schema（`contracts/schemas/`）已落地且可执行校验**（`ConnectorContractTests` 把 C# 契约记录序列化后喂给 schema，分叉即测试失败）；
  OpenAPI 3.1（`contracts/openapi/`）与 TS 类型生成**待落地**，取舍见 `contracts/README.md` §5
- 可观测：S1 起只做结构化日志 + trace_id + audit + `/metrics`；OTel + Prometheus + Grafana + Loki 推后
- 沙箱 / 部署：Docker（V3 启用）；Docker Compose → K8s

> 协议层（Connector / REST / envelope）保持**技术中立**：只定义语义，不绑定实现语言。

## 实施顺序（S1 → S2 → S3）

| 刀 | 用户看到的价值 | 覆盖能力域 | 状态 |
| --- | --- | --- | --- |
| **S1 — Ask the Team** | 在 Channel 里 @Agent，Agent 接手、工作、把结果回到原 Channel | E1/E3/E2/E7（transport + Execution 主干）/E4/E6 最小/E10 基础；Agent 端用 **stub** | ✅ **主链路已落地** |
| **S2 — Shared Knowledge** | Agent 申请记忆 → 人审 → 之后的 Agent 自动用上这条知识 | E5 + E6（propose/approve）+ Needs You → Approval | ✅ **业务闭环已落地** |
| **S3 — Work Delivery** | 建 WorkItem → Agent 接手推进 → 产出与状态回流到 Work | E8 + Built-in Provider + Work 页面 + WorkItem↔Execution | ✅ **主链路已落地**（Work 前端页面待做） |
| V1+ | Jira（Work Management Provider） | E9 | ⏳ |

**S1+S2 累计 11 个 feat commit**（远端 main）：

| Commit | 能力域 | 单测 | 主要内容 |
| --- | --- | --- | --- |
| `674affe` | M1 Identity & Workspace | 140 | 14 端点：register/login/refresh + org/team/project 5 层实体 + audit/trace |
| `893ee08` | M2 Channel HTTP | 204 (+64) | 13 端点 + 5 形态 projection + 客户端幂等 + seq 分配 |
| `7db3b1a` | M2-WS | 242 (+38) | /ws 鉴权 + subscribe + resume + 推 message.created + heartbeat watchdog |
| `cac0247` | M3a Agent Registry | 298 (+56) | credentials AES-GCM + agents + tokens + project membership + activity 6 态 |
| `85dd6f3` | M3b Connector | 343 (+45) | agent_token JWT 鉴权 + dispatch inbox + events cursor + result CAS 终态 |
| `16f0519` | M4a Authorization | 388 (+45) | 8 perm_key + 3 态 effect + 三层合并（Channel > Project > Default） |
| `303c7e7` | M4b Routing | 429 (+41) | triggers + CR 6 态 + Decision 4 态 + ACCEPT 同步调 E7 创建 Execution |
| `0e89ce2` | M7 Outbox | 445 (+16) | transactional outbox + relay worker + 指数退避 + capacity invariant |
| `c546144` | S2 Memory | 485 (+40) | memory_proposals + memory_items + 4 类型 + Source 三件套 + 全文检索 |
| `9f78430` | S2 Needs You 收口 | 495 (+10) | 跨 4 事实源聚合 timeline（INFORMATION/APPROVAL/DECISION/PROBLEMS） |
| `bfd39dc` | S2 Memory 写链路 | 495 | Memory Proposal 申请/审批/拒绝时写 channel message（MEMORY_REQUEST + SYSTEM）+ WS 推 |

**S3 本轮改动（工作区，未提交）**：`008_work_item.sql` 迁移 + `MateOS.Domain/Work/`（类型 / 状态机 / Provider 抽象 + Registry）
+ `MateOS.Api/Work/`（BuiltInProvider / 13 端点 / WorkDelivery）+ outbox 回流 + 45 个新单测 + 10 条 E8 集成测试；
单测 495 → **541**。

**S1 跑通端到端链路**（参考 `docs/design/detailed/01-single-agent-task-lifecycle.md` + `detailed/03-ws-connection-and-resume.md`）：

```
User @Backend ──POST /channels/{id}/messages──▶ E3 落库 + 推 message.created
                │
                └─▶ E3 mention 提取 ──▶ POST /internal/triggers ──▶ E4 写 trigger + CR (PENDING)
                                                                 │
                                                                 └─▶ E4 ACCEPT 同步调 E7 创建 Execution
                                                                     │
                                                                     ▼
Stub Agent inbox 轮询 ──▶ dispatch_ack ──▶ E7 RUNNING
                                │
                                ├─▶ events (seq cursor) ──▶ 推 projected message
                                │
                                └─▶ result SUCCEEDED ──▶ outbox event → relay → WS message.created
```

**S3 跑通端到端链路**（参考 `docs/design/detailed/06-workitem-provider-sync.md` + E8 epic）：

```
PO ──POST /projects/{id}/work-items──▶ work_items（binding = active binding，默认 builtin）
                                        │
                                        └─▶ POST /work-items/{id}/assign { agent_id }
                                                │
                                                ├─▶ Trigger(WORK_ITEM) + CR(ACCEPTED) + DecisionRecord   ← 同一事务
                                                ├─▶ Execution(work_item_ref = work_item_id) + Attempt + inbox
                                                └─▶ WorkItem: assignee=AGENT / status=IN_PROGRESS
                                                        │
Stub Agent 轮询 ──▶ dispatch_ack ──▶ RUNNING ──▶ events ──▶ result
                                                        │
                                                        └─▶ outbox execution.completed → relay
                                                                ├─▶ WorkItem: IN_PROGRESS → IN_REVIEW
                                                                ├─▶ SYSTEM 评论（含产出摘要）
                                                                └─▶ WS message.created（Channel 侧）
```

**S3 已落地的 13 个端点**（全部 `RequireAuthorization`，project member 作用域）：

| Method | Path | 说明 |
| --- | --- | --- |
| GET | `/work-management/providers` | 已注册 Provider 目录（`ProviderRegistry.List()`） |
| GET | `/projects/{id}/work-management/bindings` | binding 列表（含 active 标记） |
| POST | `/projects/{id}/work-management/bindings` | 切换 Provider（project owner；老 active 先下台再上台，同事务） |
| GET / POST | `/projects/{id}/work-items` | 列表（status/type/assignee 过滤）/ 创建（走 active binding） |
| GET | `/work-items/search?project_id=&q=` | 全文 + 中文子串搜索 |
| GET / PATCH | `/work-items/{id}` | 详情 / 修改（走 work_item.binding） |
| POST | `/work-items/{id}/transition` | 状态机迁移 |
| GET / POST | `/work-items/{id}/comments` | 评论 |
| POST | `/work-items/{id}/relations` | 关联（自动写反向行） |
| GET | `/work-items/{id}/executions` | WorkItem → Execution 正向可见 |
| POST | `/work-items/{id}/assign` | **Work Delivery 主链路**：指派 Agent → CR + Execution，幂等 |

**S3 的不变量（DB 强制，不靠应用层自觉）**：同 project 只能有一条 `is_active` binding（partial unique index）；
`(binding_id, external_ref)` 唯一；`work_relations` 自关联与非同 project 关联被拒；`assignee_type` / `assignee_id` 成对；
`canonical_status_category` 由 `status` 派生（代码层，禁止手填）。

**M3b Phase 2：Agent 实时收单**（`docs/design/detailed/03` §2 §7.2）：

```
Agent ──WS /ws + hello{agent_token}──▶ 校验 agent_token → 会话登记为 (AGENT, agent_id)
                                        │
PO ──POST /work-items/{id}/assign──▶ 事务提交（CR + Execution）
                                        │
                                        └─▶ 提交后 push execution.dispatch ──▶ Agent 实时开工
                                                 │
                                                 └─ 无活跃会话时推送返回 false：
                                                    dispatch 留在 inbox，Agent 轮询兜底
```

- **推送是优化，不是依赖**：`AgentDispatchNotifier` 只读 DB 事实、只推帧、**不写库**
  （尤其不改 `dispatch_sent_at`——那是「Runtime 决定派发」的业务事实，不是投递尝试）。
  推不出去不算失败；`GET /agents/{id}/executions/inbox` 永久保留为兜底路径。
- **推送与轮询形状逐字段一致**：同一份 dispatch 两条通道，SDK 只应有一份解析代码。
  `deadline_s` 统一为**相对秒数**（绝对时间戳会让快时钟的 Agent 提前放弃）。
- **重复投递允许**：客户端按 `(execution_id, attempt_no)` 幂等（detailed/10 §4），
  重复收到只重绑会话并再回 ACK，不得重复执行。
- **会话主体分类型**：`project_members`（人）与 `agent_project_membership`（Agent）是两套关系表；
  注册表按 `(ActorType, ActorId)` 分别索引，`execution.dispatch` 只认 AGENT 索引——
  人 id 与 agent id 同为 UUID，只按 id 索引会把执行指令推给人的浏览器。

**M3b Phase 3：Agent 侧协议闭环**（`10fb1d5`）：

```
Agent ──GET  /agents/{id}/collaboration-requests/inbox────────▶ 拉取派给自己的 PENDING CR
        └──POST /agents/{id}/collaboration-requests/{crId}/decision──▶ ACCEPT / REJECT / NEED_CONTEXT
                                                                       │
                                       DecisionApplier（与 /internal 决策入口共用同一实现）
                                                                       ▼
                                                 CR=ACCEPTED + decision_record + Execution + dispatch
```

- **Agent 自己表态**：决策主体是 agent_token 的 `sub`；越权闸门校验 `cr.target_agent_id`——
  缺这道闸，任何持 agent_token 的主体都能改掉别人 CR 的终态。
- **决策落库只有一处实现**（`Routing/DecisionApplier`）：`/internal` 那条允许人代 Agent 提交，
  但两条入口的业务事实必须逐字段一致，否则会出现「人代提交能建 Execution、Agent 自己提交建不出来」。
- **续传位点**：`POST /agents/{id}/executions/{executionId}/resume_request` 返回**连续位点**
  `last_persisted_seq`（不是 `MAX(seq)`）+ dispatch 冻结快照；过期 attempt 与终态 execution 显式 409，
  而不是回一个「接着发」的位点。
- **两条通道字节一致**：WS 与 REST 必须用同一套命名策略**与同一套 JSON 转义策略**
  （默认 Encoder 会把中文写成 `\uXXXX`，而 HTTP 侧不转义 → 同一份 payload 在两条通道上字节不同）。

> **wire 契约统一 snake_case**：REST 响应 / WS envelope / outbox payload / JSONB 内容四处同形。
> 全局策略在 `Program.cs` 的 `ConfigureHttpJsonOptions`；query 参数对新增端点显式声明 snake_case 名。

**M1–M9 仅作能力域映射标签，不是实施顺序**（明细见 `docs/design/detailed/09-implementation-checklist.md`）：M1=E1、M2=E3+WS+relay、M3a=E2、M3b=Connector、M4a=E6、M4b=E4、M5=E5、M6=E8、M7=E7 完整、M8=E10 完整、M9=集成压测。

## 本地开发

```bash
# 1. 起基础设施（PostgreSQL 16 + pgvector / Redis 7）
docker-compose up -d

# 2. 建测试库（仅首次；已有数据卷不会重跑 entrypoint 脚本）
docker exec mateos-postgres psql -U mateos -d mateos -c "CREATE DATABASE mateos_test"

# 3. 跑全部测试（单元 + 集成）
docker run --rm --network mateos_default \
  -e MATEOS_TEST_POSTGRES="Host=postgres;Port=5432;Database=mateos_test;Username=mateos;Password=mateos_dev_only" \
  -e MATEOS_TEST_REDIS="redis:6379" \
  -v "$PWD:/src" -v mateos-nuget:/root/.nuget/packages -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test mateos.slnx
```

- **统一在容器内构建/测试**：不依赖本机是否装了 .NET SDK，CI 与本地走同一条路径。
- 端口：PG `55432`、Redis `16379`（避开默认值与本机保留端口段）。
- schema 由 `ops/postgres/migrations/*.sql` 显式管理（不用 EF Migrations），
  启动时自动应用，每个迁移一个事务；SQL 是唯一副本，以嵌入资源打进 API。
- 集成测试用独立库 `mateos_test` 并每个用例前 TRUNCATE；连接串可用
  `MATEOS_TEST_POSTGRES` / `MATEOS_TEST_REDIS` 覆盖。

## 工作约定

- 单人项目，直推 `main` 分支（无 PR 流程）
- 文档与原型是同源物，跨文档修订必须一次性收敛（不留互相矛盾）
- 评审流程：先看 `docs/requirement/` → 再看 `docs/design/` → 最后看 `docs/UI design/`
- 凭据、API Key 等敏感信息走 `User → Credential → Agent Runtime` 分离模型，绝不回显明文
- **任何文档 / 原型 / UI 改动前先自检**：用户是否需要理解 CollaborationRequest、Execution、Attempt、Event、Lease、Provider、fencing 才能完成操作？若答案是"需要"，UI 就设计错了

## License

MIT — see `LICENSE`.
