# MateOS Domain Model v0.3（草案）

| 项 | 内容 |
| --- | --- |
| 状态 | **Draft · 待拍板**。定稿后再回修 SYSTEM_DESIGN 与 Epic，**不要在此之前改主文档** |
| 日期 | 2026-09-08 |
| 上游 | PRD v0.4 / SYSTEM_DESIGN v0.3.2 / 2026-09-08 架构评审 |
| 下游 | 定稿后 → 一次性回修 SYSTEM_DESIGN 7 处 + Epic 5 处 |

---

## 0. 先回答「是 Jira 还是 OS」

判定方法：看产品的**主语**是什么。

| 主语 | 它是什么 |
| --- | --- |
| WorkItem | AI 时代的 Jira |
| Message / Channel | AI 时代的 Slack |
| **Agent（成员）** | AI 时代的 Operating System |

**MateOS 选第三者。** 这不只是一句定位，它有可执行的推论：

- **Domain 主语 = Agent；用户入口 = Human Attention（Needs You）。**
  这两件事不是一回事：**Agent-centric ≠ Fleet-centric**。领域围绕 Agent 组织，但默认入口是"有什么在等我"（Needs You），而不是"我的 Agent 此刻什么状态"（Fleet 态势图）。
- 默认入口 **Needs You**（围绕"需要我"），第二入口 **Channels**（发生工作的对话），第三 **Work**（工作中的对象），第四 **Team**（成员与 Agent 详情）。
- WorkItem 是必要组件，但它是 Agent 的**工作对象**，不是系统的组织轴心
- 因此 Agent 是一等成员，而不是挂在工单上的执行器

这条判据也给出两条防退化规则：

- **任何时候 Work 列表成为首页，MateOS 就退化成工单系统了。**
- **任何时候 UI 需要用户理解 Execution / Attempt / Lease / fencing 才能操作，MateOS 就退化成运维后台了。**

### User-facing Vocabulary（内部模型 ↔ 用户语言）

| Internal Model | 默认 UI 表达 | 默认可见性 |
| --- | --- | --- |
| CollaborationRequest | 不显示名称（用户只看到"Agent 接手了"） | 隐藏 |
| Decision `ACCEPT` | Taking this / 已接手 | 消息流可见 |
| Decision `NEED_CONTEXT` | Needs information | 消息流 + Needs You |
| Decision `REJECT` | Can't take this（必带建议人选） | 消息流可见 |
| Execution | Work / Activity（"正在做…"） | Level 1 只显示进度文案 |
| ExecutionAttempt | 默认隐藏 | Level 3 |
| ExecutionEvent | 默认隐藏（合并为 `▸ 8 activity events`） | Level 3 |
| Agent activity `WORKING` | Working | Level 1 |
| Agent `ERROR` / `UNHEALTHY` | Needs attention / Problem（带可执行动作） | Needs You |
| Lease / fencing / provider_event_id | **永不展示** | 永不 |
| Provider / external_ref / canonical status | Advanced details / Settings | Level 3 |

**Invariant（与 I1–I10 同级）**：

> **Domain Entity ≠ UI Navigation Entity。**
> 数据库模型不得一对一生成页面；UI 分三级（Level 1 Outcome / Level 2 Explanation / Level 3 Technical Trace），**Level 3 永远不能默认展开**。

---

## 1. 两个世界 + 一座桥

```
Conversation Domain          Work Domain
（说了什么）                  （承诺了什么 / 跑了什么）
   Channel                      Project
     └ Topic                      └ WorkItem
         └ Message                    └ Execution

        ↘ Trigger              Trigger ↙
              CollaborationRequest
                     └ Decision → Execution

切面：Memory（按 scope 挂载） / Agent（一等成员，两域都参与）
```

关键：**两域之间唯一的桥是 CollaborationRequest**。它因此**不是**"工作容器"——它是一条路由与决策记录。这是解决"6 个容器"问题的落点：WorkItem 是承诺，Topic 是会话，Execution 是运行实例，CollaborationRequest 只是**桥**。

---

## 2. 核心实体（8）

| 实体 | 定义 | 边界原则（不是什么） |
| --- | --- | --- |
| **Organization** | 顶层租户，User / Project 的容器 | **不是权限作用域**（权限最小到 Project）；不承载 Memory |
| **Project** | **Context Boundary**：成员 / 记忆 / 工作 / Provider 绑定的作用域 | 不一定是代码项目（可以是"Tesla 竞品调研"）；不挂 `integration_backend`；`repo_url` 可空 |
| **Agent** | 一等成员：owner + capability + lifecycle×activity + credential | 不是工具，不是工单执行器；不拥有工作（I5） |
| **WorkItem** | **唯一的业务对象**：团队承诺交付的一件事 | 不是会话，不是执行记录；不是 Memory 的容器 |
| **Execution** | **Runtime 的一次运行实例**（类比 K8s 的 Pod） | 不是业务对象；不是 WorkItem 的 history 字段；**可独立存在**（I2） |
| **Channel** | 通信边界 | 不承载业务状态 |
| **Topic** | Channel 内的会话聚类（按 root message 聚类） | **不拥有** WorkItem（I3）；不维护 IMPLEMENTING/REVIEWING 等业务态 |
| **Memory** | 团队知识：scope + Source 溯源 + 人审门禁 | 不是聊天记录；scope 只有两级（I4） |

### 2.1 支撑实体（不进核心图，但有独立 lifecycle）

| 实体 | 归属 | 说明 |
| --- | --- | --- |
| User | Identity | 自然人；Agent 的 owner |
| Credential | Identity | User 1:N；Agent 使用时可热切换 |
| Message | Conversation | Topic 的组成，单层 reply |
| CollaborationRequest / DecisionRecord | Bridge | 两域唯一的桥；Decision 是 Execution 的前置门 |
| ExecutionAttempt / ExecutionEvent / Artifact | Work | Execution 的组成 |
| MemoryProposal | Cognition | 人审门禁的待审态；与 memory_items 拆表 |
| Notification | Cross | Inbox 的数据源（`category='NEEDS_HUMAN'`） |

---

## 3. 关系表（ownership vs reference）

**这是本模型最重要的一张表**——区分"拥有"和"引用"是避免概念撞车的核心手段。

| From | To | 基数 | 类型 | 说明 |
| --- | --- | --- | --- | --- |
| Organization | Project | 1:N | **owns** | |
| Organization | User / Agent | N:M | membership | |
| **Project** | **WorkItem** | 1:N | **owns** | WorkItem 归属唯一 Project（I1） |
| **Project** | **Memory** | 1:N | **owns** | Project 级共享记忆（I4） |
| Project | Channel | 1:N | **owns** | |
| Channel | Topic | 1:N | **owns** | |
| Topic | Message | 1:N | **owns** | 单层，不嵌套 |
| **Topic** | **WorkItem** | **N:M** | **reference** | **引用而非拥有**（I3）；一个 WorkItem 可在多个 Topic 被讨论 |
| **WorkItem** | **Execution** | 1:N | **reference** | 一次承诺可多次执行（重试 / 换 Agent） |
| **Execution** | **WorkItem** | **0..1** | **reference** | **可为空**（I2）：`@backend 看下代码` |
| Execution | Attempt / Event / Artifact | 1:N | **owns** | |
| User | Agent | 1:N | **owns** | owner_user_id |
| User | Credential | 1:N | **owns** | |
| Agent | Credential | N:1 | uses | 可热切换，不换身份 |
| Agent | Project / Channel | N:M | membership | |
| Memory | WorkItem / Execution / Topic / Message / Agent | N:M | **source_ref** | **仅溯源，不参与可见性判定**（I4） |
| CollaborationRequest | Message \| WorkItem | 0..1 | **reference** | Trigger 来源，二选一或都无（API/AUTOMATION） |
| CollaborationRequest | Execution | 0..1 | produces | ACCEPT 后由 E4 调 E7 API 创建 |

---

## 4. Invariants（10 条）

每条给出「陈述 / 为什么 / 违反后果 / 如何校验」，便于落 CI。

### I1 · WorkItem 归属唯一 Project
- **为什么**：Project 是知识 / 工作 / Provider 绑定的唯一作用域。归属一旦可多值，Memory 隔离和 Provider 路由同时失效。
- **违反后果**：同一件事在两个 Project 各有一份状态，用户看到矛盾的进度。
- **校验**：`work_items.project_id NOT NULL`；无跨 Project 的 WorkItem 关联表。

### I2 · Execution 可独立存在
- **为什么**：`@architect 帮我看下这个设计` 是协助不是工单。强制建 WorkItem 会把 MateOS 退化成"带 AI 的工单系统"。
- **违反后果**：Work 页被临时提问产生的垃圾记录淹没；失去 teammate 语义。
- **补充约束**：独立 Execution **只在 Conversation 域（Channel / Execution 详情）可见，不进 Work 视图**。约束落视图，不落数据模型。
- **校验**：`agent_executions.work_item_ref NULLABLE`；Work 页查询必须 `WHERE work_item_ref IS NOT NULL`。

### I3 · Topic 永不拥有 WorkItem
- **为什么**：一个 WorkItem 会在多个 Topic 被讨论，"拥有"会导致所有权歧义。
- **正确关系**：`WorkItem N:M Topic` 引用（类似 Slack thread 讨论 Jira ticket，thread 不拥有 ticket）。
- **校验**：Topic 是 Channel 内消息聚类，**无独立表**；Topic ↔ WorkItem 只经 `topic_work_item_refs` 关联表。

### I4 · Memory 有且仅有一个 primary scope
- **为什么**（**这是我对上一轮「Memory 可挂 7 处」的收口**）：若 Organization / Project / Channel / Topic / WorkItem / Execution / Agent 都能作为 scope，可见性判定要在 7 层做归并，权限必然出洞。
- **正确切分**：
  - `scope` ∈ `{PERSONAL, PROJECT}` —— **只有这两级，只决定可见性**
  - `source_refs` ⊆ `{channel_id, message_seq, topic_id, work_item_id, execution_id, agent_id}` —— **仅溯源展示与跳转，不参与可见性判定**
- **补充**：跨 Project 零泄漏；Source 三件套强约束不变。
- **校验**：`memory_items.scope_type CHECK IN ('PERSONAL','PROJECT')`（v0.5 落地：`scope_type` 是由 `type` 推导的**生成列**，见 detailed/05 §4.2 与 E5 §3 —— 保留 E5 的 4 类内容类别，同时保证 scope 不可漂移）；查询层只按 scope 过滤，**PERSONAL 不参与 project 过滤**（否则跨 Project 的个人记忆会漏读）。

### I5 · Agent 不拥有工作
- **为什么**：Agent 是 teammate，工作是 Project 的承诺。若 assignee 即所有权，Agent 下线/删除会导致工作悬空。
- **精确表述**：WorkItem 所有权归 Project，`created_by` 记录来源，`assignee{HUMAN|AGENT}` 只是**承担者**；**改 assignee 不转移所有权**。
- **校验**：删除 Agent 时 WorkItem 的 assignee 置空而非级联删除。

### I6 · Execution 是 Runtime 运行实例，状态收敛且幂等
- **为什么**：它是可取消、可重试、占 slot、产生 cost 的运行实体，不能被降级成 WorkItem 的一个 history 字段。
- **约束**：terminal 状态 CAS（`WHERE status IN (PENDING, RUNNING)`）；`provider_event_id` + `UNIQUE(attempt_id, provider_event_id)` 协议级幂等；**异步队列（v0.5 起 = outbox relay）只做 transport，绝不是事实源**。
- **校验**：`execution_events.attempt_id NOT NULL` + `UNIQUE(attempt_id, provider_event_id)`（**v0.4.5 已在 SYSTEM_DESIGN §5.2 补齐**）。

### I7 · 消息流只存 entity_ref
- **为什么**：复制事实源字段必然双写漂移。
- **约束**：DECISION / MEMORY_REQUEST 形态只引 `decision_ref` / `memory_proposal_ref`；卡片从事实源投影渲染。
- **校验**：`messages.content` 不得出现 `analysis` / `memory_content` 等事实字段。

### I8 · lifecycle ≠ ACTIVE 的 Agent 不参与调度
- **约束**：不进 Resolver 候选、不竞争 slot、UI 上不可被 @。
- **注意**：**不用 activity 做调度过滤**（activity 只做 UI derived）——SYSTEM_DESIGN §6.2 当前仍写 activity 过滤，与 §4.1 冲突，需按本条回修。

### I9 · Provider 路由规则冻结
- **CREATE** 用 `activeBinding.provider_key`；**UPDATE** 用 `work_item.provider_key`。
- 业务层**永不**出现 `if (provider === 'jira')`；`ProviderKey = string` + Registry。
- **校验**：CI 扫描业务层的 provider 字面量分支。

### I10 · 所有写操作留审计，权限变更必审计
- **为什么**：权限变更是最该审计却最容易被漏的（当前 E10 §3.1 审计触发点表缺 E6）。
- **约束**：`audit_logs` append-only；`PERMISSION_CHANGED` / 审批决定 / Memory 审批动作全部入审计。

---

## 5. 非不变量的设计约束（不进 invariants，但需遵守）

| 约束 | 说明 |
| --- | --- |
| Agent 无入站端口 | NAT 穿透，产出必经 Runtime Gateway |
| Topic 单层 reply | 不做嵌套楼中楼 |
| WorkItem 类型 MVP 只 `TASK` / `BUG` | EPIC / STORY 延后（改 CHECK 即可加回） |
| Built-in Provider 默认 | Jira V1+；MVP 不做历史迁移 Wizard |
| V1 `execute_code` DENY | 需在 PRD 明确"V1 的 Execution 到底交付什么" |

---

## 6. 本模型定稿后需回修的 SYSTEM_DESIGN 位置

| # | 位置 | 按哪条改 |
| --- | --- | --- |
| 1 | L394 `collaboration_requests` CHECK 含 EXECUTING/COMPLETED/FAILED | 收敛 6 态 |
| 2 | §6.2 Resolver 用 activity 过滤 | I8（改 slot） |
| 3 | L339 / L482 `work_item_projections` | v0.3.1 已删 |
| 4 | L659 旧 `resume` 协议 | 改 resume_request / resume_ack |
| 5 | L438 `execution_events` 缺幂等键 | I6 |
| 6 | L519 `work_management_connections` 仍 project 级 | 改 org 级 |
| 7 | L730 残留 `getSelfMetadata()` | 删 |
| 8 | `messages` 表 `PARTITION BY RANGE(created_at)` + PK `(channel_id, seq)` | **PG 建不出来**，分区键必须在唯一约束内；MVP 建议不分区 |
| 9 | E5 §5.1 缺 `policy.evaluate('write_memory')` 调用点 | 补 |
| 10 | E10 §3.1 审计触发点缺 E6 | I10 |

---

## 7. 待拍板

| # | 问题 | 建议 |
| --- | --- | --- |
| D1 | Execution 是否可独立存在 | **是**（I2）。上一轮已达成一致 |
| D2 | Project 是否改名 Workspace | **不改**。改名收益 < 同步成本；职责按 §2 定义为 Context Boundary |
| D3 | Memory scope 是否收为两级 | **是**（I4）。这是本轮唯一的实质收口，需要确认 |
| D4 | 试用额度形态 | **Demo Mode（有限额度，仅 onboarding）+ BYOK**，不做全平台承担。Demo 模式下 Memory 人审门禁**不得跳过** |
| D5 | 是否接受 §0 的"主语判据"作为防退化规则 | 建议接受，并写进 PRD §1 |
