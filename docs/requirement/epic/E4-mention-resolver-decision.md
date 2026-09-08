# E4 · Mention Resolver & Decision Engine

| 字段 | 值 |
| --- | --- |
| Epic ID | E4 |
| 标题 | Mention Resolver & Decision Engine |
| 阶段 | MVP（M4） |
| 上游 | PRD v0.3 §5 FR-4 / §5 FR-5 / SYSTEM_DESIGN v0.2 §4.1 / §4.2 / §5.1 mentions + decision_records / UI DS §5.4 |
| 下游 | E3（消息载体）、E5（Need Context 触发记忆）、E7（Runtime dispatch）、E9（协作协议） |
| 状态 | Draft |

## 1. 背景与动机

Mention + Decision 是 MateOS 的**核心差异点**（PRD §5 FR-4 "优先级最高"）。传统 IM 里 @ 是字符串，MateOS 里 @ 是一次**意图解析 + 能力路由 + 决策可解释**的端到端流程。本 epic：

- @ 谁 → 异步解析为可路由的成员候选
- 能力排序 → Top-N 命中（前端可见分数，禁止黑箱）
- 决策三态（Accept / Reject / Need Context；Delegate 留 V2）
- 决策依据三件套（capability / context_score / permission）持久化

## 2. 范围

### 2.1 In Scope

- Mention 解析（@Human / @Agent / @group / @all 四种）
- Mention Resolver Worker（异步化，BullMQ 队列）
- Capability Ranking（capability 匹配度 + 负载 + 近期相关性）
- Top-N 命中 + 分数回写（`mentions.scores`）
- Decision 状态机（PENDING → ACCEPTED / REJECTED / NEED_CONTEXT；DELEGATED 留 V2 枚举）
- 分析三件套（capability / context_score / permission）强制落库
- Decision 超时重路由（60s 默认）
- 决策卡片 UI（P5 5 形态之一）
- Resolver 结果可视化（消息流 mention 胶囊展示命中分数）

### 2.2 Out of Scope

- Delegate 实际路由（V2，PRD §5 FR-5 备注）
- @all 仲裁（M6 E8 落地，本 epic 仅实现"@all 投递"）
- 决策回放（V2+）

## 3. 数据模型

```sql
-- mentions（一条消息可有多个 mentions）
CREATE TABLE mentions (
  id            UUID PRIMARY KEY,
  message_id    UUID NOT NULL,
  channel_id    UUID NOT NULL,
  mention_type  TEXT NOT NULL CHECK (mention_type IN ('USER','AGENT','GROUP','ALL')),
  raw_text      TEXT NOT NULL,           -- '@backend' / '@Backend Agent' / '@all'
  status        TEXT NOT NULL DEFAULT 'RESOLVING'
                CHECK (status IN ('RESOLVING','RESOLVED','UNRESOLVED')),
  targets       JSONB,                   -- [{ type:'AGENT', id:'...', score: 0.95 }, ...]
  resolved_at   TIMESTAMPTZ,
  created_at    TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_mentions_message ON mentions(message_id);
CREATE INDEX idx_mentions_targets_gin ON mentions USING gin(targets);
-- v0.2 增：resolver 缓存 key 在 Redis mention:{mention_id}

-- decision_records
CREATE TABLE decision_records (
  id           UUID PRIMARY KEY,
  mention_id   UUID NOT NULL REFERENCES mentions(id),
  agent_id     UUID NOT NULL REFERENCES agents(id),
  decision     TEXT NOT NULL CHECK (decision IN ('ACCEPT','REJECT','NEED_CONTEXT','DELEGATE')),
  reason       TEXT,
  needs        JSONB,                   -- NEED_CONTEXT 时：["近 7 天失败率", "当前配置"]
  analysis     JSONB NOT NULL,          -- { capability: bool, context_score: int, permission: bool }
  delegate_to  UUID,                    -- DELEGATE 时
  expires_at   TIMESTAMPTZ NOT NULL,    -- 60s 默认
  decided_at   TIMESTAMPTZ,
  created_at   TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_decision_mention ON decision_records(mention_id);
CREATE INDEX idx_decision_agent_time ON decision_records(agent_id, created_at DESC);

-- mfeat:{project_id}:{agent_id} (Redis Hash)
--   accept_rate 30 天滚动
--   load 实时负载
```

### 3.1 状态机

```
            ┌──────────────────────────────┐
mention ───► PENDING ─┬─► ACCEPTED ──► 创建 task placeholder（V2 runtime 接管）
                     ├─► REJECTED（reason 必填）
                     ├─► NEED_CONTEXT（needs[] 必填）
                     └─► DELEGATED（V2，delegate_to 必填合法成员）
PENDING 超 60s → 下一个候选；全部超时 → mentions.status=UNRESOLVED，WS 推发起人
```

## 4. API

### 4.1 REST

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET | `/channels/:id/messages/:seq/mentions` | 拉取 mention 解析状态 | channel member |
| POST | `/decisions`（内部） | Runtime 上报决策 | agent token |
| GET | `/decisions?mention_id=...` | 查询决策记录 | 关联成员 |

### 4.2 WebSocket

- `mention.resolved` — 解析完成事件，`payload: { mention_id, status, targets, scores, resolved_at }`
- `decision.proposed` — Agent 决策上报，`payload: { mention_id, agent_id, decision, reason, needs?, analysis, decided_at }`
- `mention.unresolved` — 全部候选超时，`payload: { mention_id }`

### 4.3 错误码

- 422 决策不在 4 态枚举
- 422 NEED_CONTEXT 缺 needs[]
- 422 DELEGATE 缺 delegate_to 或非合法成员

## 5. 关键流程

### 5.1 消息发送触发 Resolver

```
1. 客户端发消息 @backend @tom 麻烦看下 @all
2. POST /channels/:id/messages（E3）
   → 消息落库、seq 分配
   → mention 提取：3 条 mentions 行（status=RESOLVING）
   → 入 BullMQ mention.resolve 队列
3. 返回客户端：消息已落库
4. Resolver Worker（异步）：
   a) 硬过滤：
      - channel 成员资格（channel_members JOIN agents）
      - permission：read/write 必须 ALLOW
      - 排除 status ∈ {OFFLINE, ERROR, WORKING}
   b) 候选排序（v0.2 起 6 态过滤）：
      score = 0.6 * capability_match
            + 0.25 * (1 - load)            // load ∈ [0,1]
            + 0.15 * accept_rate_30d       // 滚动
   c) Top-N=3，写 mentions.targets + scores
   d) status=RESOLVED
   e) WS 推 mention.resolved
   f) 派发：逐个向候选 agent 投递 decision 请求（WS + 队列）
5. 客户端收到 mention.resolved 后，消息流胶囊展示
   "@backend → Backend Agent 0.95 · QA Agent 0.62"
```

### 5.2 Decision 超时重路由

```
PENDING 计时：
- decision_records.expires_at = created_at + 60s
- BullMQ delayed job @60s 触发 scan_expired
- 单 agent 超时：
  a) 该 agent 的 decision 记录标 EXPIRED（不入主状态，audit 留痕）
  b) 取下一个候选重投递，重置 expires_at
- 全部候选超时：
  a) mentions.status = UNRESOLVED
  b) WS mention.unresolved 推给发起人
  c) 写 audit_logs
```

### 5.3 Analysis 三件套来源

| 字段 | 计算来源 |
| --- | --- |
| `capability` | agent.capabilities ∩ request.required_capabilities 非空 → true |
| `context_score` | 0-100：相关 memory_refs 命中数 / 阈值 + recent_messages 数量 / 阈值，取小者，<80% 视为不完整 |
| `permission` | E6 `check(agent, perm, channel_scope)` 返 ALLOW/REQUEST → true；DENY → false |

### 5.4 @all 投递

- 6 态过滤后 AVAILABLE + THINKING 的 agent 全员投递
- 预警气泡已在 UI DS §5.4 落实

## 6. UI

### 6.1 消息流组件

- `<MentionChip>`：hover/点击展开命中候选 + 分数
- `<DecisionCard>`：P5 5 形态之一
  - 左侧 2px 色条：Accept 绿 / Reject 红 / Need Context 琥珀
  - 顶部徽标（ACCEPT/REJECT/NEED CONTEXT）
  - 分析三格（等宽）：能力匹配 ✓/×、上下文完整度 N% + 进度条（≥80% 绿）、权限检查 ✓/×
  - 理由（reason）
  - 缺失清单（NEED_CONTEXT 时）
  - 操作按钮：补充上下文 / 转为人工处理（V1）

### 6.2 状态

- 解析中：消息流对应 mention 胶囊显示「⏳ 解析中」（浅灰）
- 已解析：胶囊展示命中候选
- 决策中：决策卡片显示「正在判断…」
- 决策完成：决策卡渲染
- 超时：消息下加一行琥珀小字「@backend 暂无 Agent 响应，3 分钟后回退」

## 7. 验收标准

### 7.1 功能

- **F1** @backend 触发 Resolver，2s 内完成；mention.resolved 推到发起人
- **F2** 候选只含 channel member + 6 态排除后 + permission 通过
- **F3** 决策 Accept 落库 decision_records，analysis 三件套必填
- **F4** Need Context 决策必须带 needs[]，UI 决策卡渲染缺失项
- **F5** PENDING 60s 后未响应 → 自动重路由到下一候选
- **F6** 全部超时 → mentions.status=UNRESOLVED + 通知发起人
- **F7** 同一 mention 多 agent 决策，全部到齐后才关闭（all-accept 不阻塞；任一 reject 不影响其他）
- **F8** mention 胶囊 hover 展开命中分数

### 7.2 E2E

- `e2e/E4-001-resolver-rank`：@backend 命中 Backend 0.95、QA 0.62，顺序对
- `e2e/E4-002-decision-state-machine`：Accept/Reject/Need Context 三种各走通
- `e2e/E4-003-timeout-reroute`：单 agent 60s 未响应 → 重路由 → 第二个 agent 收到
- `e2e/E4-004-all-timeout`：所有候选超时 → UNRESOLVED + 通知
- `e2e/E4-005-mention-visibility`：消息流胶囊展示命中分数
- `e2e/E4-006-analysis-required`：缺 analysis 的 decision 写入返 422

### 7.3 非功能

- Resolver P95 < 2s（候选 ≤ 10）
- mention.resolved WS 广播 P95 < 500ms
- 1000 mentions/分钟场景下 BullMQ 无积压

## 8. 与其他 Epic 的关系

- **被依赖**：
  - E5 Need Context 触发记忆搜索
  - E7 Runtime 收到 Accept 后 dispatch 任务
  - E8 audit 写 Resolver 路径
  - E9 AgentBoard 协作协议的 request 也走 Decision 状态机
- **依赖**：E2（Agent 6 态）、E3（消息载体）、E6（permission）
- **冲突裁决**：Resolver 不黑箱 → 命中分数必带（与 UI DS §5.4 / SD §4.1 强约束）

## 9. 风险与开放问题

- **R1**：60s 超时默认值需用真实数据校准（SD §13 开放问题）
- **R2**：capability 命中算法 MVP 用规则分，V2 可引入 embedding 相似度
- **R3**：@all 在大频道（>50 agent）的成本预警阈值如何动态调整？→ MVP 固定文案，V2 根据历史均值
- **R4**：mention 解析失败但消息已发送——消息流用 placeholder 标识「⏳ 解析中」，避免用户困惑

## 10. 实施顺序（M4）

1. `mentions` / `decision_records` 表 + REST
2. BullMQ `mention.resolve` 队列 + Resolver Worker
3. 能力排序算法（含 6 态过滤）
4. Decision 上报端点 + 状态机
5. 超时扫描 + 重路由
6. 决策卡 + 命中分数 UI
7. E2E 套件
