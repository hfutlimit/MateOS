# Detailed Design · Autonomous Delivery · 04 · Topic, Needs You, Assignment Mode & Handoff

> **配套**：PRD v0.4 / SYSTEM_DESIGN v0.3.2 / [00-integration-map.md](./00-integration-map.md) / [03-scheduler-autonomy-policy.md](./03-scheduler-autonomy-policy.md) / v0.1 Architecture Proposal §4-5
> **范围**：Layer 1 + Layer 2 概念扩展：Topic（复用 Channel）+ Needs You（Notifications 扩展）+ Assignment Mode 3 态 + Handoff 协议
> **前置**：[00-integration-map.md](./00-integration-map.md) **（必读，9 Invariants）** + [01](./01-mission-and-grill.md) / [02](./02-workunit-and-delivery-graph.md) / [03](./03-scheduler-autonomy-policy.md)
> **本文不覆盖**：Channel 自身（E3）/ Mission / WorkUnit / Scheduler 主体（见 [01-03](./01-mission-and-grill.md)）

## 0. 文档结构

- **§1** Topic 概念与数据模型（**复用 channels + messages 字段**，不新加表）
- **§2** Topic 状态机
- **§3** Needs You 数据模型（notifications 扩展）
- **§4** Needs You 聚合算法
- **§5** Assignment Mode 3 态扩展（DIRECT / ROUTED / OPEN_CLAIM）
- **§6** Handoff 协议
- **§7** Domain Events
- **§8** 关键边界（与 #1 I-6 / I-7 / I-8 联动）
- **§9** 反模式
- **§10** e2e 验收点
- **§11** 实施 M13 子任务

## 1. Topic 概念与数据模型

### 1.1 Topic = Channel 内的协作会话

Topic 是 **Channel 内的「一次问题或需求的持续协作上下文」**，**不**新加表，复用 E3 `channels` + `messages`：

- **Channel** = 长期团队/领域空间（#backend、#frontend、#design）
- **Topic** = Channel 内一次讨论（"Jira Provider Architecture"、"Runtime reconnect bug"）
- 一个 Topic 可关联 0..N WorkItem + N Execution + N CollaborationRequest
- Topic **不**维护 IMPLEMENTING / REVIEWING / FIXING 等状态（这些只是 UI projection，见 #1 I-4）

```sql
-- 扩展 E3 messages 表（v0.1.1 迁移，additive）
ALTER TABLE messages ADD COLUMN topic_id UUID;
ALTER TABLE messages ADD COLUMN topic_root_seq BIGINT;
ALTER TABLE messages ADD COLUMN topic_closed_at TIMESTAMPTZ;
ALTER TABLE messages ADD COLUMN topic_summary TEXT;
ALTER TABLE messages ADD COLUMN needs_human BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE messages ADD COLUMN needs_human_question_id UUID;  -- ref to memory_items (kind=MISSION_GRILL_QUESTION / MISSION_BLOCKED_QUESTION / AD_HOC)

CREATE INDEX idx_messages_topic ON messages(channel_id, topic_id, seq)
  WHERE topic_id IS NOT NULL;
CREATE INDEX idx_messages_needs_human ON messages(channel_id, created_at DESC)
  WHERE needs_human = true;

-- Channel 层 summary（v0.1 简化：派生 view，v0.2 物化）
CREATE VIEW channel_topics_v AS
SELECT
  channel_id,
  topic_id,
  MIN(seq) FILTER (WHERE topic_root_seq IS NOT NULL) AS root_seq,
  MAX(seq) AS latest_seq,
  COUNT(*) AS reply_count,
  MAX(topic_closed_at) AS closed_at,
  -- 找 needs_human 问题
  BOOL_OR(needs_human) AS has_needs_human,
  -- 取最新 needs_human 问题的 memory_items ref
  (SELECT needs_human_question_id
   FROM messages m2
   WHERE m2.channel_id = m.channel_id
     AND m2.topic_id = m.topic_id
     AND m2.needs_human = true
   ORDER BY m2.seq DESC LIMIT 1) AS latest_needs_human_question_id
FROM messages m
WHERE topic_id IS NOT NULL
GROUP BY channel_id, topic_id;
```

### 1.2 字段语义

| 字段 | 用途 |
| --- | --- |
| `topic_id` | 同一 Topic 的所有消息共享；NULL = 自由消息（不属于任何 Topic） |
| `topic_root_seq` | 同一 Topic 的 root 消息 seq；用于快速定位 Topic 起点 |
| `topic_closed_at` | Topic close 时间；NULL = open |
| `topic_summary` | PO / Agent 写的 Topic 摘要（optional） |
| `needs_human` | 该消息（含 root）是否触发 Needs You |
| `needs_human_question_id` | 关联的 `memory_items.id`（kind 含 MISSION_GRILL_QUESTION / MISSION_BLOCKED_QUESTION / AD_HOC） |

### 1.3 Topic 创建（任何 member 在 Channel 发 root message）

```python
async def post_message(channel_id, actor, body, mentions, is_root=False):
    # 1. 写 messages（E3 标准路径）
    msg = await channel_service.post(channel_id, actor, body, mentions)

    # 2. 如果是 root，生成 topic_id + topic_root_seq
    if is_root:
        topic_id = uuid4()
        await db.execute("""
            UPDATE messages
            SET topic_id = $2, topic_root_seq = seq
            WHERE id = $1
        """, msg.id, topic_id)
        # OUTBOX topic.opened

    return msg
```

### 1.4 Topic 关闭

```python
async def close_topic(topic_id: UUID, actor: Actor, summary: str = None):
    """Topic close = 设 root message 的 topic_closed_at"""
    affected = await db.execute("""
        UPDATE messages
        SET topic_closed_at = NOW(), topic_summary = COALESCE($2, topic_summary)
        WHERE topic_id = $1 AND topic_root_seq IS NOT NULL
    """, topic_id, summary)
    # 不删消息；UI 显示「Closed」badge
```

## 2. Topic 状态机

```
(open)  ─── close_topic() ──►  (closed)
   ▲                              │
   │                              │ reopen_topic() (PO 显式)
   └──────────────────────────────┘
```

**关键约束**：
- Topic close **不**取消关联的 WorkUnit / Execution（WorkUnit 独立 lifecycle）
- Topic close **不**删消息（append-only）
- Topic reopen 仅 PO 可（audit）

### 2.1 Topic 与 Mission / WorkUnit 的关系

| 关系 | 落点 | 谁来连 |
| --- | --- | --- |
| Mission 启动后，Planner 创建默认 Topic | `topics` metadata.mission_id = mission_id | Planner 自动 |
| WorkUnit → Topic | `work_units.metadata.topic_ids[]` | Planner / Agent 显式 |
| CollaborationRequest → Topic | `collaboration_requests.context_refs.topic_id` | E4 标准 context_refs |
| Execution → Topic | `agent_executions.context_refs.topic_id` | E7 标准 context_refs |

**Topic 是"看见"的窗口，WorkUnit / CR / Execution 是"工作"的事实源**。

## 3. Needs You 数据模型

### 3.1 Needs You = notifications 按 category 过滤的视图

**不**新加表。复用 E10 `notifications` 表 + 加 `category` + `urgency` 字段：

```sql
-- 扩展 E10 notifications 表
ALTER TABLE notifications ADD COLUMN category TEXT NOT NULL DEFAULT 'GENERAL';
-- CHECK: 'GENERAL' | 'NEEDS_HUMAN' | 'APPROVAL' | 'MEMBER' | 'WORK_ITEM' | 'MENTION'
ALTER TABLE notifications ADD COLUMN urgency TEXT NOT NULL DEFAULT 'NORMAL';
-- CHECK: 'LOW' | 'NORMAL' | 'HIGH' | 'CRITICAL'
ALTER TABLE notifications ADD COLUMN question_id UUID;  -- ref to memory_items (MISSION_GRILL_QUESTION / MISSION_BLOCKED_QUESTION / AD_HOC)
ALTER TABLE notifications ADD COLUMN mission_id UUID;
ALTER TABLE notifications ADD COLUMN work_unit_id UUID;
ALTER TABLE notifications ADD COLUMN context_refs JSONB NOT NULL DEFAULT '{}';

CREATE INDEX idx_notifications_needs_human ON notifications(recipient_id, created_at DESC)
  WHERE category = 'NEEDS_HUMAN' AND read_at IS NULL;
```

### 3.2 写 Needs You 通知

```python
async def write_needs_human_notification(
    recipient_id: UUID,
    question_id: UUID,
    mission_id: UUID = None,
    work_unit_id: UUID = None,
    urgency: str = 'NORMAL',
    context_refs: dict = None,
):
    await db.execute("""
        INSERT INTO notifications
        (recipient_id, category, urgency, question_id, mission_id, work_unit_id, context_refs, created_at)
        VALUES ($1, 'NEEDS_HUMAN', $2, $3, $4, $5, $6, NOW())
    """, recipient_id, urgency, question_id, mission_id, work_unit_id, context_refs or {})

    # OUTBOX notification.created → E3 WS 推送给 recipient
```

### 3.3 Needs You 触发方

| 触发 | 谁写 | 例子 |
| --- | --- | --- |
| Mission Grill 产生 `needs_human_questions` | Mission Planner | "Should we migrate existing Built-in WorkItems to Jira?" |
| WU 阻塞 | Delivery WU Service | Agent 推 handoff.requested=BLOCKED with reason='need_clarification' |
| Mission 决策需要 PO | Delivery Mission Service | "Mission conflict: Story A 改 WU 改了 Story B 接口" |
| Memory approval | E5 memory_proposals | 现有 approval 通知（re-categorize 为 NEEDS_HUMAN） |
| Policy REQUIRED action | Policy evaluate | "Project policy escalation: production change" |

## 4. Needs You 聚合算法

### 4.1 目标

PO 不应该看到几十条散乱通知。**同 Mission / 同 WU 的 Needs You 聚合为 1 张卡**。

### 4.2 聚合维度

| 维度 | 聚合键 | 显示 |
| --- | --- | --- |
| Mission 级 | `mission_id` | "Jira Provider Mission · 1 question" |
| WU 级 | `work_unit_id` | "WU-3 (Provider Interface) · blocked" |
| Topic 级 | `context_refs.topic_id` | "Topic: Memory approval · 2 questions" |
| 无聚合键 | 单条 | — |

### 4.3 聚合查询（UI 「Needs You」页）

```sql
-- 1. Mission 级聚合
SELECT
  mission_id,
  COUNT(*) AS question_count,
  MAX(urgency) AS max_urgency,  -- 用 urgency 序数值取 max
  MIN(created_at) AS first_asked,
  MAX(created_at) AS last_asked,
  -- 取所有 question_id 数组
  array_agg(question_id ORDER BY created_at) AS question_ids
FROM notifications
WHERE recipient_id = $1
  AND category = 'NEEDS_HUMAN'
  AND read_at IS NULL
  AND mission_id IS NOT NULL
GROUP BY mission_id
ORDER BY max_urgency DESC, last_asked DESC;

-- 2. WU 级聚合（同 Mission 或独立）
SELECT
  COALESCE(mission_id, '__orphan__') AS mission_key,
  work_unit_id,
  ...
FROM notifications
WHERE recipient_id = $1
  AND category = 'NEEDS_HUMAN'
  AND read_at IS NULL
  AND work_unit_id IS NOT NULL
GROUP BY COALESCE(mission_id, '__orphan__'), work_unit_id;

-- 3. Union + sort by max_urgency
```

### 4.4 UI 投影（v0.1 简化）

```ts
// Needs You 页
interface NeedsYouCard {
  id: string;                         // 聚合键
  groupBy: 'mission' | 'work_unit' | 'topic' | 'none';
  title: string;                       // Mission title / WU title / Topic title
  questionCount: number;
  maxUrgency: 'LOW' | 'NORMAL' | 'HIGH' | 'CRITICAL';
  firstAsked: string;
  lastAsked: string;
  questions: NeedsYouQuestion[];       // 展开列表
}

interface NeedsYouQuestion {
  id: string;                          // question_id (memory_items.id)
  content: string;                     // 来自 memory_items.content.question
  options: { label: string; value: string }[];  // 来自 memory_items.content.options
  recommendation?: string;             // 来自 memory_items.content.recommendation
  impact?: string;                     // 来自 memory_items.content.impact
}
```

### 4.5 答完一个 question 后的状态

- UPDATE `memory_items.answer` + `answered_at` + `answered_by`
- 答完的 question 从 Needs You 卡聚合中**消失**（但 notification 记录保留，UI 显示已回答）
- 若该卡是某 Mission 最后 1 个 question，Mission 可自动从 GRILLED → PO 触发 /start

## 5. Assignment Mode 3 态扩展

### 5.1 v0.3.2 Resolver 现状

v0.3.2 详细设计 §4 有 2 态分支：
- `target_agent_id` 显式 → DIRECT
- 否则 Resolver 选 → ROUTED

v0.1 新增 OPEN_CLAIM 3 态。

### 5.2 扩展 schema

```sql
-- 扩展 E4 collaboration_requests（v0.1.1 迁移）
ALTER TABLE collaboration_requests ADD COLUMN assignment_mode TEXT NOT NULL DEFAULT 'ROUTED';
-- CHECK: 'DIRECT' | 'ROUTED' | 'OPEN_CLAIM'

-- 扩展 work_offers（已在 [03 §4](./03-scheduler-autonomy-policy.md) 定义，引用此 mode）
-- work_offers.collaboration_request_id → collaboration_requests
-- 同一 CR 仅 OPEN_CLAIM 模式有 work_offer
```

### 5.3 三态行为

| mode | 谁来 | 触发 | 路径 |
| --- | --- | --- | --- |
| **DIRECT** | PO / System 显式指定 | `@Backend-Agent` Mention 命中特定 agent | E4 Resolver 跳过评分，直接 CR (target_agent_id=指定) |
| **ROUTED** | E4 Resolver 选 | Mission policy.assignment_mode=ROUTED | E4 Resolver 按能力 + load + history 评分选 top-1 |
| **OPEN_CLAIM** | Agent 主动 claim | Mission policy.assignment_mode=OPEN_CLAIM | Scheduler 发 work_offer；Agent claim → E4 CR (target_agent_id=claimer) |

### 5.4 mission.policy 联动

```yaml
# project_policies.policy_yml 新增段
assignment:
  default_mode: ROUTED                # ROUTED | OPEN_CLAIM
  per_mission_override: true          # Mission 可独立指定 mode
  per_work_unit_override: false       # WU 级不覆盖（统一 Mission mode）
```

`missions.assignment_mode` 字段（启动时拍 policy snapshot 决定）：

```sql
ALTER TABLE missions ADD COLUMN assignment_mode TEXT NOT NULL DEFAULT 'ROUTED';
-- CHECK: 'DIRECT' | 'ROUTED' | 'OPEN_CLAIM'
```

### 5.5 assignment_mode 决定路径

```python
async def create_collaboration_request(trigger: Trigger, mission: Mission = None) -> CollaborationRequest:
    # 1. mission 级 mode
    if mission:
        mode = mission.assignment_mode
    else:
        # 2. Mention 等非 mission 触发 → 总是 DIRECT（@特定 agent）或 ROUTED
        if trigger.target_agent_id:
            mode = 'DIRECT'
        else:
            mode = 'ROUTED'

    # 3. 创建 CR（按 mode 不同路径）
    if mode == 'DIRECT':
        return await create_cr_direct(trigger, trigger.target_agent_id)
    elif mode == 'ROUTED':
        return await create_cr_routed(trigger)
    elif mode == 'OPEN_CLAIM':
        # 不直接创建 CR；发布 work_offer（由 Agent claim 触发）
        return await publish_work_offer(trigger)
```

**关键不变量**（#1 I-7）：OPEN_CLAIM 模式下**不**直接创建 CR；CR 是 Agent claim work_offer 的副作用。

## 6. Handoff 协议

### 6.1 Handoff 是什么

Agent 完成一棒后，**主动**产生下一棒（"Implementation 完了，请 reviewer 接手"），不依赖外部 Trigger。

### 6.2 v0.1 协议设计

```ts
// Agent 端：推 handoff.requested
{
  "type": "handoff.requested",
  "payload": {
    "from_execution_id": "uuid",          // 必填：来源 Execution
    "from_work_unit_id": "uuid?",         // optional
    "purpose": "REVIEW" | "FIX" | "QA" | "DOCS" | "ESCALATE",
    "required_capabilities": ["review", "database"]?,
    "preferred_agent_ids": ["uuid"]?,     // 优先级 1
    "excluded_agent_ids": ["uuid"]?,      // 必排除
    "context_snapshot_id": "uuid",        // 见 §6.4
    "artifact_refs": ["pr:123", "s3://..."]?,  // 必填
    "block_until_human_review": false,
    "reason": "string"
  }
}

// Server 端：返回 handoff.ack（同步）
{
  "type": "handoff.ack",
  "payload": {
    "handoff_id": "uuid",
    "collaboration_request_id": "uuid",  // 创建成功的 CR id
    "status": "ACCEPTED" | "REJECTED" | "PENDING"
  }
}
```

### 6.3 时序

```
T+0   Agent A 推 handoff.requested { purpose: 'REVIEW', from_execution_id: E1 }
T+1   Runtime Gateway 验证 Agent A 拥有 E1（避免伪造）
T+2   Runtime Gateway: BEGIN transaction:
        a) 写 handoffs 表 (status=PROCESSING, from_execution_id=E1)
        b) 拍 context_snapshot（拉 E1 + WU + Mission 上下文）
        c) INSERT outbox_events (event_type='handoff.requested', payload={handoff_id, ...})
T+3   COMMIT
T+4   Runtime Gateway: 立即推 handoff.ack { handoff_id, status: 'PENDING' }
T+5   E4 Outbox Worker 拾取 handoff.requested
T+6   E4 Orchestrator:
        a) 解析 purpose → assignment_mode
        b) DIRECT: 选 preferred_agent_ids[0] / excluded 后 Resolver
        c) ROUTED: 走 Resolver 评分
        d) OPEN_CLAIM: 走 Scheduler（publish_work_offer）
        e) 创建 collaboration_requests (context_refs.handoff_id=handoffs.id)
        f) 走标准 E4 → E7 流程
T+7   E4: UPDATE handoffs.status='RESOLVED' + collaboration_request_id
T+8   Agent A 收 handoff.ack (retry via WS)
```

### 6.4 context_snapshot

```sql
-- context_snapshots（Handoff / Mission 启动时拍快照，避免后续依赖变）
CREATE TABLE context_snapshots (
  id              UUID PRIMARY KEY,
  snapshot_kind   TEXT NOT NULL CHECK (snapshot_kind IN ('HANDOFF','MISSION_START','WORK_UNIT_READY')),
  source_ref      JSONB NOT NULL,        -- {execution_id?, work_unit_id?, mission_id?}
  content         JSONB NOT NULL,        -- 见 §6.4.1
  created_at      TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_cs_source ON context_snapshots USING GIN (source_ref);
```

#### 6.4.1 context_snapshot content schema

```ts
interface ContextSnapshot {
  // 来自 Execution
  execution?: {
    id: string;
    input: object;
    output: object;
    artifacts: Artifact[];
    usage: { tokens_in: number; tokens_out: number; duration_ms: number };
  };

  // 来自 WorkUnit
  work_unit?: {
    id: string;
    title: string;
    description: string;
    required_capabilities: string[];
  };

  // 来自 Mission
  mission?: {
    id: string;
    goal: string;
    briefs: BriefRef[];            // 引用 briefs.id（不复制 content）
  };

  // 来自 Topic
  topic?: {
    id: string;
    recent_messages: MessageRef[];  // 最近 10 条引用
  };

  // 来自 Memory
  memory_refs: { ref_id: string; excerpt: string }[];

  // 来自 Channel
  channel_recent: MessageRef[];

  // 决策 audit
  decision_history: DecisionRecord[];
}
```

### 6.5 handoffs 表

```sql
CREATE TABLE handoffs (
  id                      UUID PRIMARY KEY,
  from_execution_id       UUID NOT NULL REFERENCES agent_executions(id),
  from_work_unit_id       UUID REFERENCES work_units(id),
  from_agent_id           UUID NOT NULL REFERENCES agents(id),
  purpose                 TEXT NOT NULL CHECK (purpose IN ('REVIEW','FIX','QA','DOCS','ESCALATE')),
  required_capabilities   JSONB NOT NULL DEFAULT '[]',
  preferred_agent_ids     JSONB NOT NULL DEFAULT '[]',
  excluded_agent_ids      JSONB NOT NULL DEFAULT '[]',
  artifact_refs           JSONB NOT NULL DEFAULT '[]',
  context_snapshot_id     UUID REFERENCES context_snapshots(id),
  reason                  TEXT,
  block_until_human_review BOOLEAN NOT NULL DEFAULT false,
  status                  TEXT NOT NULL DEFAULT 'PROCESSING'
                          CHECK (status IN ('PROCESSING','RESOLVED','FAILED','CANCELLED')),
  collaboration_request_id UUID REFERENCES collaboration_requests(id),
  error_message           TEXT,
  created_at              TIMESTAMPTZ DEFAULT now(),
  resolved_at             TIMESTAMPTZ
);
CREATE INDEX idx_handoffs_status ON handoffs(status, created_at DESC);
```

### 6.6 关键边界（#1 I-8）

**禁止**：
- Agent 直接调 E4 API 创建 CR
- Agent 直接调 E7 API 创建 Execution
- Agent 直接写 `collaboration_requests` 表

**正确**：
- Agent 推 `handoff.requested` 协议消息
- Runtime Gateway 落 handoffs 表 + OUTBOX
- E4 Outbox Worker 创建 CR
- 标准 E4 → E7 流程

### 6.7 Handoff 失败重试

- `handoffs.status='PROCESSING'` 超时（默认 5 分钟）→ FAILED + 通知 Agent
- Agent 可推 `handoff.cancel` 撤销（仅 PROCESSING 状态）

## 7. Domain Events

| aggregate | event_type | payload 关键字段 | 何时 | 投递目标 |
| --- | --- | --- | --- | --- |
| `topic` | `topic.opened` | channel_id, root_message_seq, topic_id | root message 创建后 | E3 WS 广播 |
| `topic` | `topic.closed` | topic_id, closed_by, summary | close_topic | E3 WS 广播 |
| `topic` | `topic.needs_human` | topic_id, work_unit_id?, question_id | Needs You 写入 | E10 监听 |
| `handoff` | `handoff.requested` | handoff_id, from_execution_id, purpose, required_capabilities, context_snapshot_id | E4 Outbox Worker 拾取 | E4 监听 → 创建 CR |
| `handoff` | `handoff.resolved` | handoff_id, collaboration_request_id, status | E4 创建 CR 后 | Runtime Gateway 监听 → 推 handoff.ack 给 Agent |
| `handoff` | `handoff.failed` | handoff_id, error_message | E4 处理失败 | E10 监听 → 通知 Agent |

## 8. 关键边界（与 #1 9 Invariants 联动）

| Invariant | 本文落地 |
| --- | --- |
| **I-6** Topic 是 Channel 内的协作会话，**不**新加 entity | §1.1 复用 messages 字段；§1.3 topic_id 自动生成；§1.4 close 不删消息 |
| **I-7** Open Claim 仍走 E4 CollaborationRequest | §5.5 OPEN_CLAIM 不直接创建 CR；work_offer claim 是 CR 的副作用 |
| **I-8** Handoff 协议 Agent 不直接创建 CollaborationRequest | §6.6 禁止清单；§6.3 时序：handoff.requested → OUTBOX → E4 Worker → CR |
| **#1 §3.1 Topic/Needs You/Open Claim 不允许做的事** | §1.4 Topic close 不取消 WU/Execution；§3 Needs You 复用 notifications；§5.4 OPEN_CLAIM 不在 E6 Guard 走 |

## 9. 反模式

| 反模式 | 后果 | 正确做法 |
| --- | --- | --- |
| **topics 新加表** | 与 E3 channel 重复 | 复用 messages 字段（§1.1） |
| **Topic close 删消息** | append-only 破坏 | 设 topic_closed_at（§1.4） |
| **Topic 维护 IMPLEMENTING/REVIEWING 状态** | 与 WU/Execution 状态机重复 | UI projection（#1 I-4） |
| **Needs You 新加表** | 重复 notifications | 复用 notifications + category 字段（§3.1） |
| **Needs You 卡片按 question 1:1 显示** | PO 看到几十条散乱 | §4 聚合（Mission / WU / Topic 级） |
| **Agent 直接调 E4 API 创建 CR** | 协议破坏；outbox 兜底失效 | 推 handoff.requested；E4 Worker 创建 |
| **OPEN_CLAIM 直接创建 CR** | 跳过 Resolver 协议 | work_offer claim 是 CR 的副作用（§5.5） |
| **Handoff 不拍 context_snapshot** | 后续依赖变导致不可重现 | §6.4 必拍快照 |
| **Mission assignment_mode 让 WU 单独覆盖** | 模式混乱 | §5.4 统一 Mission mode |
| **Handoff.processing 永不超时** | 永久卡 PROCESSING | §6.7 默认 5 分钟超时 → FAILED |

## 10. e2e 验收点

```
e2e/autonomous-delivery/04-topic-needsyou-assignment-handoff/
  test_001_topic_reuses_messages_no_new_table.json
    Given Channel C
    When  User 发 root message M
    Then  messages.topic_id 自动生成 UUID
    And   messages.topic_root_seq = M.seq
    And   无 topics 新表

  test_002_topic_close_does_not_delete_messages.json
    Given Topic T with 5 messages
    When  POST /channels/:c/topics/:t/close
    Then  5 messages 仍存在
    And   root message.topic_closed_at 落表
    And   UI 显示 "Closed" badge

  test_003_needs_you_aggregates_mission_level.json
    Given Mission M with 3 needs_human_questions
    When  PO GET /needs-you
    Then  1 张聚合卡（M-level）
    And   questionCount=3
    And   展开后看到 3 个 question 详情

  test_004_needs_you_aggregates_wu_level.json
    Given WU-1 BLOCKED, 2 questions 关联
    When  PO GET /needs-you
    Then  1 张 WU-level 聚合卡
    And   questionCount=2

  test_005_answered_question_disappears_from_aggregation.json
    Given 1 聚合卡 with 3 questions
    When  PO 答 1 question
    Then  该聚合卡 questionCount=2
    And   已答 question 显示 "answered" 状态

  test_006_assignment_mode_direct_skips_resolver.json
    Given CR trigger with target_agent_id=A
    When  E4 create CR
    Then  assignment_mode=DIRECT
    And   E4 Resolver **不**被调
    And   target_agent_id=A

  test_007_assignment_mode_routed_uses_resolver.json
    Given CR trigger without target_agent_id
    When  E4 create CR
    Then  assignment_mode=ROUTED
    And   E4 Resolver 评分选 top-1

  test_008_assignment_mode_open_claim_creates_offer.json
    Given Mission policy.assignment_mode=OPEN_CLAIM
    When  WU-1 → READY
    Then  work_offer ×1 发布 (status=OPEN)
    And   collaboration_requests **不**直接创建

  test_009_open_claim_claim_creates_cr.json
    Given work_offer OPEN
    When  Agent A claim
    Then  work_offer CAS OPEN → CLAIMED
    And   E4 创建 CR (assignment_mode=OPEN_CLAIM, target_agent_id=A)
    And   CR 走标准 E4 → E7 路径

  test_010_handoff_via_outbox_not_direct.json
    Given Execution E1 SUCCEEDED
    When  Agent A 推 handoff.requested
    Then  handoffs 表落 1 行 (status=PROCESSING)
    And   Agent A **不**调 E4 / E7 API
    And   OUTBOX handoff.requested 投递
    And   E4 Outbox Worker 创建 CR
    And   handoffs.status=RESOLVED

  test_011_handoff_context_snapshot_immutable.json
    Given handoff 拍 context_snapshot_id=S1
    When  Mission M 改 goal
    Then  S1.content.mission.goal 仍原值（snapshot 不可变）
    And   新 handoff 拍 S2 含新 goal

  test_012_handoff_processing_timeout_fails.json
    Given handoff status=PROCESSING
    When  5 分钟未 RESOLVED
    Then  handoff.status=FAILED
    And   Agent A 收通知

  test_013_handoff_purpose_routing.json
    Given handoff.requested { purpose: 'REVIEW' }
    Then  E4 选 reviewer 候选（capability ⊇ ['review']）
    And   policy.review.different_agent_required → 排除 from_agent_id

  test_014_topic_links_to_work_unit.json
    Given Topic T 关联 WU-1
    When  Planner 创建 WU-1
    Then  WU-1.metadata.topic_ids=[T.id]
    And   Topic 详情页看到 WU-1 卡片

  test_015_needs_you_urgency_priority.json
    Given 3 needs_human: 1 CRITICAL, 1 HIGH, 1 NORMAL
    When  PO GET /needs-you
    Then  排序：CRITICAL > HIGH > NORMAL
```

## 11. 实施 M13 子任务

| 子任务 | 内容 | 依赖 |
| --- | --- | --- |
| M13.1 | DB migration: messages 扩字段 + notifications 扩字段 | — |
| M13.2 | Topic Service (create / close / reopen + view channel_topics_v) | M13.1, E3 |
| M13.3 | Needs You Service (聚合查询 + 答 question) | M13.1, E10 |
| M13.4 | collaboration_requests 扩字段 assignment_mode | E4 |
| M13.5 | E4 Resolver 改 3 态分支 (DIRECT/ROUTED/OPEN_CLAIM) | M13.4 |
| M13.6 | missions 扩字段 assignment_mode | M12, [01 §1](./01-mission-and-grill.md) |
| M13.7 | handoffs 表 + context_snapshots 表 | — |
| M13.8 | Runtime Gateway: 收 handoff.requested → OUTBOX | M13.7, E7 |
| M13.9 | E4 Outbox Worker: 拾 handoff.requested → 创建 CR | M13.5, M13.7 |
| M13.10 | Mission.start 联动 assignment_mode | M12, [01 §7](./01-mission-and-grill.md) |
| M13.11 | OUTBOX event handlers (topic.* / handoff.*) | M13.2, M13.8, M13.9 |
| M13.12 | REST API: /topics /needs-you /handoffs | M13.2, M13.3, M13.7 |
| M13.13 | e2e: 15 个验收点 | M13.12 |

**Vertical slice 必备**：M13.1 + M13.2 + M13.3 + M13.12 第 1-2 项应在 M13 早期就端到端跑通（root message → Topic 聚合 → Needs You 卡），避免 v0.4 重蹈"vertical slice 排末尾"覆辙。

**关键：M13 与 M10-M12 大量耦合**（Handoff 用 work_unit / mission / policy / offer）。M13 实施时**应**先冻结 #1 Invariants，避免 v0.4 推倒覆辙。
