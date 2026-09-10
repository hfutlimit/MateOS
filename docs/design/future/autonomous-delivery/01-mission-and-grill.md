# Detailed Design · Autonomous Delivery · 01 · Mission & Grill & Brief

> **配套**：PRD v0.4 / SYSTEM_DESIGN v0.3.2 / [00-integration-map.md](./00-integration-map.md) / v0.1 Architecture Proposal §6-9
> **范围**：PO 选多 Story → Mission 创建 → Mission Grill（cross-story analysis）→ Requirement Briefs（versioned）→ Mission 启动
> **前置**：[00-integration-map.md](./00-integration-map.md) **（必读，9 Invariants + 边界对照）**
> **本文不覆盖**：WorkUnit 拆解（见 [02-workunit-and-delivery-graph.md](./02-workunit-and-delivery-graph.md)）/ Scheduler（见 [03-scheduler-autonomy-policy.md](./03-scheduler-autonomy-policy.md)）/ Topic（见 [04-topic-needsyou-and-assignment.md](./04-topic-needsyou-and-assignment.md)）

## 0. 文档结构

- **§1** Mission 概念与数据模型（missions / mission_work_item_refs）
- **§2** Mission 状态机
- **§3** Mission Grill 流程（cross-story analysis）
- **§4** Grill 状态机
- **§5** Requirement Brief 数据模型（briefs / brief_revisions）
- **§6** Brief 版本化与 Diff
- **§7** Mission 启动（PO start → Planner）
- **§8** Mission Planner 服务（NestJS 模块）
- **§9** Domain Events
- **§10** 关键边界（与 #1 Invariants 联动）
- **§11** 反模式
- **§12** e2e 验收点
- **§13** 实施 M10 子任务

## 1. Mission 概念与数据模型

### 1.1 Mission = 一次 Delivery 目标

Mission 是一组 Story / WorkItem 共同完成的目标。**不**拥有 Story，只引用。

```sql
-- missions（核心事实源）
CREATE TABLE missions (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id),
  title               TEXT NOT NULL,
  goal                TEXT NOT NULL,                  -- PO 自述
  objective           TEXT NOT NULL DEFAULT 'FASTEST_COMPLETION'
                      CHECK (objective IN ('FASTEST_COMPLETION','BALANCED','LOWEST_COST')),
  autonomy_level      INT NOT NULL DEFAULT 2
                      CHECK (autonomy_level IN (0,1,2,3)),
  policy_snapshot_id  UUID REFERENCES project_policies(id),  -- 启动时 policy 快照
  status              TEXT NOT NULL DEFAULT 'CREATED'
                      CHECK (status IN ('CREATED','GRILLING','GRILLED','PLANNING','RUNNING','REPLANNING','COMPLETED','CANCELLED','FAILED')),
  created_by_type     TEXT NOT NULL CHECK (created_by_type IN ('USER','AGENT')),
  created_by_id       UUID NOT NULL,
  started_at          TIMESTAMPTZ,
  completed_at        TIMESTAMPTZ,
  stats               JSONB NOT NULL DEFAULT '{}',     -- {work_units_total, work_units_done, work_units_blocked, ...}
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_missions_project_status ON missions(project_id, status, created_at DESC);

-- mission_work_item_refs（M:N 引用；Mission 不 owns Story）
CREATE TABLE mission_work_item_refs (
  id              UUID PRIMARY KEY,
  mission_id      UUID NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
  work_item_ref   JSONB NOT NULL,                     -- {provider_key, work_item_id, external_ref} 与 E8 一致
  ref_role        TEXT NOT NULL DEFAULT 'PRIMARY' CHECK (ref_role IN ('PRIMARY','DEPENDENCY','CONTEXT')),
  added_at        TIMESTAMPTZ DEFAULT now(),
  UNIQUE (mission_id, work_item_ref)
);
CREATE INDEX idx_mwir_mission ON mission_work_item_refs(mission_id);
```

### 1.2 Mission Stats（实时聚合）

stats 字段是**派生**（derived）属性，**不**是事实源。真实统计从 work_units / execution_events 聚合：

```sql
-- Mission 启动时初始化空
stats = {"work_units_total": 0, "work_units_done": 0, "work_units_blocked": 0, "executions_total": 0, "needs_human_count": 0}

-- 每次 work_unit.completed / blocked 事件后由 Delivery 域聚合更新
-- v0.1 简化：每次事件触发一次 UPDATE missions.stats（避免再加事实表）
-- v0.2 优化：单独的 mission_stats 物化视图
```

**反模式**：禁止业务逻辑直接读 `missions.stats` 做决策；**必须**重新查 work_units 表（stats 仅供 UI 快速显示）。

## 2. Mission 状态机

```
                     ┌──────────┐
                     │ CREATED  │ (PO POST /missions)
                     └─────┬────┘
                           │ OUTBOX mission.created
                           ▼
                     ┌──────────┐
              ┌─────►│ GRILLING │ (Planner 拉 WorkItem 上下文 + 调 LLM cross-story analysis)
              │      └─────┬────┘
              │            │
              │            │ 写 briefs + needs_human_questions
              │            ▼
              │      ┌──────────┐
              │      │ GRILLED  │ (PO 看到 3 needs_human_questions)
              │      └─────┬────┘
              │            │ PO 全部回答 + POST /missions/:id/start
              │            ▼
              │      ┌──────────┐
              │      │ PLANNING │ (Planner 拆 WU + 建依赖)
              │      └─────┬────┘
              │            │ OUTBOX mission.started
              │            ▼
              │      ┌──────────┐
              │      │ RUNNING  │ (Scheduler 发布 work_offers; Agent claim + 执行)
              │      └─────┬────┘
              │            │                 ┌─────────────────┐
              │            │ 跨 story 矛盾     │ REPLANNING      │
              │            ├────────────────►│ (Planner 重新   │
              │            │                 │  拆/合 WU)     │
              │            │                 └────────┬────────┘
              │            │                          │
              │            │                          ▼
              │            │                 ┌─────────────────┐
              │            │                 │ 回到 RUNNING    │
              │            │                 └─────────────────┘
              │            │
              │            │ 所有 WU.completed 且 无 blocked
              │            ▼
              │      ┌──────────┐
              │      │COMPLETED │──► OUTBOX mission.completed
              │      └──────────┘
              │
              │ PO 取消 / 管理员取消（仅 CREATED/GRILLING/GRILLED/PLANNING/RUNNING 可）
              └────────►┌───────────┐
                       │ CANCELLED │
                       └───────────┘

任何状态：
  Planner 失败 → FAILED（人工介入）
  Manager 取消 → CANCELLED
```

**关键约束**（与 #1 I-1 一致）：
- **Mission 状态变更不直接改 WorkItem.status**（通过 OUTBOX → E8）
- **CANCELLED 不回滚已 COMPLETED 的 WU**（仅标 in-flight WU 为 CANCELLED）
- **REPLANNING 不重置 COMPLETED WU**（见 [02 §4](./02-workunit-and-delivery-graph.md)）

## 3. Mission Grill 流程

### 3.1 Grill 的目标

PO 选 N 个 Story 后，**不**让不同 Agent 分别独立轰炸 PO。Grill 一次性做 cross-story analysis：

1. **Requirement ambiguity**：每个 Story 是否 Acceptance Criteria 完整
2. **Cross-story contradiction**：Story 之间是否冲突
3. **Shared architecture decision**：哪些 Story 共享架构决策
4. **Hidden dependency**：未显式声明的依赖
5. **Duplicate work**：是否在多个 Story 重做

### 3.2 流程时序

```
T+0   PO POST /missions { work_item_refs: [RF-201, RF-203, RF-209, RF-215, RF-221] }
T+1   Delivery: INSERT missions (status=CREATED) + mission_work_item_refs ×5
T+2   Delivery: OUTBOX mission.created
T+3   Delivery: missions.status = GRILLING

T+4   E4 Outbox Worker 拾取 mission.created
      └─ 调 Delivery Mission Planner 启动 Grill

T+5   Mission Planner.Grill(mission_id):
      ├─ 拉 5 个 WorkItem 详情（E8 read）
      ├─ 拉项目 Memory（E5 read）—— 找历史决策
      ├─ 拉 Channel 最近消息（E3 read）—— 找 PO 之前讨论
      └─ 调 LLM cross-story analysis
         ├─ Input: 5 WorkItem 内容 + 上下文
         ├─ Prompt: "分析 requirement ambiguity, contradiction, shared decision, hidden dependency, duplicate work"
         └─ Output: JSON { briefs: [...], needs_human_questions: [...] }

T+6   Mission Planner 写 briefs (status=GRILLED) + brief_revisions v1
      └─ 每个 Story 一份 Brief
T+7   Mission Planner 写 needs_human_questions[]
      └─ NOT 落 messages（PO 走 Needs You 页查看）
T+8   Mission Planner: missions.status = GRILLED
      └─ OUTBOX mission.grill_completed

T+9   E10 监听 mission.grill_completed
      └─ 写 notifications ×N（category=NEEDS_HUMAN, urgency=HIGH）

T+10  PO 登录 → 看到 Needs You 页
      └─ 3 questions 推荐 + 1 question 警告
      └─ PO 选 Keep Built-in / Migrate / Discuss

T+11  PO 回答 → E4: 写 decision_records（cross-cutting 决策）
      └─ 引 needs_human_question_id（保留 audit 链）

T+12  PO POST /missions/:id/start
T+13  Delivery: missions.status = PLANNING
      └─ OUTBOX mission.started

T+14  Mission Planner.Plan(mission_id):
      ├─ 读 briefs ×5 + 5 个 work_item_refs + 3 个 PO decisions
      ├─ 拆 work_units（每个 Story 至少 1 WU，复杂 Story 多 WU）
      ├─ 建 work_unit_dependencies（EXPLICIT 显式 / AGENT_INFERRED / HUMAN_CONFIRMED）
      ├─ 标 ready WU（无依赖或依赖已 satisfied）
      └─ OUTBOX work_unit.ready ×K
```

### 3.3 needs_human_questions 数据模型

```sql
-- 存在 memory_items（E5），不用新表
-- 通过 memory_items.metadata.kind = 'MISSION_GRILL_QUESTION' 标记
CREATE TABLE memory_items (
  id              UUID PRIMARY KEY,
  project_id      UUID NOT NULL,
  kind            TEXT NOT NULL DEFAULT 'FACT',  -- 'FACT' | 'DECISION' | 'MISSION_GRILL_QUESTION' | ...
  content         JSONB NOT NULL,                -- {question, options[], recommendation, impact, ...}
  source          JSONB NOT NULL,                -- E5 Source 三件套
  status          TEXT NOT NULL DEFAULT 'APPROVED',  -- GRILL_QUESTION 直接 APPROVED（无审批）
  answered_at     TIMESTAMPTZ,
  answer          JSONB,                          -- {selected_option, rationale}
  answered_by     UUID,
  created_at      TIMESTAMPTZ DEFAULT now()
);
```

**关键决策**：
- GRILL_QUESTION **不走 write_memory=REQUIRE_APPROVAL 门禁**（Mission 创建是正常产品流程）
- `status='APPROVED'` 直接入索引（E5 不阻塞）
- 回答时 UPDATE `memory_items.answer` + `answered_at` + `answered_by`
- **回答不创建 DECISION 类型 memory**（decision_records 是 E4 事实源；E5 memory 仅记录问题本身）

## 4. Grill 状态机（sub-state of Mission）

```
GRILLING  ─┬─► GRILLED     (LLM 分析完成 + briefs 全部写出)
           ├─► FAILED      (LLM 5xx 连续 3 次 / 内容校验失败)
           └─► CANCELLED   (PO / 管理员取消)
```

**超时**：GRILLING 默认 5 分钟超时（受 LLM 响应时间影响）。超时 → FAILED + 通知 PO 手动重试。

## 5. Requirement Brief 数据模型

```sql
-- briefs（一次 Grill 收敛出的版本化需求规约）
CREATE TABLE briefs (
  id                UUID PRIMARY KEY,
  project_id        UUID NOT NULL REFERENCES projects(id),
  mission_id        UUID NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
  work_item_ref     JSONB NOT NULL,             -- 关联的 WorkItem
  title             TEXT NOT NULL,
  current_revision_id UUID REFERENCES brief_revisions(id),
  status            TEXT NOT NULL DEFAULT 'GRILLED'
                    CHECK (status IN ('GRILLING','GRILLED','STALE','DEPRECATED')),
  created_at        TIMESTAMPTZ DEFAULT now(),
  updated_at        TIMESTAMPTZ DEFAULT now(),
  UNIQUE (mission_id, work_item_ref)
);

-- brief_revisions（versioned；append-only；最新 version 由 briefs.current_revision_id 索引）
CREATE TABLE brief_revisions (
  id              UUID PRIMARY KEY,
  brief_id        UUID NOT NULL REFERENCES briefs(id) ON DELETE CASCADE,
  revision_no     INT NOT NULL,
  content         JSONB NOT NULL,                -- 见 §5.1
  sources         JSONB NOT NULL DEFAULT '[]',   -- memory_items.id[] 引用
  decisions       JSONB NOT NULL DEFAULT '[]',   -- 决策引用（cross-cutting）
  diff_from_prev  JSONB,                          -- 字段级 diff（UI 显示）
  author_type     TEXT NOT NULL CHECK (author_type IN ('USER','AGENT','SYSTEM')),
  author_id       UUID,
  created_at      TIMESTAMPTZ DEFAULT now(),
  UNIQUE (brief_id, revision_no)
);
CREATE INDEX idx_brief_revisions_brief ON brief_revisions(brief_id, revision_no DESC);
```

### 5.1 Brief Content Schema

```ts
interface BriefContent {
  goal: string;                                  // 一句话
  acceptance_criteria: string[];                 // 验收条件
  constraints: string[];                         // 约束
  decisions: {                                   // 决策（带 provenance）
    statement: string;
    source: {                                    // E5 memory_items.id 或 Topic ref
      type: 'MEMORY_ITEM' | 'TOPIC' | 'WORK_ITEM_COMMENT';
      ref_id: string;
    };
    confirmed_by?: { type: 'USER' | 'AGENT'; id: string };
    confirmed_at?: string;                       // ISO8601
  }[];
  dependencies: {                                // 与其他 Story 的依赖
    work_item_ref: WorkItemRef;
    type: 'BLOCKS' | 'RELATES_TO';
    reason: string;
    source: 'EXPLICIT' | 'AGENT_INFERRED' | 'HUMAN_CONFIRMED';
    confidence?: 'LOW' | 'MEDIUM' | 'HIGH';
  }[];
  open_questions: string[];                      // 未解决的问题（与 GRILL_QUESTION 不同，存 brief 自身）
  out_of_scope: string[];                        // 明确排除
  sources: {                                     // 引用 memory_items
    type: 'MEMORY_ITEM';
    ref_id: string;
    excerpt: string;
  }[];
}
```

### 5.2 Brief 写入规则

- **Brief 写入不复制 memory_items.content**（§1 I-9 + v0.1 §19 provenance-first）—— 用 `sources: [{type:'MEMORY_ITEM', ref_id, excerpt}]` 引用
- Brief 修订不删除旧 revision（append-only `brief_revisions`）
- Brief 状态 `STALE` = 关联 WorkItem 变化（标题/描述/状态）超过阈值（configurable，默认 30% 字段变化）
- Brief 状态 `DEPRECATED` = Mission CANCELLED / Brief 被新 revision 取代后保留 30 天

## 6. Brief 版本化与 Diff

### 6.1 Diff 算法（v0.1 简化）

```ts
// v0.1: 字段级 shallow diff（不深入 nested object）
function diffBriefContent(prev: BriefContent, next: BriefContent): BriefDiff {
  const diff: BriefDiff = { added: [], removed: [], modified: [] };

  for (const key of Object.keys(next)) {
    if (!(key in prev)) {
      diff.added.push({ field: key, value: next[key] });
    } else if (JSON.stringify(prev[key]) !== JSON.stringify(next[key])) {
      diff.modified.push({
        field: key,
        before: prev[key],
        after: next[key]
      });
    }
  }

  for (const key of Object.keys(prev)) {
    if (!(key in next)) {
      diff.removed.push({ field: key, value: prev[key] });
    }
  }

  return diff;
}
```

V0.2 可升级为 array-aware diff（如 `acceptance_criteria` 数组）。

### 6.2 Diff 展示（UI DS v0.6+）

PO 看到 Brief revision 列表时：
- 颜色块：绿色 = added，红色 = removed，黄色 = modified
- Hover 显示 before/after
- 不展示 sources 数组 diff（仅在 "Provenance" 折叠面板）

## 7. Mission 启动（PO start → Planner）

### 7.1 启动校验

```ts
async function startMission(missionId: UUID, actor: Actor): Promise<void> {
  const mission = await db.getMission(missionId);

  // 1. 状态机：仅 GRILLED 可 start
  if (mission.status !== 'GRILLED') {
    throw new InvalidStateError(`Cannot start mission: status=${mission.status}`);
  }

  // 2. 校验所有 needs_human_questions 已回答
  const unanswered = await db.query(`
    SELECT id FROM memory_items
    WHERE project_id = $1
      AND kind = 'MISSION_GRILL_QUESTION'
      AND metadata->>'mission_id' = $2
      AND answered_at IS NULL
  `, mission.project_id, missionId);
  if (unanswered.length > 0) {
    throw new ValidationError(`${unanswered.length} questions unanswered`);
  }

  // 3. 拍 policy snapshot
  const policy = await projectPolicyService.getActive(mission.project_id);
  const policySnapshotId = await projectPolicyService.snapshot(policy, missionId);

  // 4. CAS mission status + 写 outbox（同事务）
  await db.transaction(async (tx) => {
    const affected = await tx.execute(`
      UPDATE missions
      SET status = 'PLANNING', policy_snapshot_id = $2, started_at = NOW(), updated_at = NOW()
      WHERE id = $1 AND status = 'GRILLED'
      RETURNING id
    `, missionId, policySnapshotId);

    if (!affected) throw new StaleStateError();

    await tx.execute(`
      INSERT INTO outbox_events (aggregate_type, aggregate_id, event_type, payload, idempotency_key)
      VALUES ('mission', $1, 'mission.started', $2, $3)
    `, missionId, { mission_id: missionId, policy_snapshot_id: policySnapshotId }, `mission-started-${missionId}`);
  });
}
```

### 7.2 启动后流程（PLANNING → RUNNING）

E4 Outbox Worker 拾取 `mission.started` → 调 Mission Planner.Plan(missionId)：

1. 读 briefs ×N
2. 调 LLM 拆 work_units（每个 Story 至少 1 WU；复杂 Story 多 WU）
3. 写 work_units（状态 PLANNED）
4. 写 work_unit_dependencies（source + confidence）
5. 标 ready WU → OUTBOX work_unit.ready ×K
6. CAS mission.status = RUNNING

后续见 [02-workunit-and-delivery-graph.md](./02-workunit-and-delivery-graph.md) §3-4。

## 8. Mission Planner 服务（NestJS 模块）

```
services/delivery/
  src/
    mission/
      mission.controller.ts        # REST: /api/v1/missions
      mission.service.ts           # 状态机 + 创建 / 启动 / 取消
      mission.repository.ts        # DB 读写
      grill.service.ts             # Grill 流程：拉 context + 调 LLM + 写 briefs
      grill.prompts.ts             # LLM prompt 模板
      brief.service.ts             # Brief CRUD + 版本化
    planner/
      planner.service.ts           # Plan(missionId) → 拆 WU + 建依赖
      work-unit.service.ts         # 单独 WU 操作（split / merge / add-dep）
      dependency.service.ts        # 依赖边
      readiness.service.ts         # 重新算 WU readiness（DAG 拓扑）
    events/
      mission.event-handlers.ts    # 订阅 mission.* / brief.* / work_unit.*
```

### 8.1 Mission Planner 责任

| 责任 | 不责任 |
| --- | --- |
| Mission / Brief / WorkUnit 域事实源 | 选 Agent（归 E4 Resolver） |
| Grill 流程（调 LLM cross-story analysis） | 调 LLM 处理单 Task（归 Agent Runtime） |
| 拆 WU + 建依赖（Planner） | 调度 WU 给 Agent（归 Scheduler） |
| 写 mission.* / brief.* / work_unit.* OUTBOX 事件 | 处理 OUTBOX 事件（归 E4 Outbox Worker） |

### 8.2 Grill LLM 约束

- 同一 Mission 多次 Grill 时使用相同 `grill_seed`（确保可复现）
- LLM 输出 schema 强校验（`goal / acceptance_criteria / decisions / dependencies / open_questions / out_of_scope / sources`）—— 缺关键字段则抛 ValidationError → FAILED
- LLM 5xx 连续 3 次 → FAILED + 通知 PO
- Grill 结果不可改：PO 想改 Goal 只能重 run Grill（创建新 Mission）

## 9. Domain Events

| aggregate | event_type | 何时 | 投递目标 |
| --- | --- | --- | --- |
| `mission` | `mission.created` | INSERT missions 后同事务 | E4 监听 → 启动 Grill |
| `mission` | `mission.grill_completed` | missions.status=GRILLED 后 | E10 监听 → 写 notifications NEEDS_HUMAN |
| `mission` | `mission.started` | missions.status=PLANNING 同事务 | E4 监听 → 调 Planner.Plan |
| `mission` | `mission.replan_requested` | Planner 决定重拆 WU | Delivery 监听 → 触发 Planner.Replan |
| `mission` | `mission.completed` | 所有 WU COMPLETED 且无 BLOCKED | E10 监听 → 通知 PO + 写消息流 |
| `mission` | `mission.cancelled` | 任意状态可 | E4 监听 → 取消 in-flight WU + Execution |
| `mission` | `mission.failed` | Planner 异常 / LLM 失败 | E10 监听 → 通知 PO |
| `brief` | `brief.revision_created` | INSERT brief_revisions 后 | E5 监听 → memory_items 同步投影 |

事件 payload schema 见 [00-integration-map.md §5](./00-integration-map.md)。

## 10. 关键边界（与 #1 9 Invariants 联动）

| Invariant | 本文落地 |
| --- | --- |
| **I-1** Mission ≠ WorkItem | missions 表无 work_item_ids[] 字段；通过 mission_work_item_refs 引用 |
| **I-9** Memory approval 不走 Guard | GRILL_QUESTION 直接 APPROVED（kind 标记）；不写 write_memory=REQUIRE_APPROVAL |
| **#1 §3.1 Mission 不允许做的事** | §1.2 stats 派生属性；§2 状态机禁修改 WorkItem.status；§3 Grill 阶段不改 WorkItem.status |

## 11. 反模式（Mission 域）

| 反模式 | 后果 | 正确做法 |
| --- | --- | --- |
| **Mission 直接改 WorkItem.status** | 跳过 E8 业务规则；Provider 同步冲突 | Mission → OUTBOX → E8 收 → updateWorkItem |
| **Brief 复制 memory_items.content** | 重复事实源；approval 路径破坏 | Brief 用 `sources: [{ref_id}]` 引用 |
| **Grill 结果可改** | PO 改 Goal 破坏 audit 链 | Grill 写完不可改；想改重 run Grill（新 Mission） |
| **needs_human_questions 走 write_memory 审批** | 阻塞产品流程 | GRILL_QUESTION 直接 APPROVED |
| **Mission 取消时回滚已 COMPLETED WU** | 破坏 audit；Agent 工作丢失 | 取消只影响 in-flight WU |
| **Planner 调 LLM 处理单个 Task 内容** | 越权（planner 不生成内容） | Planner 只做 WU 拆解（结构化操作） |
| **Grill 不带 grill_seed（不可复现）** | 调试噩梦；同输入不同结果 | 必带 `grill_seed` 入 LLM |
| **Mission.start() 不校验 unanswered questions** | Planner 拿到不完整 brief → 错误 WU 拆解 | 强校验（§7.1 step 2） |
| **stats 字段作为事实源** | 业务逻辑读到过期数据 | stats 派生；读 work_units 表为准 |

## 12. e2e 验收点

```
e2e/autonomous-delivery/01-mission-grill/
  test_001_mission_create_does_not_touch_workitems.json
    Given PO POST /missions { work_item_refs: [W1, W2, W3] }
    Then missions 落 1 行，mission_work_item_refs 落 3 行
    And   work_items 表无 UPDATE
    And   OUTBOX mission.created 投递

  test_002_grill_produces_briefs_with_provenance.json
    Given Mission Grill 启动
    Then 调 LLM 1 次（grill_seed 一致）
    And   briefs ×3 落表（每 Story 1 份）
    And   brief_revisions v1 落 3 行
    And   每份 brief sources 引用 memory_items.id（不复制 content）

  test_003_grill_aggregates_needs_human_to_one_card.json
    Given 3 briefs，grill 产生 3 needs_human_questions
    Then PO Needs You 页看到 1 张聚合卡（非 3 张）
    And   每张卡可单独回答

  test_004_start_blocked_by_unanswered_questions.json
    Given 1 question 未回答
    When  PO POST /missions/:id/start
    Then  422 UnansweredQuestionsError
    And   mission.status 仍 GRILLED

  test_005_brief_stale_on_workitem_change.json
    Given Brief v1 status=GRILLED
    When  WorkItem 标题变化 > 30%
    Then  Brief.status=STALE
    And   UI 提示"重新 Grill？"

  test_006_brief_revision_preserves_history.json
    Given Brief v1 存在
    When  Mission Grill 重新 run（PO 改 goal）
    Then  brief_revisions v2 落 1 行，v1 仍存
    And   briefs.current_revision_id 指向 v2

  test_007_mission_cancel_does_not_rollback_completed_wu.json
    Given Mission 5 WU，3 COMPLETED，1 IN_PROGRESS，1 PLANNED
    When  PO POST /missions/:id/cancel
    Then  missions.status=CANCELLED
    And   3 COMPLETED WU 仍 COMPLETED（不重置）
    And   IN_PROGRESS WU 标 CANCELLED
    And   PLANNED WU 标 CANCELLED
    And   WorkItem.status **不**被 Mission 改

  test_008_mission_replan_preserves_invariant.json
    Given Mission RUNNING with 5 WU
    When  PO 触发 replan（跨 story 矛盾）
    Then  missions.status=REPLANNING → RUNNING
    And   COMPLETED WU **不**重拆
    And   IN_PROGRESS WU 标 BLOCKED（等新决策）

  test_009_mission_stats_derived.json
    Given Mission with 10 WU
    When  1 WU completed
    Then  missions.stats.work_units_done = 1（自动更新）
    And   业务读 WorkUnit 仍以 work_units 表为准

  test_010_grill_failure_notifies_po.json
    Given LLM 5xx 连续 3 次
    Then  Mission.status=FAILED
    And   OUTBOX mission.failed 投递
    And   PO 收 notifications × 1（NEEDS_HUMAN, urgency=CRITICAL）
```

## 13. 实施 M10 子任务

| 子任务 | 内容 | 依赖 |
| --- | --- | --- |
| M10.1 | DB migration: missions / mission_work_item_refs / briefs / brief_revisions | — |
| M10.2 | Mission Service (CRUD + 状态机 + 取消) | M10.1 |
| M10.3 | Grill Service (拉 context + LLM 调用 + 写 briefs) + grill.prompts | M10.2 |
| M10.4 | Brief Service (CRUD + 版本化 + diff) | M10.1 |
| M10.5 | Mission Planner.Plan (拆 WU + 建依赖) | M10.2, [02 §3](./02-workunit-and-delivery-graph.md) |
| M10.6 | OUTBOX event handlers (mission.* / brief.*) | M10.2, E4 outbox worker |
| M10.7 | REST API: /api/v1/missions + /api/v1/briefs | M10.2, M10.4 |
| M10.8 | Mission UI (Needs You 集成 + Mission 首页 4 问题) | M10.7, UI DS v0.7+ |
| M10.9 | e2e: 10 个验收点 | M10.7 |

**Vertical slice 必备**：M10.1 + M10.2 + M10.3 + M10.7 + M10.9 第 1-3 项应在 M10 早期就端到端跑通（PO start → Grill → Brief → 看到 Needs You 页面），避免 v0.4 重蹈 "vertical slice 排末尾" 覆辙。
