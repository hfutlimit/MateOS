# Detailed Design · Autonomous Delivery · 02 · WorkUnit & Delivery Graph & Replan

> **配套**：PRD v0.4 / SYSTEM_DESIGN v0.3.2 / [00-integration-map.md](./00-integration-map.md) / [01-mission-and-grill.md](./01-mission-and-grill.md) / v0.1 Architecture Proposal §10-12
> **范围**：WorkUnit 域（Agent 团队内部 Delivery Planning 单位）+ Dynamic Delivery Graph（DAG）+ Dynamic Replanning（split / merge / add / remove dependency）+ Promote WorkUnit → WorkItem
> **前置**：[00-integration-map.md](./00-integration-map.md) **（必读，9 Invariants）** + [01-mission-and-grill.md](./01-mission-and-grill.md) **（必读，Mission 状态机）**
> **本文不覆盖**：Mission 创建 / Grill（见 [01](./01-mission-and-grill.md)）/ Scheduler（见 [03](./03-scheduler-autonomy-policy.md)）/ Topic（见 [04](./04-topic-needsyou-and-assignment.md)）

## 0. 文档结构

- **§1** WorkUnit 概念与数据模型（work_units / work_unit_dependencies）
- **§2** WorkUnit 状态机
- **§3** Dynamic Delivery Graph（DAG 拓扑 + 边类型）
- **§4** Dependency 来源与 Confidence
- **§5** WorkUnit 完成与依赖解锁（readiness 重算）
- **§6** Dynamic Replanning（split / merge / add / remove）
- **§7** Replanning 状态机
- **§8** Promote WorkUnit → WorkItem（optional）
- **§9** Domain Events
- **§10** 关键边界（与 #1 I-3 / I-7 联动）
- **§11** 反模式
- **§12** e2e 验收点
- **§13** 实施 M11 子任务

## 1. WorkUnit 概念与数据模型

### 1.1 WorkUnit = Agent 团队内部 Delivery Planning 单位

WorkUnit 不同于 E8 `work_items`：
- **WorkItem** = PO / Provider 视角（Story/Task/Bug/Epic），跨 Provider 同步
- **WorkUnit** = Agent 团队内部 Delivery 单位，**optional** promote 为 WorkItem

```sql
-- work_units（核心事实源）
CREATE TABLE work_units (
  id                      UUID PRIMARY KEY,
  mission_id              UUID NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
  brief_id                UUID REFERENCES briefs(id),                   -- 可选，Planner 可不带 brief 直接建 WU（replan）
  work_item_ref           JSONB,                                        -- optional {provider_key, work_item_id, external_ref} 关联的 WorkItem
  parent_work_unit_id     UUID REFERENCES work_units(id),               -- 拆出来的子 WU 引父
  promoted_work_item_id   UUID REFERENCES work_items(id),               -- promote 出去的 WorkItem
  title                   TEXT NOT NULL,
  description             TEXT,
  required_capabilities   JSONB NOT NULL DEFAULT '[]',
  estimated_duration_min  INT,                                          -- Planner 估算（best-effort；v0.1 用 LLM 给）
  priority                INT NOT NULL DEFAULT 3 CHECK (priority BETWEEN 1 AND 5),
  status                  TEXT NOT NULL DEFAULT 'PLANNED'
                          CHECK (status IN ('PLANNED','READY','IN_PROGRESS','BLOCKED','COMPLETED','MERGED','CANCELLED')),
  blocked_reason          TEXT,
  blocked_by_human_question_id UUID,                                    -- 见 §3.3 Needs You 关联
  critical_path           BOOLEAN NOT NULL DEFAULT false,                -- Planner 标 critical path（v0.1 简化：v0.2 实时算）
  metadata                JSONB NOT NULL DEFAULT '{}',
  created_at              TIMESTAMPTZ DEFAULT now(),
  updated_at              TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_wu_mission_status ON work_units(mission_id, status, priority);
CREATE INDEX idx_wu_brief ON work_units(brief_id) WHERE brief_id IS NOT NULL;
CREATE INDEX idx_wu_parent ON work_units(parent_work_unit_id) WHERE parent_work_unit_id IS NOT NULL;

-- work_unit_dependencies（DAG 边）
CREATE TABLE work_unit_dependencies (
  id                  UUID PRIMARY KEY,
  upstream_work_unit_id   UUID NOT NULL REFERENCES work_units(id) ON DELETE CASCADE,
  downstream_work_unit_id UUID NOT NULL REFERENCES work_units(id) ON DELETE CASCADE,
  dep_type            TEXT NOT NULL DEFAULT 'BLOCKS'
                      CHECK (dep_type IN ('BLOCKS','RELATES_TO')),
  source              TEXT NOT NULL
                      CHECK (source IN ('EXPLICIT','AGENT_INFERRED','HUMAN_CONFIRMED')),
  confidence          TEXT CHECK (confidence IN ('LOW','MEDIUM','HIGH')),
  reason              TEXT,
  created_by_type     TEXT NOT NULL CHECK (created_by_type IN ('USER','AGENT','SYSTEM')),
  created_by_id       UUID,
  created_at          TIMESTAMPTZ DEFAULT now(),
  -- 同一对 (upstream, downstream) 只能有一条 BLOCKS 边（去重）
  UNIQUE (upstream_work_unit_id, downstream_work_unit_id, dep_type)
);
CREATE INDEX idx_wud_upstream ON work_unit_dependencies(upstream_work_unit_id);
CREATE INDEX idx_wud_downstream ON work_unit_dependencies(downstream_work_unit_id);
```

### 1.2 关键字段

| 字段 | 用途 |
| --- | --- |
| `parent_work_unit_id` | WU 拆解溯源：split 出来的子 WU 引用父 WU |
| `promoted_work_item_id` | optional 桥：WU 完成时如果决定 promote，写入关联 WorkItem |
| `required_capabilities` | E4 Resolver / OPEN_CLAIM 资格评估的输入 |
| `estimated_duration_min` | Scheduler 评分（v0.1 仅 informational） |
| `priority` | 1=最高，5=最低；Scheduler 用 |
| `critical_path` | Planner 离线算（v0.1 简化：Mission 启动时算一次） |
| `blocked_by_human_question_id` | WU BLOCKED 时引关联的 GRILL_QUESTION / 新 question |

## 2. WorkUnit 状态机

```
              ┌──────────┐
              │ PLANNED  │ (Planner 拆解后默认)
              └─────┬────┘
                    │ readiness 算出（依赖全部 COMPLETED）
                    ▼
              ┌──────────┐
              │  READY   │ (OUTBOX work_unit.ready)
              └─────┬────┘
                    │ Agent claim / E4 Resolver 派 CR + Execution 启动
                    ▼
              ┌──────────┐
              │IN_PROGRESS│
              └─────┬────┘
        ┌───────────┼───────────────┬─────────────┐
        │           │               │             │
        ▼           ▼               ▼             ▼
   ┌─────────┐ ┌────────┐   ┌──────────┐  ┌──────────┐
   │COMPLETED│ │BLOCKED │   │  MERGED  │  │ CANCELLED│
   │         │ │        │   │ (被合到  │  │ (Mission │
   │         │ │reason  │   │  其他 WU)│  │  取消)   │
   │         │ │必填    │   │          │  │          │
   └─────────┘ └────┬───┘   └──────────┘  └──────────┘
                    │ 人类决策
                    ▼
                 回 PLANNED / READY / CANCELLED
```

**关键约束**（与 #1 I-3 一致）：
- **WorkUnit 状态枚举不含 EXECUTING / SUCCEEDED / FAILED**（与 Execution 状态机独立）
- `BLOCKED` 必须填 `blocked_reason` + `blocked_by_human_question_id`
- `MERGED` 不可回 PLANNED / READY（终态）
- `IN_PROGRESS → COMPLETED` 必填 `execution_id`（`metadata.execution_id`）

### 2.1 状态机迁移矩阵

| From | To | 触发 | 必填字段 |
| --- | --- | --- | --- |
| PLANNED | READY | Readiness 服务算依赖满足 | 无 |
| PLANNED | CANCELLED | Mission CANCELLED / Planner 拆 WU 后回滚 | 无 |
| READY | IN_PROGRESS | Agent claim work_offer + Execution 启动 | `metadata.execution_id` |
| READY | CANCELLED | Mission CANCELLED / Planner 主动撤销 | 无 |
| IN_PROGRESS | COMPLETED | 关联 Execution SUCCEEDED | `metadata.execution_id` |
| IN_PROGRESS | BLOCKED | 关联 Execution FAILED / Agent 推 handoff.requested=BLOCKED | `blocked_reason` + `blocked_by_human_question_id?` |
| IN_PROGRESS | CANCELLED | Mission CANCELLED / Execution CANCELLED | 无 |
| BLOCKED | PLANNED | 人类决策：需修改 WU | 无 |
| BLOCKED | READY | 人类决策：无需修改 | 无 |
| BLOCKED | CANCELLED | 人类决策：放弃 | 无 |
| PLANNED / READY | MERGED | Planner merge 到其他 WU | `parent_work_unit_id` 不变（被 merge 的 WU 保留） |

**CAS 保护**：所有状态迁移走 `UPDATE work_units SET status=? WHERE id=? AND status IN (...合法前置状态)`；affected=0 → StaleStateError。

## 3. Dynamic Delivery Graph

### 3.1 DAG 拓扑

DAG = `{work_units 节点} ∪ {work_unit_dependencies 边}`。

**DAG 不变量**：
1. **无环**：每次 add_dependency 前做 cycle detection（BFS 从 new_upstream 出发能不能到 new_downstream；能到 = cycle，拒绝）
2. **同 Mission 闭包**：所有 WU + 边同属一个 mission
3. **跨 WU 引用禁止**：downstream / upstream 必须同 mission

### 3.2 边类型

| dep_type | 语义 | UI 投影 | 阻塞性 |
| --- | --- | --- | --- |
| `BLOCKS` | upstream 必须 COMPLETED，downstream 才能 READY | 实线箭头 | 阻塞 |
| `RELATES_TO` | 仅做信息关联（"看 upstream 的实现"），不阻塞 | 虚线 | 不阻塞 |

v0.1 简化：**`RELATES_TO` 不影响 readiness**。v0.2 可升级为软阻塞（"upstream 80% 完成时下游可启动"）。

### 3.3 Readiness 算法

```python
def recalculate_readiness(mission_id: UUID) -> List[WorkUnit]:
    """每次 WU 状态变更后调用，重算所有 WU 的 readiness。
    返回从 PLANNED 跳到 READY 的 WU 列表（供 OUTBOX 触发）。"""
    
    with db.transaction() as tx:
        # 1. 读 mission 下所有 WU
        work_units = tx.query("""
            SELECT id, status FROM work_units
            WHERE mission_id = $1
        """, mission_id)
        
        # 2. 读所有 BLOCKS 依赖边
        edges = tx.query("""
            SELECT upstream_work_unit_id, downstream_work_unit_id
            FROM work_unit_dependencies
            WHERE dep_type = 'BLOCKS'
              AND upstream_work_unit_id IN (
                  SELECT id FROM work_units WHERE mission_id = $1
              )
        """, mission_id)
        
        # 3. 构图（in-memory adjacency list）
        blocked_by = defaultdict(set)  # downstream_id -> {upstream_ids}
        for e in edges:
            blocked_by[e.downstream].add(e.upstream)
        
        # 4. 重算 PLANNED → READY
        newly_ready = []
        for wu in work_units:
            if wu.status != 'PLANNED':
                continue
            upstream_ids = blocked_by.get(wu.id, set())
            all_done = all(
                work_units_by_id[uid].status == 'COMPLETED'
                for uid in upstream_ids
            )
            if all_done:
                affected = tx.execute("""
                    UPDATE work_units SET status = 'READY', updated_at = NOW()
                    WHERE id = $1 AND status = 'PLANNED'
                    RETURNING id
                """, wu.id)
                if affected:
                    newly_ready.append(wu.id)
        
        return newly_ready
```

**事务性**：整个 recalculate 在一个事务内（避免"刚算完就变"导致脏数据）。事务提交后 OUTBOX 事件发出。

**性能**：v0.1 单 mission ≤ 100 WU 时 in-memory 完全够；v0.2 超过 1000 WU 改用 topological sort + incremental。

## 4. Dependency 来源与 Confidence

### 4.1 三种 source

| source | 含义 | 谁来 | 可信度 |
| --- | --- | --- | --- |
| `EXPLICIT` | PO 在 Brief / Comment 显式声明 | PO | 最高（不变） |
| `AGENT_INFERRED` | Planner LLM 自动推断 | Planner | 中（confidence 必填） |
| `HUMAN_CONFIRMED` | Agent 推断后被 PO 显式确认 | PO | 最高（升级） |

### 4.2 Confidence 升级流程

```
Planner 拆 WU 时建 AGENT_INFERRED 边 + confidence=LOW/MEDIUM/HIGH
   ↓
Mission UI 显示虚线（inferred 边）+ tooltip "LLM 推断，置信度 MEDIUM"
   ↓
PO 点击「确认」→ UI 调 PATCH /work-units/:id/dependency/:dep_id
{ source: 'HUMAN_CONFIRMED', reason: 'PO confirmed' }
   ↓
边变实线（confirmed）
```

### 4.3 Dependency 删除 / 修改

| 操作 | API | 约束 |
| --- | --- | --- |
| 增依赖 | POST /work-units/:id/dependency | cycle detection（§3.1） |
| 删依赖 | DELETE /work-units/:id/dependency/:dep_id | 仅 EXPLICIT / HUMAN_CONFIRMED 可删；AGENT_INFERRED 需 PO 显式（audit） |
| 改 source | PATCH /work-units/:id/dependency/:dep_id | 不可从 HUMAN_CONFIRMED 改回 AGENT_INFERRED（单向） |
| 改 reason | PATCH | 不限 |

## 5. WorkUnit 完成与依赖解锁

### 5.1 完整时序

```
T+0   Agent A 执行 WU-1 → Execution SUCCEEDED
T+1   E7: OUTBOX execution.completed
T+2   E4 Outbox Worker → 释放 execution lease + 写 messages AGENT_OUTPUT 投影
T+3   E4 Outbox Worker → 调 Delivery Domain: handleWorkUnitCompleted(WU-1, execution_id)
T+4   Delivery: CAS WU-1.status=IN_PROGRESS → COMPLETED, 写 metadata.execution_id
T+5   Delivery: OUTBOX work_unit.completed (payload: {wu_id, execution_id, artifacts[]})
T+6   E4 Outbox Worker → work_unit.completed
      ├─ 释放 work_offer（如果有 OPEN_CLAIM）
      ├─ 标 WU 关联的 work_item status 变更（如果 promote_to_work_item=true）
      └─ 调 Delivery.readinessService.recalculate(mission_id)

T+7   Readiness 重算：
      ├─ WU-2 依赖 WU-1（BLOCKS）→ 满足 → CAS WU-2 PLANNED → READY
      └─ WU-3 依赖 WU-2（BLOCKS）→ 不满足 → 仍 PLANNED

T+8   Delivery: OUTBOX work_unit.ready ×K（新 READY 的 WU）
T+9   E4 Outbox Worker → 看 mission.policy.assignment_mode
      ├─ DIRECT → E4 选指定 Agent
      ├─ ROUTED → E4 Resolver 走标准评分
      └─ OPEN_CLAIM → 写 work_offers → WS 广播
```

### 5.2 关键不变量

- **WorkUnit COMPLETED ≠ WorkItem COMPLETED**（除非 `promote_to_work_item=true` 且已 promote）
- **WorkUnit COMPLETED 不撤销下游 PLANNED 的 WU**（只能解锁 BLOCKS 边）
- **解锁 + 新 READY 在同事务**（避免 race：unlock 后但 readiness 还没算，下游 Agent 看到 WU 仍 PLANNED 拒绝 claim）

## 6. Dynamic Replanning

### 6.1 触发场景

| 场景 | 触发方 | 例子 |
| --- | --- | --- |
| Cross-story 矛盾 | Planner 拆 WU 后发现 Story A 的 WU 改了 Story B 的接口 | Planner 推 `mission.replan_requested` |
| OAuth 复杂度超预期 | Agent 在 WU-2 上发现 OAuth 复杂度过高需拆 | Agent 推 handoff.requested={purpose:'BLOCKED', reason:'split_needed', wu_id} → E4 → Planner.Replan |
| PO 改 Goal | PO 在 Mission UI 改 goal | PO POST /missions/:id/replan |
| Mission REPLANNING 状态 | Planner 主动重算（如 dependency 删后产生孤立 WU） | 内部 |

### 6.2 split / merge / add / remove API

```ts
// split
POST /work-units/:id/split
body: { children: [{ title, required_capabilities[], estimated_duration_min? }] }
→ 拆成 N 个子 WU
→ 原 WU 标 MERGED（保留为 audit）
→ 子 WU.parent_work_unit_id = 原 WU.id
→ 子 WU.status = PLANNED（继承原 WU 依赖 + capability）
→ OUTBOX work_unit.split

// merge
POST /work-units/:id/merge
body: { source_work_unit_ids: [w1, w2, w3], target: { title, ... } }
→ 把多个 WU 合成 1 个
→ source WU 标 MERGED
→ target WU 创建
→ target WU.required_capabilities = ∪ source
→ target 继承 source 的入边（upstream）
→ target 的出边（downstream）由 Planner 决定如何处理（默认指向所有 source 的下游）
→ OUTBOX work_unit.merged

// add dependency
POST /work-units/:id/dependency
body: { upstream_work_unit_id, dep_type?, source?, confidence?, reason? }
→ cycle detection 通过才落库
→ OUTBOX（无独立 event；触发 readiness 重算 + OUTBOX work_unit.ready）

// remove dependency
DELETE /work-units/:id/dependency/:dep_id
→ AGENT_INFERRED 需 PO 确认（audit）
→ 触发 readiness 重算
```

### 6.3 Planner 内部实现

```python
async def plan_mission(mission_id: UUID, replan_reason: str):
    mission = await get_mission(mission_id)
    briefs = await get_briefs(mission_id)
    existing_wus = await get_work_units(mission_id)
    
    # 1. 决策：增量 vs 全量
    if replan_reason in ('cross_story_contradiction', 'po_goal_changed'):
        # 全量：仅保留 COMPLETED WU，其他重拆
        await preserve_completed_wus(existing_wus)
        await mark_others_for_replan(existing_wus)  # PLANNED/READY → STALE
        new_wus = await decompose_from_scratch(briefs)
    else:
        # 增量：仅在受 replan_reason 影响的 WU 子集操作
        affected = find_affected_wus(existing_wus, replan_reason)
        new_wus = await adjust_wus(affected, briefs)
    
    # 2. 写新 WU + 依赖
    for wu in new_wus:
        await insert_work_unit(wu)
    
    # 3. 重算 readiness
    newly_ready = await readiness_service.recalculate(mission_id)
    
    # 4. 触发 OUTBOX
    for wu_id in newly_ready:
        await emit('work_unit.ready', wu_id)
    
    # 5. CAS mission.status
    await db.execute("""
        UPDATE missions SET status='RUNNING', updated_at=NOW()
        WHERE id=$1 AND status IN ('PLANNING','REPLANNING')
    """, mission_id)
```

**关键**：`REPLANNING` 状态期间**不**重置已 COMPLETED WU；只重算 in-flight 子集。

## 7. Replanning 状态机

```
RUNNING ──── replan_requested ────► REPLANNING
                                          │
                                          ├──► Planner 成功 ──► RUNNING（解锁新 WU）
                                          │
                                          ├──► Planner 失败（LLM 5xx 等）──► REPLANNING_FAILED ──► PO 介入 ──► RUNNING
                                          │
                                          └──► PO 取消 ──► CANCELLED
```

**关键约束**：
- REPLANNING 期间**不**接受新 work_offer.claim（已发布的 offer 仍可 claim，避免 in-flight WU 卡死）
- REPLANNING 状态**不**触发 Mission Cancel（cancel 走单独路径）

## 8. Promote WorkUnit → WorkItem

### 8.1 何时 promote

v0.1 简化：**仅当 Mission policy `promote_to_work_item=true` 时 promote**。决策在 Mission start 时确定，全局生效。

### 8.2 promote 时机

| 时机 | 含义 | 例子 |
| --- | --- | --- |
| WU PLANNED 时 | 提前创建 WorkItem 跟踪 | 复杂 WU 早期可见 |
| WU COMPLETED 时 | 完成后一次性 promote | 默认 |

v0.1 选**COMPLETED 时 promote**（避免提前污染 WorkItem 流）。

### 8.3 promote 实现

```python
async def promote_work_unit(wu: WorkUnit, mission: Mission):
    if wu.promoted_work_item_id:
        return  # 已 promote
    if not mission.policy_snapshot.promote_to_work_item:
        return
    
    # 调 E8 createWorkItem
    work_item = await work_management.create_work_item(
        project_id=mission.project_id,
        type='TASK',
        title=wu.title,
        description=wu.description,
        required_capabilities=wu.required_capabilities,
        # 不传 work_item_ref（Built-in 新建）
    )
    
    # 写回 WU
    await db.execute("""
        UPDATE work_units SET promoted_work_item_id = $1, updated_at = NOW()
        WHERE id = $2
    """, work_item.id, wu.id)
```

**关键边界**：
- Promote **不**改 WorkItem.status（WorkItem 创建后状态独立流转）
- Promote **不**改 WU.status（WU 仍走 COMPLETED）
- 失败回滚：promote 失败时 WU 仍 COMPLETED（不阻塞 Mission 进度）；写 outbox `work_unit.promote_failed` 由人工兜底

## 9. Domain Events

| aggregate | event_type | payload 关键字段 | 何时 | 投递目标 |
| --- | --- | --- | --- | --- |
| `work_unit` | `work_unit.ready` | wu_id, mission_id, required_capabilities | Readiness 重算后 | E4 监听 → 发布 work_offer（OPEN_CLAIM 模式）|
| `work_unit` | `work_unit.claimed` | wu_id, work_offer_id, agent_id | E4 创建 CR 后 | Delivery 监听 → 更新 WU 关联 |
| `work_unit` | `work_unit.completed` | wu_id, execution_id, artifacts[] | E4 收到 execution.completed | E4 监听 → 释放 work_offer + 解锁依赖 |
| `work_unit` | `work_unit.blocked` | wu_id, reason, needs_human_question_id? | Agent 推 handoff.requested={BLOCKED} | E10 监听 → 写 notifications NEEDS_HUMAN |
| `work_unit` | `work_unit.split` | parent_wu_id, child_wu_ids[] | Planner split | Delivery 监听 → 更新 DAG |
| `work_unit` | `work_unit.merged` | source_wu_ids[], target_wu_id | Planner merge | Delivery 监听 → 更新 DAG |
| `work_unit` | `work_unit.promote_failed` | wu_id, error | promote 异常 | E10 监听 → 通知 PO |

事件 payload schema 见 [00-integration-map.md §5](./00-integration-map.md)。

## 10. 关键边界（与 #1 9 Invariants 联动）

| Invariant | 本文落地 |
| --- | --- |
| **I-3** WorkUnit ≠ WorkItem | §1 work_units 不强制 work_item_ref；promote optional；§8 promote 走 E8 API |
| **I-3** WorkUnit != Execution | §1 work_units.status 枚举不含 Execution 状态；§2 状态机独立；§5.1 WU COMPLETED 由 Execution SUCCEEDED 触发 |
| **I-7** Open Claim 仍走 E4 CollaborationRequest | §5.1 work_unit.completed → E4 Outbox Worker 走标准路径；§1.1 work_offer 在 E4 域（不属 Delivery） |
| **#1 §3.1 WorkUnit 不允许做的事** | §1.1 status 枚举；§2 状态机约束；§3 无环；§8 promote 走 E8 API |

## 11. 反模式（WorkUnit 域）

| 反模式 | 后果 | 正确做法 |
| --- | --- | --- |
| **WorkUnit 1:1 替换 WorkItem** | PO 视角 Story 被覆盖；Provider 同步破坏 | WU 独立；promote optional |
| **WorkUnit 直接创建 Execution** | 绕过 E4 → E7 协议；破坏 decision 事实源 | WU → OUTBOX → E4 创建 CR |
| **WU 状态枚举含 EXECUTING/SUCCEEDED** | 与 Execution 状态机混淆 | WU 状态独立：PLANNED/READY/IN_PROGRESS/COMPLETED/BLOCKED/MERGED/CANCELLED |
| **readiness 在 WU 状态变更事务外重算** | unlock 与 readiness race | 同事务（§3.3） |
| **AGENT_INFERRED 边可随意删** | Planner 反复推断同依赖 | AGENT_INFERRED 需 PO 显式删 |
| **merge 不保留 source WU** | audit 链丢失 | source WU 标 MERGED 保留 |
| **split 拆完改 parent WU.status=DELETED** | 与 v0.1 §12 矛盾（拆完父标 MERGED） | 父标 MERGED（终态） |
| **replan 重置 COMPLETED WU** | Agent 工作丢失 | replan 仅影响 in-flight 子集（§6.3） |
| **promote 时改 WorkItem.status** | 跳过 E8 业务规则 | 仅 createWorkItem；状态独立 |
| **cycle detection 在 edge INSERT 后** | 已污染数据难清理 | 在 POST /work-units/:id/dependency 前 dry-run 验证 |

## 12. e2e 验收点

```
e2e/autonomous-delivery/02-workunit-dag/
  test_001_wu_create_with_dependencies.json
    Given Mission M with 3 WU: W1, W2, W3; W3 depends on W1
    Then work_units ×3 落表
    And   work_unit_dependencies W1→W3 (BLOCKS) 落 1 行
    And   W1 状态 PLANNED，W2 状态 PLANNED，W3 状态 PLANNED
    And   readiness 算：W1/W2 READY（无依赖），W3 PLANNED

  test_002_completion_unblocks_downstream.json
    Given W1 PLANNED, W3 BLOCKED on W1
    When  W1 → COMPLETED
    Then  readiness 重算
    And   W3 PLANNED → READY
    And   OUTBOX work_unit.ready ×1 (W3)

  test_003_cycle_detection_rejects.json
    Given W1, W2, W3 with W3→W1
    When  POST /work-units/W1/dependency { upstream: W3 }
    Then  422 CycleDetectedError
    And   无新边

  test_004_split_preserves_parent.json
    Given W1 IN_PROGRESS
    When  POST /work-units/W1/split { children: [C1, C2] }
    Then  W1.status=MERGED
    And   C1.parent_work_unit_id=W1
    And   C2.parent_work_unit_id=W1
    And   C1, C2 status=PLANNED
    And   OUTBOX work_unit.split

  test_005_merge_unions_capabilities.json
    Given W1, W2, W3 with capabilities ['coding','db']
    When  POST /work-units/T/merge { source_work_unit_ids: [W1, W2, W3] }
    Then  T.required_capabilities ⊇ ['coding','db']
    And   W1, W2, W3 status=MERGED
    And   T 继承 W1/W2/W3 的入边
    And   OUTBOX work_unit.merged

  test_006_replan_preserves_completed.json
    Given Mission with 5 WU: 3 COMPLETED, 1 IN_PROGRESS, 1 PLANNED
    When  POST /missions/:id/replan
    Then  missions.status=REPLANNING → RUNNING
    And   3 COMPLETED WU 仍 COMPLETED
    And   IN_PROGRESS WU 标 BLOCKED
    And   新 WU 创建（拆/合）

  test_007_promote_creates_workitem_via_e8.json
    Given Mission with promote_to_work_item=true
    When  W1 COMPLETED
    Then  E8 createWorkItem 被调 1 次
    And   work_items 落 1 行
    And   W1.promoted_work_item_id 落表
    And   W1.status 仍 COMPLETED

  test_008_no_promote_skips_e8.json
    Given Mission with promote_to_work_item=false
    When  W1 COMPLETED
    Then  E8 createWorkItem **不**被调
    And   W1.promoted_work_item_id 仍 NULL

  test_009_blocked_writes_human_question.json
    Given W1 IN_PROGRESS, Agent 推 handoff BLOCKED with reason='need_clarification'
    When  Delivery 处理
    Then  W1.status=IN_PROGRESS → BLOCKED
    And   W1.blocked_reason='need_clarification'
    And   memory_items 新增 1 行 (kind=MISSION_BLOCKED_QUESTION)
    And   W1.blocked_by_human_question_id 落表

  test_010_ag_inferred_requires_po_to_delete.json
    Given edge W1→W2 source=AGENT_INFERRED
    When  Agent A 调 DELETE /work-units/W2/dependency/:dep_id
    Then  403 RequiresPOConfirmationError
    When  PO PATCH 删
    Then  边删成功

  test_011_readiness_atomic_with_unlock.json
    Given W1 PLANNED with 2 upstream (U1, U2 both COMPLETED)
    When  readiness.recalculate(mission_id) 触发
    Then  W1 CAS PLANNED → READY（同事务）
    And   OUTBOX work_unit.ready 在事务提交后投递
    And   不存在「W1 仍 PLANNED 但已发出 OUTBOX」race

  test_012_wu_status_does_not_mirror_execution.json
    Given W1 IN_PROGRESS, Execution RUNNING
    When  Execution FAILED
    Then  W1.status=IN_PROGRESS → BLOCKED（不是 FAILED）
    And   W1.blocked_reason='execution_failed:...'
    And   WU 状态枚举不出现 FAILED
```

## 13. 实施 M11 子任务

| 子任务 | 内容 | 依赖 |
| --- | --- | --- |
| M11.1 | DB migration: work_units / work_unit_dependencies | — |
| M11.2 | WorkUnit Service (CRUD + 状态机 CAS) | M11.1 |
| M11.3 | Dependency Service (增/删/改 + cycle detection) | M11.2 |
| M11.4 | Readiness Service (重算 + OUTBOX 触发) | M11.2, M11.3 |
| M11.5 | Planner Service (拆 WU + 调 LLM) | M11.2, [01 M10.5](./01-mission-and-grill.md) |
| M11.6 | Split / Merge Service (含 audit 保留) | M11.2 |
| M11.7 | Promote Service (调 E8 createWorkItem) | M11.2, E8 work-management |
| M11.8 | OUTBOX event handlers (work_unit.* + 写 work_offers) | M11.4, E4 outbox worker |
| M11.9 | REST API: /api/v1/work-units + /api/v1/missions/:id/replan | M11.5, M11.6, M11.7 |
| M11.10 | Replan 状态机 + UI (Mission status badge: REPLANNING) | M11.5, UI DS v0.7+ |
| M11.11 | e2e: 12 个验收点 | M11.9 |

**Vertical slice 必备**：M11.1 + M11.2 + M11.4 + M11.8 + M11.9 第 1-3 项应在 M11 早期就端到端跑通（拆 WU → 建依赖 → readiness 重算 → 看到 OUTBOX work_unit.ready）。
