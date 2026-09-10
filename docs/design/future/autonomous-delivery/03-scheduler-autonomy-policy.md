# Detailed Design · Autonomous Delivery · 03 · Scheduler & Autonomy & Project Policy

> **配套**：PRD v0.4 / SYSTEM_DESIGN v0.3.2 / [00-integration-map.md](./00-integration-map.md) / [02-workunit-and-delivery-graph.md](./02-workunit-and-delivery-graph.md) / v0.1 Architecture Proposal §12-13, §23-24
> **范围**：Scheduler（WorkUnit → Agent 调度）+ Autonomy Level 0-3 + Project Policy（formal 实体）
> **前置**：[00-integration-map.md](./00-integration-map.md) **（必读，9 Invariants）** + [02-workunit-and-delivery-graph.md](./02-workunit-and-delivery-graph.md) **（必读，WU 状态机 + OUTBOX work_unit.ready）**
> **本文不覆盖**：Mission 创建 / Grill（见 [01](./01-mission-and-grill.md)）/ WorkUnit 拆解（见 [02](./02-workunit-and-delivery-graph.md)）/ Topic（见 [04](./04-topic-needsyou-and-assignment.md)）

## 0. 文档结构

- **§1** Scheduler 概念与责任
- **§2** Scheduler 输入输出
- **§3** 评分公式（FASTEST_COMPLETION / BALANCED / LOWEST_COST）
- **§4** WorkOffer 数据模型
- **§5** Scheduler 状态机
- **§6** Autonomy Level 0-3 详细行为
- **§7** Project Policy 数据模型
- **§8** Policy 评估路径（policy.evaluate）
- **§9** Autonomy × Policy 联动
- **§10** Domain Events
- **§11** 关键边界（与 #1 I-7 / 与 E4 Resolver 关系）
- **§12** 反模式
- **§13** e2e 验收点
- **§14** 实施 M12 子任务

## 1. Scheduler 概念与责任

### 1.1 Scheduler 解决什么

Scheduler 把 **Mission 内的 WU → 合适的 Agent**。它**不**是 E4 Resolver（单 Collab 路由），而是**多 WU 批量调度**：

- 一次 Mission 内可能有 50 个 READY WU
- 5 个 Agent 在线
- Scheduler 决定"这 50 个 WU 怎么分给 5 个 Agent"（考虑 critical path、capacity、capability、agent 上下文熟悉度）

### 1.2 Scheduler 责任清单

| 责任 | 不责任 |
| --- | --- |
| 监听 `work_unit.ready` 事件，触发调度 | 选单个 Collab 的 Agent（归 E4 Resolver） |
| 按 Mission objective 评分 WU 优先级 | 拆 WU（归 Planner） |
| 发布 `work_offers`（OPEN_CLAIM 模式） | 拆 / 合 Execution（归 E7） |
| 计算 critical path（Mission 启动时 + Replan 时） | 取消 Execution（归 E4） |
| 评估 Autonomy Level 决定 escalate 还是 auto | 写 Memory（归 Agent / E5） |
| 应用 Project Policy（review / handoff / escalation） | 改 WorkItem.status（归 E8） |

### 1.3 Scheduler 类型（v0.1 简化）

v0.1 **不**做复杂优化求解器。简单 Score 函数 + FIFO + critical path 加权：

```python
class Scheduler:
    def schedule(self, mission_id: UUID) -> List[WorkOffer]:
        """Mission 启动时调用 + 每次 work_unit.ready 触发增量调度"""
        ready_wus = self.get_ready_work_units(mission_id)
        agents = self.get_eligible_agents(mission_id)
        return [self._score_and_offer(wu, agents) for wu in ready_wus]
```

V0.2 可升级为 ILP / CP-SAT（V2 课题，**不**在 v0.1 范围）。

## 2. Scheduler 输入输出

### 2.1 输入

| 输入 | 数据源 | 用途 |
| --- | --- | --- |
| READY WorkUnits | `work_units` (status=READY) | 待调度任务 |
| Eligible Agents | E2 `agents` ∩ `agent_project_membership` ∩ `permissions` | 候选 |
| Mission objective | `missions.objective` (FASTEST_COMPLETION / BALANCED / LOWEST_COST) | 评分公式 |
| Mission policy snapshot | `project_policies` snapshot | review / handoff 策略 |
| Agent current load | Redis Lua `agent-capacity:{id}:leases` ZCARD | 评分负载项 |
| Agent capability match | E2 `agents.capabilities` | 评分能力项 |
| Agent accept_rate_30d | `decision_records` 聚合 | 评分质量项 |
| Agent context familiarity | 1-week 决策聚合 | 同 Project / 同 Channel 优先 |
| Critical path | WU 上 `critical_path` 字段 | FASTEST_COMPLETION 优先 |
| Dependency unlock value | BFS 数下游 WU 数 | 解锁越多优先 |

### 2.2 输出

| 输出 | 用途 |
| --- | --- |
| WorkOffer ×K | E4 域（OPEN_CLAIM 模式） |
| `work_unit.assignment_decision` log | 评分明细审计 |

## 3. 评分公式

### 3.1 FASTEST_COMPLETION（默认 PO 场景）

```python
def score_fastest(wu: WorkUnit, agent: Agent, ctx: Ctx) -> float:
    # 1. critical path 加权（critical path WU 优先）
    cp_weight = 1.0 if wu.critical_path else 0.6

    # 2. unlock value（BFS 下游 WU 数）
    unlock_value = ctx.unlocked_downstream_count(wu) / 10.0  # 归一化

    # 3. agent fit
    capability_match = jaccard(wu.required_capabilities, agent.capabilities)  # 0-1
    load = ctx.current_load(agent) / agent.max_concurrency  # 0-1
    load_score = 1 - load
    accept_rate = ctx.accept_rate_30d(agent)  # 0-1
    context_familiarity = ctx.context_familiarity(agent, wu.mission_id)  # 0-1

    # 4. 公式
    score = (
        0.30 * cp_weight +
        0.20 * min(unlock_value, 1.0) +
        0.30 * capability_match +
        0.10 * load_score +
        0.05 * accept_rate +
        0.05 * context_familiarity
    )
    return score
```

### 3.2 BALANCED

```python
# 同 FASTEST_COMPLETION，但 cp_weight 改为 0.15，load_score 改为 0.20
# 强调"不让单 Agent 累死"
score_balanced = (
    0.15 * cp_weight +
    0.15 * min(unlock_value, 1.0) +
    0.30 * capability_match +
    0.20 * load_score +     # 提高权重
    0.10 * accept_rate +
    0.10 * context_familiarity
)
```

### 3.3 LOWEST_COST

```python
# 强调 token 成本与时长
# estimated_duration_min 短的优先；agent cost 低的优先（V1+ 才有 agent cost）
score_lowest = (
    0.10 * cp_weight +
    0.10 * unlock_value +
    0.20 * capability_match +
    0.10 * load_score +
    0.20 * (1 - normalize(wu.estimated_duration_min)) +  # 短任务优先
    0.20 * (1 - ctx.agent_cost_score(agent)) +  # V1+ 实现
    0.10 * context_familiarity
)
```

### 3.4 DIRECT 模式（不走评分）

Project policy 显式指定 Agent 时：
- `work_unit.assignment_target_agent_id` 非空
- 评分 = ∞（必选）
- 跳过 work_offer，直接 E4 创建 CollaborationRequest（target_agent_id=指定）

### 3.5 ROUTED 模式（v0.1 主要路径）

按 score 选 top-1 Agent，**不**发布 work_offer，直接 E4 创建 CollaborationRequest。

### 3.6 OPEN_CLAIM 模式

**不**做 Agent 选择。Scheduler 仅决定：
- 是否发布 work_offer（默认是）
- 发布给哪些 eligible Agents（按 capability 过滤后全发）
- work_offer TTL（默认 1h）

Agent 主动 claim。**不**做评分（V0.2 可加 interest score / estimated completion）。

## 4. WorkOffer 数据模型

```sql
-- work_offers（OPEN_CLAIM 模式下的"开放工作"）
CREATE TABLE work_offers (
  id                UUID PRIMARY KEY,
  mission_id        UUID NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
  work_unit_id      UUID NOT NULL REFERENCES work_units(id) ON DELETE CASCADE,
  required_capabilities JSONB NOT NULL DEFAULT '[]',
  eligible_agent_ids JSONB NOT NULL DEFAULT '[]',    -- Scheduler 算的候选
  status            TEXT NOT NULL DEFAULT 'OPEN'
                    CHECK (status IN ('OPEN','CLAIMED','EXPIRED','CANCELLED')),
  claimed_by_agent_id UUID REFERENCES agents(id),
  claimed_at        TIMESTAMPTZ,
  collaboration_request_id UUID REFERENCES collaboration_requests(id),  -- claim 时回填
  expires_at        TIMESTAMPTZ NOT NULL,            -- 默认 1h
  metadata          JSONB NOT NULL DEFAULT '{}',
  created_at        TIMESTAMPTZ DEFAULT now(),
  updated_at        TIMESTAMPTZ DEFAULT now(),
  -- 同 WU 只能有 1 个 active offer
  UNIQUE (work_unit_id) WHERE status = 'OPEN'
);
CREATE INDEX idx_wo_mission_status ON work_offers(mission_id, status, created_at DESC);
CREATE INDEX idx_wo_eligible ON work_offers USING GIN (eligible_agent_ids);
```

### 4.1 WorkOffer 状态机

```
OPEN ─┬─► CLAIMED     (Agent claim 成功 → 写 collaboration_request_id)
      ├─► EXPIRED     (TTL 到 + 没 claim)
      └─► CANCELLED   (Mission CANCELLED / Planner 拆 WU)
```

### 4.2 Agent Claim 协议

```python
async def claim_work_offer(offer_id: UUID, agent_id: UUID):
    # 1. CAS 抢 offer
    affected = await db.execute("""
        UPDATE work_offers
        SET status = 'CLAIMED', claimed_by_agent_id = $2, claimed_at = NOW()
        WHERE id = $1 AND status = 'OPEN' AND expires_at > NOW()
        RETURNING id
    """, offer_id, agent_id)

    if not affected:
        raise OfferUnavailableError()

    # 2. 校验 Agent 资格
    if not check_agent_eligibility(agent_id, offer):
        # 回滚
        await db.execute("""
            UPDATE work_offers SET status = 'OPEN', claimed_by_agent_id = NULL, claimed_at = NULL
            WHERE id = $1
        """, offer_id)
        raise AgentIneligibleError()

    # 3. E4 创建 CollaborationRequest
    cr = await collaboration_service.create(
        trigger_type='WORK_OFFER',
        target_agent_id=agent_id,
        required_capabilities=offer.required_capabilities,
        context_refs={'work_offer_id': offer.id, 'work_unit_id': offer.work_unit_id},
        assignment_mode='OPEN_CLAIM',
    )

    # 4. 写回 work_offer.collaboration_request_id
    await db.execute("""
        UPDATE work_offers SET collaboration_request_id = $2
        WHERE id = $1
    """, offer_id, cr.id)

    # 5. OUTBOX work_unit.claimed
    await emit('work_unit.claimed', {wu_id: offer.work_unit_id, work_offer_id: offer_id, agent_id})

    return cr
```

## 5. Scheduler 状态机

```
IDLE  ──► SCHEDULING  ──► IDLE
                            │
                            ├─► OFFERED  (WorkOffer ×K 发布)
                            │
                            └─► REPLANNING  (Mission Replan 触发)
                                          │
                                          ▼
                                       IDLE
```

Scheduler **不**持久化状态（无状态服务）。BullMQ 任务承载：
- `scheduler.run` (Mission 启动)
- `scheduler.on_work_unit_ready` (OUTBOX 触发)
- `scheduler.on_agent_offered` (V1+)
- `scheduler.replan` (Mission Replan)

## 6. Autonomy Level 0-3 详细行为

### 6.1 4 级对比

| 维度 | LEVEL 0 MANUAL | LEVEL 1 ASSISTED | LEVEL 2 SUPERVISED（默认） | LEVEL 3 AUTONOMOUS |
| --- | --- | --- | --- | --- |
| Mission Grill | PO 手动 trigger | Agent 推荐，PO 确认 | Agent 自动 + PO 看到 Needs You | Agent 自动，PO 仅收 escalations |
| WU 拆解 | PO 手动 | Agent 推荐，PO 确认 | Agent 自动，PO 可干预 | Agent 自动 |
| WU 调度（DIRECT/ROUTED/OPEN_CLAIM） | PO 手动指定 Agent | Agent 推荐 | OPEN_CLAIM + Resolver | OPEN_CLAIM + Resolver |
| 跨 Story 矛盾处理 | PO 决策 | Agent 推荐 | Agent 自动 + Needs You | Agent 自动 |
| 写 Memory | PO 必审 | PO 必审 | PO 必审 | PO 必审（E5 门禁不变）|
| 部署 / 生产变更 | 必走审批 | 必走审批 | 必走审批 | 必走审批（架构决策） |
| Execution Cancel | PO 触发 | Agent 推荐 | Agent 自动 + 通知 PO | Agent 自动 |
| Mission Cancel | PO 触发 | PO 触发 | PO 触发 | Agent 可触发 + PO 收通知 |
| Review 流程 | PO 手动 review | Agent 推荐 reviewer | Agent 自动 + Policy 控制 | Agent 自动 + Policy 控制 |
| Replan | PO 触发 | PO 触发 | Agent 推 Needs You 建议 | Agent 自动 + 通知 PO |

### 6.2 4 级行为差异代码实现

```ts
// 简化的行为决策表
class AutonomyController {
  decide(action: AutonomyAction, ctx: Ctx): Decision {
    switch (action.kind) {
      case 'mission_grill':
        if (ctx.autonomyLevel >= 1) return { kind: 'AUTO', notify: ctx.autonomyLevel >= 3 ? 'POST' : 'NEEDS_YOU' };
        return { kind: 'REQUIRE_HUMAN' };

      case 'work_unit_split':
        if (ctx.autonomyLevel >= 2) return { kind: 'AUTO', notify: 'NOTIFY' };
        if (ctx.autonomyLevel >= 1) return { kind: 'RECOMMEND', requirePO: true };
        return { kind: 'REQUIRE_HUMAN' };

      case 'work_unit_schedule':
        if (ctx.autonomyLevel >= 1) return { kind: 'AUTO' };
        return { kind: 'REQUIRE_HUMAN' };

      case 'mission_replan':
        if (ctx.autonomyLevel >= 3) return { kind: 'AUTO', notify: 'POST' };
        if (ctx.autonomyLevel >= 2) return { kind: 'AUTO', notify: 'NEEDS_YOU' };
        if (ctx.autonomyLevel >= 1) return { kind: 'RECOMMEND', requirePO: true };
        return { kind: 'REQUIRE_HUMAN' };

      case 'execution_cancel':
        if (ctx.autonomyLevel >= 2) return { kind: 'AUTO', notify: 'NOTIFY' };
        return { kind: 'REQUIRE_HUMAN' };

      case 'mission_cancel':
        if (ctx.autonomyLevel >= 3) return { kind: 'AUTO', notify: 'POST' };
        return { kind: 'REQUIRE_HUMAN' };
    }
  }
}
```

### 6.3 默认 Level

PO 场景默认 **LEVEL 2 SUPERVISED**（v0.1 §23 推荐）。这是 v0.1 唯一 ship 的 level。

V0.2 可加 Level 0 / 1 / 3 的 UI 切换。

## 7. Project Policy 数据模型

```sql
-- project_policies（一个 Project 一份 policy；可 snapshot 进 mission）
CREATE TABLE project_policies (
  id              UUID PRIMARY KEY,
  project_id      UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  policy_yml      JSONB NOT NULL,                    -- 见 §7.1 schema
  version         INT NOT NULL DEFAULT 1,
  is_active       BOOLEAN NOT NULL DEFAULT true,
  created_by_type TEXT NOT NULL CHECK (created_by_type IN ('USER','AGENT','SYSTEM')),
  created_by_id   UUID NOT NULL,
  created_at      TIMESTAMPTZ DEFAULT now(),
  UNIQUE (project_id) WHERE is_active = true          -- 同一 Project 同一时刻 1 份 active
);

-- mission_policy_snapshots（Mission 启动时拍快照，避免 policy 改影响 in-flight decision）
CREATE TABLE mission_policy_snapshots (
  id              UUID PRIMARY KEY,
  mission_id      UUID NOT NULL REFERENCES missions(id) ON DELETE CASCADE,
  policy_yml      JSONB NOT NULL,
  policy_version  INT NOT NULL,
  taken_at        TIMESTAMPTZ DEFAULT now()
);
```

### 7.1 Policy YAML Schema

```yaml
# project_policies.policy_yml 结构
review:
  require_review: true              # 任何 IMPLEMENTATION Execution 后必走 review
  different_agent_required: true    # reviewer != implementer
  auto_open_review: true            # Implementation SUCCEEDED 自动开 review

handoff:
  implementation_to_review: AUTO    # AUTO / REQUIRE_HUMAN
  review_changes_to_fix: AUTO       # 同上
  implementation_to_qa: AUTO        # V0.2
  fix_to_review: AUTO               # 同上

open_claim:
  enabled: true                     # 是否允许 OPEN_CLAIM 模式
  offer_ttl_seconds: 3600           # 1h

escalation:
  product_decision: REQUIRED        # REQUIRED / CONDITIONAL / NEVER
  production_change: REQUIRED
  architecture_change: CONDITIONAL
  cross_story_contradiction: REQUIRED

execution:
  max_retry: 3
  cancel_on_pause: true             # Agent lifecycle=PAUSED 时自动 cancel in-flight

replan:
  auto_replan_on_cross_story: true   # Planner 检测到矛盾自动 Replan（vs Needs You）
  auto_replan_on_split_request: true # Agent 请求 split 自动 Replan

work_item:
  promote_to_work_item: false       # WU 完成是否 promote 为 WorkItem
  promote_timing: ON_COMPLETED      # ON_COMPLETED / ON_PLANNED / NEVER

autonomy:
  level: 2                          # 0-3
  overridable_by: USER               # USER / OWNER / SYSTEM
```

### 7.2 Policy 校验

```python
POLICY_SCHEMA = {
    "review": {
        "type": "object",
        "properties": {
            "require_review": {"type": "boolean"},
            "different_agent_required": {"type": "boolean"},
            "auto_open_review": {"type": "boolean"},
        },
        "additionalProperties": false,
    },
    "handoff": {
        "type": "object",
        "properties": {
            "implementation_to_review": {"enum": ["AUTO", "REQUIRE_HUMAN"]},
            "review_changes_to_fix": {"enum": ["AUTO", "REQUIRE_HUMAN"]},
            ...
        },
    },
    # ...
}

def validate_policy(yml: dict) -> None:
    """Mission start / Policy PATCH 时调"""
    try:
        jsonschema.validate(yml, POLICY_SCHEMA)
    except jsonschema.ValidationError as e:
        raise PolicyValidationError(str(e))
```

## 8. Policy 评估路径（policy.evaluate）

### 8.1 与 E6 Guard 的关系（关键边界）

| 维度 | E6 Permission Guard | Policy.evaluate |
| --- | --- | --- |
| **职责** | "主体对资源能不能做操作"（"agent X 能不能在 channel Y 发消息"） | "在当前 Mission / Project 策略下要不要这么做"（"这个 IMPLEMENTATION 要不要自动 review"） |
| **作用对象** | RBAC | Mission 流程策略 |
| **Effect** | ALLOW / DENY / REQUIRE_APPROVAL | AUTO / REQUIRE_HUMAN / NOTIFY / DEPRECATED |
| **存储** | `permissions` 表 | `project_policies.policy_yml` + snapshot |
| **修改方式** | PATCH permissions REST | PATCH project_policies REST |
| **不互相覆盖** | 永远 ANTE POLICY EVALUATE | 永远 ANTE E6 GUARD |

```python
# 调用顺序：业务层先 E6 Guard → 后 Policy.evaluate
async def business_action(actor, action, scope):
    # 1. E6 Guard
    guard = await permission_service.check(actor, action.perm_key, scope)
    if guard.effect == 'DENY':
        raise ForbiddenError()
    if guard.effect == 'REQUIRE_APPROVAL':
        # E6 必走审批（即使 policy 是 AUTO）
        return await policy_evaluate_with_approval(action, scope)

    # 2. Policy evaluate
    policy = await policy_service.evaluate(action, scope)
    if policy.decision == 'REQUIRE_HUMAN':
        return await escalate_to_human(action, policy)
    if policy.decision == 'AUTO':
        return await auto_proceed(action, policy.notify)
    if policy.decision == 'NOTIFY':
        return await auto_proceed_with_notification(action, policy.notify_targets)
```

### 8.2 policy.evaluate 决策表

```python
def policy_evaluate(action: AutonomyAction, ctx: Ctx) -> PolicyDecision:
    policy = ctx.mission.policy_snapshot or ctx.project.active_policy

    # 1. 查 policy.yml
    if action.kind == 'open_review':
        if not policy.review.auto_open_review:
            return PolicyDecision(kind='NEVER')
        if policy.review.different_agent_required:
            return PolicyDecision(kind='AUTO', constraint='different_agent')
        return PolicyDecision(kind='AUTO')

    if action.kind == 'handoff_to_fix':
        if policy.handoff.review_changes_to_fix == 'AUTO':
            return PolicyDecision(kind='AUTO', notify='NOTIFY')
        return PolicyDecision(kind='REQUIRE_HUMAN')

    if action.kind == 'mission_replan':
        if not policy.replan.auto_replan_on_cross_story:
            return PolicyDecision(kind='NEEDS_YOU')
        return PolicyDecision(kind='AUTO', notify='NEEDS_YOU')

    # ... 其他 action
    return PolicyDecision(kind='AUTO', notify='NONE')  # 默认
```

## 9. Autonomy × Policy 联动

| Autonomy Level | Policy 默认 | 实际行为 |
| --- | --- | --- |
| 0 MANUAL | 全 REQUIRE_HUMAN | PO 触发所有动作 |
| 1 ASSISTED | require_human，但允许 Agent 推荐 | Agent 推 RECOMMEND → PO 必审 |
| 2 SUPERVISED | AUTO + Needs You | Agent 自动 + PO 看到聚合卡 |
| 3 AUTONOMOUS | AUTO + NOTIFY | Agent 自动 + PO 收通知（事后看） |

**关键**：Autonomy Level 是**业务层 wrapper**（包裹 `policy.evaluate` 调用，决定 escalate 还是 auto proceed）。Policy 是**策略事实源**。两者**不**重复表达同一概念。

```python
async function autonomy_decide(action: AutonomyAction, ctx: Ctx) -> Decision:
    # 1. policy 评估
    policy_decision = await policy.evaluate(action, ctx)

    if policy_decision.kind == 'NEVER':
        return Decision.REJECT
    if policy_decision.kind == 'REQUIRE_HUMAN':
        return Decision.ESCALATE  # 不管 autonomy 多少

    # 2. policy 说 AUTO，根据 autonomy 决定怎么通知
    if ctx.autonomy_level == 0:
        return Decision.ESCALATE
    if ctx.autonomy_level == 1:
        return Decision.RECOMMEND
    if ctx.autonomy_level == 2:
        if policy_decision.notify == 'NEEDS_YOU':
            return Decision.AUTO_WITH_NEEDS_YOU
        return Decision.AUTO_WITH_NOTIFY
    if ctx.autonomy_level == 3:
        return Decision.AUTO  # 静默
```

## 10. Domain Events

| aggregate | event_type | payload 关键字段 | 何时 | 投递目标 |
| --- | --- | --- | --- | --- |
| `work_offer` | `work_offer.published` | offer_id, wu_id, eligible_agent_ids[], expires_at | Scheduler 发布 | E3 WS 广播给 eligible Agents |
| `work_offer` | `work_offer.claimed` | offer_id, agent_id, cr_id | Agent claim 成功 | E4 监听 |
| `work_offer` | `work_offer.expired` | offer_id | TTL 到 | Delivery 监听 → 重发布（若 WU 仍 READY）|
| `work_offer` | `work_offer.cancelled` | offer_id, reason | Mission CANCELLED / Planner 拆 WU | — |
| `policy` | `policy.changed` | project_id, policy_yml, version, effective_at | PATCH project_policies | E4 监听 → 重新评估 in-flight decisions（已 ACCEPTED 不重评） |

## 11. 关键边界（与 #1 9 Invariants 联动）

| Invariant | 本文落地 |
| --- | --- |
| **I-7** Open Claim 仍走 E4 CollaborationRequest | §4.2 claim 协议第 3 步 E4 创建 CR；§1.2 OPEN_CLAIM 不做评分 |
| **#1 §3.1 Scheduler 不允许做的事** | §1.2 责任清单；§3.4 DIRECT 模式直接 E4（不发布 work_offer） |
| **#1 §3.1 Project Policy 不允许做的事** | §8.1 与 E6 Guard 边界；Policy 走业务层 policy.evaluate 不经 E6 Guard |

## 12. 反模式

| 反模式 | 后果 | 正确做法 |
| --- | --- | --- |
| **Scheduler 选单个 Collab 的 Agent** | 重复 E4 Resolver 职责 | Scheduler 只做多 WU 批量调度 |
| **Scheduler 调 E7 创建 Execution** | 绕过 E4 → E7 协议 | Scheduler → work_offer → Agent claim → E4 CR |
| **WorkOffer TTL 不设** | 永远 OPEN，无人回收 | 默认 1h；超期 EXPIRED |
| **OPEN_CLAIM 模式下做评分** | Agent 被动被分 | OPEN_CLAIM 主动 claim；评分归 ROUTED |
| **Policy 走 E6 Guard** | REQUIRE_APPROVAL 静默放行 | 业务层 `policy.evaluate()`，不经 E6 |
| **Policy 改后重置已 ACCEPTED CR** | 历史决策抹除 | policy.changed 仅重新评估 in-flight；已 ACCEPTED 不重评 |
| **Autonomy Level 0 + Policy AUTO = 自动执行** | 跳过 PO 审批 | Autonomy 是 wrapper，最终决策 `autonomy_decide()` 统一出口 |
| **Mission 启动不拍 policy snapshot** | policy 中途改影响 in-flight | 启动时拍 snapshot 到 mission_policy_snapshots |
| **work_offer 同 WU 多个 OPEN** | race condition 难排查 | UNIQUE (work_unit_id) WHERE status='OPEN' |
| **claim 后 E4 创建 CR 失败但 work_offer 仍 CLAIMED** | WU 卡死 | claim 事务性：work_offer CLAIMED + CR 创建同事务；失败回滚 |

## 13. e2e 验收点

```
e2e/autonomous-delivery/03-scheduler-autonomy-policy/
  test_001_fastest_scores_critical_path_first.json
    Given Mission objective=FASTEST_COMPLETION, 2 WU READY: W1 critical, W2 non-critical
    And 1 Agent
    Then Scheduler 先发布 W1 offer，再 W2

  test_002_balanced_distributes_load.json
    Given Mission objective=BALANCED, 5 WU READY, 2 Agents
    Then Scheduler 发布 3 给 A，2 给 B（load 平衡）
    And  任何 Agent load 不超 max_concurrency

  test_003_open_claim_publishes_offer.json
    Given Mission policy.open_claim.enabled=true
    When  W1 → READY
    Then  work_offers ×1 落表 (status=OPEN, eligible_agent_ids=[A, B, C])
    And   WS 广播给 A, B, C

  test_004_open_claim_claim_creates_cr.json
    Given work_offer status=OPEN
    When  Agent A claim
    Then  work_offers CAS OPEN → CLAIMED
    And   E4 collaboration_requests 创建 1 行 (target_agent_id=A, assignment_mode=OPEN_CLAIM)
    And   OUTBOX work_unit.claimed 投递

  test_005_open_claim_claim_race_atomic.json
    Given work_offer status=OPEN
    When  Agent A + B 同时 claim
    Then  CAS 只 1 个成功
    And   另一个 409 OfferUnavailableError

  test_006_offer_expires_releases_wu.json
    Given work_offer expires_at < now
    When  Scheduler tick 检测
    Then  work_offers CAS OPEN → EXPIRED
    And   WU 状态重算（仍 READY，可重新发布）

  test_007_autonomy_level_0_requires_human.json
    Given mission autonomy_level=0
    When  W1 IN_PROGRESS, Agent 推 handoff.requested
    Then  autonomy_decide → REQUIRE_HUMAN
    And   Needs You notification 1 张
    And   W1 标 BLOCKED

  test_008_autonomy_level_2_auto_proceeds.json
    Given mission autonomy_level=2
    When  W1 SUCCEEDED
    And   policy.handoff.implementation_to_review = AUTO
    Then  autonomy_decide → AUTO_WITH_NEEDS_YOU
    And   review CR 自动创建
    And   PO 收 Needs You 卡（聚合）

  test_009_policy_changed_does_not_revert_accepted.json
    Given 1 CR 已 ACCEPTED, 1 CR PENDING
    When  PATCH /projects/:id/policy
    Then  ACCEPTED CR 状态不变
    And   PENDING CR 用新 policy 评估

  test_010_policy_snapshot_at_mission_start.json
    Given active policy v3
    When  Mission M start
    Then  mission_policy_snapshots 落 1 行 (policy_yml=active, version=3)
    When  PATCH /projects/:id/policy to v4
    Then  M 仍用 v3
    And   新 Mission 用 v4

  test_011_e6_guard_and_policy_evaluate_order.json
    Given 1 个 Agent A, channel C
    And   permissions: A.write_message=ALLOW
    And   project_policies.mission_cancel: AUTO (autonomy=3)
    When  Agent A 尝试 cancel mission
    Then  E6 Guard: ALLOW
    And   policy.evaluate: AUTO
    And   autonomy_decide: AUTO (level=3)
    And   mission.status → CANCELLED
    And   A 收 post notification

  test_012_e6_deny_overrides_policy.json
    Given 1 个 Agent A
    And   permissions: A.write_message=DENY
    And   project_policies.mission_cancel: AUTO
    When  Agent A 尝试 cancel mission
    Then  E6 Guard: DENY (即使 policy 是 AUTO)
    And   403 ForbiddenError
    And   mission 状态不变

  test_013_promote_policy_controlled.json
    Given mission policy.work_item.promote_to_work_item=true
    When  W1 COMPLETED
    Then  E8 createWorkItem 被调

  test_014_direct_mode_bypasses_offer.json
    Given W1 required_capabilities + target_agent_id (DIRECT)
    When  W1 → READY
    Then  Scheduler **不**发布 work_offer
    And   E4 直接创建 CR (target_agent_id=指定)
    And   W1 status=READY → IN_PROGRESS
```

## 14. 实施 M12 子任务

| 子任务 | 内容 | 依赖 |
| --- | --- | --- |
| M12.1 | DB migration: work_offers / project_policies / mission_policy_snapshots | — |
| M12.2 | WorkOffer Service (CRUD + state machine + CAS claim) | M12.1 |
| M12.3 | ProjectPolicy Service (CRUD + jsonschema 校验) | M12.1 |
| M12.4 | Policy.evaluate Engine (决策表 + Autonomy wrapper) | M12.3 |
| M12.5 | Scheduler Service (评分公式 3 个 objective + work_offer 发布) | M12.2, M11.4 |
| M12.6 | AutonomyController (Level 0-3 decision wrapper) | M12.4 |
| M12.7 | Mission.start 拍 policy snapshot | M12.3, [01 §7](./01-mission-and-grill.md) |
| M12.8 | policy.changed 事件 + in-flight 重新评估 | M12.3, E4 |
| M12.9 | OUTBOX event handlers (work_offer.* + policy.changed) | M12.2, M12.3 |
| M12.10 | REST API: /api/v1/work-offers + /api/v1/projects/:id/policy | M12.2, M12.3 |
| M12.11 | e2e: 14 个验收点 | M12.10 |

**Vertical slice 必备**：M12.1 + M12.2 + M12.5 + M12.9 + M12.10 第 1-3 项应在 M12 早期就端到端跑通（WU READY → Scheduler 发布 work_offer → Agent claim → E4 CR 创建），避免 v0.4 重蹈"vertical slice 排末尾"覆辙。
