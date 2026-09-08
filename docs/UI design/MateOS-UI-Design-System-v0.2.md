# MateOS UI Design System v0.6

| 文档信息 | 内容 |
| --- | --- |
| 版本 | v0.6（v0.5 协议收口 + v0.4.2 capacity 原子化修订版，文件名保留 v0.2） |
| 日期 | 2026-09-08 |
| 上游 | `docs/requirement/MateOS-总体需求文档.md` (PRD v0.4)、SYSTEM_DESIGN v0.3.2、UI Design Guidelines v0.1 |
| 配套原型 | `P3-agent-card.html` / `P4-project-dashboard.html` / `P5-channel-prototype.html` + 待做 Work / Approve / Settings |
| 设计令牌 | `tokens.css`（**单一事实来源**） |
| 状态 | Draft，**P3 / P4 / P5 原型需要按本版 + v0.4.2 收口回修** |

> **v0.5 → v0.6 修订要点**（v0.4.2 收口）：
> 1. **删除"Busy → 自动 NEED_CONTEXT"行为**——v0.4.2 E4 引入 Redis atomic slot 调度后，Busy Agent 直接被 Resolver 跳过，**不再**产生"我很忙所以需要上下文"的伪造决策
> 2. P3 Agent Card「当前工作」区改读 `agent_executions`（E7 事实源），不再读旧的 Task 表
> 3. Work Management Connection 改为 Org/Owner 级——P11 Settings UI 改为先选 Connection 再绑 Project
> 4. P4「执行（AgentBoard）」卡彻底删除；P-Work 页面（WorkItem 列表）正式进 MVP
> 5. decision 卡片从 `decision_records` 投影（不变），但 "执行中" 状态从 `agent_executions` 拉（projection）

---

## 1. 设计原则

1. **成员优先，不是消息优先。**
2. **Agent 的行为必须可解释。** 决策卡片从 `decision_records` 投影，不复制字段。
3. **上下文常驻，不折叠。** 成员、记忆、待办是核心资产。
4. **人类始终握有闸门。** Agent lifecycle / 记忆写入 / WorkItem 审批 / Agent execution 都有显式入口。
5. **工程化密度。** 信息密度优先；动效只表达"进行中"。
6. **Work Management 是独立域。** UI 永远不出现"execution backend 切换"等概念；Work 页面只与 WorkItem 交互，不展示 Execution 后端细节。

---

## 2. 冲突裁决

| # | 冲突点 | 旧版说法 | v0.5 裁决 | 理由 |
| --- | --- | --- | --- | --- |
| C1 | **Agent 状态** | 6 态（OFFLINE/.../ERROR） | **lifecycle（ACTIVE/PAUSED/DISABLED）× activity（OFFLINE/AVAILABLE/.../ERROR）双维度** | owner 暂停（lifecycle）与心跳丢失（activity）不能合并 |
| C2 | **消息流 5 形态** | DECISION/MEMORY_REQUEST 复制 decision_records/memory_items 字段 | **保留 5 形态 UI，但 DECISION/MEMORY_REQUEST 只引 `entity_ref`（projection）** | 单一事实源；防止双写不一致 |
| C3 | **Agent capability / permission** | Agent 表有 `can_execute` / `can_review` 字段 | **删除 Agent 字段；Capability（能不能）+ Permission（允不允许）单一事实源在 E6** | 双事实源必然漂移 |
| C4 | **Permission effect** | `ALLOW` / `DENY` / `REQUEST` | **ALLOW / DENY / `REQUIRE_APPROVAL`** | `REQUEST` 与 CollaborationRequest / HTTP Request 概念冲突 |
| C5 | **WorkItem 与 Execution** | 共享 `Task` 表 | **完全独立**——WorkItem 在 E8，Execution 在 E7，`work_item_ref` optional 引用 | 一个 Execution 可无 WorkItem（"@backend 看下代码"）；一个 WorkItem 可多 Execution（不同 Agent 重试） |
| C6 | **外部协作** | "执行后端可切 AgentBoard / 项目跟踪可切 AgentBoard/Jira" | **删除 AgentBoard 概念；Project Settings 改 Work Management 动态表单**（Built-in + Jira V1+） | MateOS 永久自洽执行域；Project 不挂 Provider 字段 |
| C7 | **Tasks 页面** | V3+ 升格 | **Work 页面进入 MVP**；导航 Tasks 入口改 Work | WorkItem 是 MVP 一等公民，不再绑定 AgentBoard |

---

## 3. 设计令牌

> 与 v0.4 一致；详见 `tokens.css`。本节不重复。

### 3.1 Lifecycle × Activity 状态色（v0.5 新增）

| 维度 | 状态 | 圆点 | 徽标 | 文案 |
| --- | --- | --- | --- | --- |
| **lifecycle** | ACTIVE | （由 activity 决定） | — | — |
| | PAUSED | 灰 | 灰「已暂停」 | "owner 已暂停" |
| | DISABLED | 灰 | 红「已禁用」 | "系统禁用" |
| **activity** | OFFLINE | `#888780` | — | "心跳丢失" |
| | AVAILABLE | `#1D9E75` | — | "可接活" |
| | THINKING | `#7F77DD`（呼吸） | — | "正在判断" |
| | WORKING | `#378ADD`（流式光标） | — | "正在产出" |
| | WAITING_CONTEXT | `#BA7517` | — | "需要人类行动" |
| | ERROR | `#E24B4A` | — | "调用失败 / 限额触顶" |

**冲突时显示规则**：lifecycle ≠ ACTIVE → 强制显示 lifecycle 徽标 + activity 灰点；lifecycle = ACTIVE 时按 activity 语义显示。

---

## 4. Agent 状态机 UI 表现

| lifecycle | activity | 圆点色 | 徽标 | hover 详情 |
| --- | --- | --- | --- | --- |
| ACTIVE | OFFLINE | 灰 | — | "心跳丢失 > 90s" |
| ACTIVE | AVAILABLE | 绿 | — | "可接活" |
| ACTIVE | THINKING | 紫（呼吸） | — | "正在判断" |
| ACTIVE | WORKING | 蓝（流式） | — | "正在产出" |
| ACTIVE | WAITING_CONTEXT | 琥珀 | "等待上下文 ×N" | 缺失项列表 |
| ACTIVE | ERROR | 红 | "fix_hint" | "查看日志" |
| PAUSED | * | 灰 | "已暂停" | "owner 已暂停" |
| DISABLED | * | 灰 | "已禁用" | "系统禁用" |

`lifecycle ≠ ACTIVE` 的 Agent **永不出现在 Resolver 候选**（UI 也置灰不可 @）。

---

## 5. 组件库

### 5.1 消息流：5 形态（projection）

| 形态 | 结构 | 数据来源 |
| --- | --- | --- |
| **人类消息** | 方角头像 26px + 姓名 + 时间 + 正文 | `messages.content.human` |
| **决策投影** | 圆形头像 + 决策徽标 + **entity_ref → decision_records** + 三格 analysis + 理由 | **`decision_records` 是事实源**，消息只引 `decision_ref` |
| **Agent 输出投影** | 圆形头像 + Markdown + 代码块 + 元信息 | `agent_executions` 关联输出（`execution_artifacts`） |
| **系统事件** | 单行居中灰字 | `messages.content.system` |
| **记忆申请投影** | 主色浅底 + 标题 + **entity_ref → memory_proposals** + 标签 + 三按钮 | **`memory_proposals` 是事实源** |

> 关键：**消息流不存储事实**。点击决策卡 / 记忆卡跳转实体详情页（事实源）。
> **v0.6 改** decision 卡片"执行中"显示从 `agent_executions` 拉（projection display_state），不再镜像 CollaborationRequest status。

### 5.2 成员行

`状态点 7px`（双维度语义）+ 名称 13px + 右侧状态文字 11px；Agent 额外一行 capability 标签（canonical key 转 display name）+ lifecycle 徽标（若非 ACTIVE）。

### 5.3 Agent Card（P3）— v0.6 修订

| 区块 | 调整 |
| --- | --- |
| **状态与身份** | lifecycle 徽标（PAUSED/DISABLED）；activity 与 reason 单独展示 |
| **当前工作** | **v0.6 改** 读 `agent_executions`（E7 事实源，最近 1 条 RUNNING 详情）；点击跳转 Execution 详情页 |
| **能力 Capabilities** | 4 个固定 key（不再"未启用"） |
| **可见范围** | 不变 |
| **用量与成本** | 不变 |
| **运行配置** | 删除 can_execute / can_review 行；凭据行保留；Runtime 改"MateOS Runtime"（不再"V1+ 可切 AgentBoard"） |
| **最近活动** | 来源从 `audit_logs` 投影，按 decision / execution / memory 分类 |

### 5.4 Mention 输入器

不变（v0.4 已落地）。

### 5.5 Work 页面（v0.5 新增 MVP，v0.6 强化）

- 路由：`/projects/:id/work`
- 列表视图：表格（标题 / 类型 / 状态 / Assignee / Due / Updated / Provider 标签）
- 顶部 Provider 切换器：Built-in 不可切；Jira 时显示"Jira" 标签 + 状态映射配置入口
- 过滤：状态 / 类型 / Assignee / 提供方（Built-in / Jira）
- 详情：WorkItem 详情 + 评论 + Execution 关联
- 新建 WorkItem：弹窗（type / title / description / assignee / due）
- "绑定 Provider"按钮：仅 Project owner 可见；跳转 Settings

### 5.6 Project Settings（v0.6 改：Connection 先选）

| Section | 字段 |
| --- | --- |
| **Work Management** | **v0.6 改** Connection 选择器（Org 级已有 Connection 列表）；选 Connection 后再选 Provider；Project binding 配置 external_project_ref + Status Mapping；Sync Status 显示 |
| **Members** | Human / Agent 邀请 |
| **Channels** | CRUD |
| **Memory Policy** | 默认人审；可放宽到 owner only |
| **Agent Runtime** | lifecycle 批量控制（V3 启用） |

> **删除**：旧的「执行后端」「项目跟踪」两块（v0.4 的"外部协作"卡已废弃）。

### 5.7 **v0.6 新增**：删除"Busy → 自动 NEED_CONTEXT"行为

v0.5 之前设计：当 Agent 处于 WORKING 状态收到新 mention，自动回复 "Need Context" 变体（"我很忙，请稍后 @ 我"）。

**v0.6 删除此行为**——v0.4.2 引入 Redis atomic slot reservation 后：
- Resolver 选 Agent 时，slot reservation 失败的 Agent 直接被跳过
- Busy Agent 不进入候选（因为没有空闲 slot）
- 没有"伪造 NEED_CONTEXT"语义
- 用户体验：@backend 会被路由到其他可用的 Agent，**不**会让"很忙的 Agent"返回奇怪的 NEET_CONTEXT 响应

> v0.5 之前的 Busy → NEED_CONTEXT 行为本质是 v0.4 旧 Resolver 设计（用 activity 当调度源）的补丁；v0.4.2 修复 Resolver 后不再需要。

---

## 6. 页面清单（MVP 8 项 + 1 项 V1+）

| 优先级 | 页面 | 原型 | v0.5 调整 |
| --- | --- | --- | --- |
| ★★★ | P5 Channel | v0.4 → 需回修（DECISION 改 entity_ref） | projection 模型 |
| ★★ | P4 Project Dashboard | v0.4 → 需回修（Work Management 替换"外部协作"） | 删 AgentBoard |
| ★★ | P3 Agent Card | v0.4 → 需回修（lifecycle badge + 删 can_execute） | 双维度状态 |
| ★★ | **P-Work**（新） | 待做 | MVP Work 页面 |
| ★★ | P6 审批中心 | 待做 | MVP（含 Memory + WorkItem 审批） |
| ★ | P2 我的 Agents | 待做 | lifecycle 筛选 |
| ★ | **P7 Memory 文档** | 待做 | MVP（v0.3 已升） |
| ★ | P1 登录 / 注册 | 待做 | |
| ★ | P8 团队与组织设置 | 待做 | |
| V1+ | E9 Jira 集成设置 | 合并到 P4 Settings | |

---

## 7. 响应式

不变（与 v0.4 一致）。

---

## 8. 与系统设计的对接

| 实体 | UI 落点 |
| --- | --- |
| `organizations` / `organization_members` | P8 团队设置（v0.5 新增 Org Member 管理） |
| `agents` lifecycle + activity | Agent Card / 成员行 / 上下文面板（双维度） |
| `collaboration_requests` | 决策卡片投影（不存字段，只引 entity_ref） |
| `decision_records` | 决策详情页（事实源） |
| `agent_executions` | Agent Card 当前工作区块 + WorkItem 详情关联 |
| `work_items` | Work 页面主表（Built-in） |
| `work_item_projections` | Work 页面（Provider=Jira 时） |
| `work_item_bindings` | Project Settings（Work Management） |
| `memory_proposals` | 记忆卡投影 |
| `permissions` (effect=REQUIRE_APPROVAL) | 申请 → 走 E5 / E6 审批中心 |

---

## 9. 下一步

1. **回修 P3 / P4 / P5 原型**：
   - P3 加 lifecycle badge + 删 can_execute 行 + 当前工作改 execution 投影
   - P4 「外部协作」卡改「Work Management」动态表单（Built-in + Jira V1+ 钩子）
   - P5 决策卡 / 记忆卡改 entity_ref 投影（不复制字段）
2. **新增 P-Work 原型**（v0.5 新）
3. **新增 P6 审批中心**（待做；v0.3 已升 MVP）
4. 出组件切图与状态清单，交付前端
5. tokens.css 同步 lifecycle × activity 状态色（v0.5 新增徽标样式）

---

## 10. 更新记录

### v0.5（2026-09-08，架构评审修订）

1. **删除 AgentBoard**：C6 裁决；C7 Work 页面入 MVP
2. **lifecycle × activity 双维度**：C1；新增 §3.1 状态色；§4 状态机 UI 表现；§5.3 Agent Card
3. **Capability 与 Permission 单一事实源**：C3；删除 Agent `can_execute` / `can_review`
4. **Permission effect `REQUIRE_APPROVAL`**：C4
5. **消息流 projection**：C2；§5.1 表格说明
6. **Work Management 域**取代原「外部协作」卡：§5.5 Work 页面（MVP）+ §5.6 Project Settings 动态表单
7. **P3 / P4 / P5 原型回修任务清单**：§9
8. **新页面 P-Work** 入 MVP 8 项
9. **§6 不变** 响应式；§7 新增跨文档落点表
### v0.4 → 已合并到 v0.5
### v0.3 → v0.4 略（见 PRD v0.4 changelog）
