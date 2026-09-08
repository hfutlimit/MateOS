# Detailed Design · 04 · Resolver and Routing

> E4 Mention Resolver + Redis Lua atomic slot reservation + 60s 超时重路由。
> 前置：[00-overview.md](./00-overview.md) / [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)

## 0. 范围

- Resolver 触发与流程
- Capability ranking 算法
- **Redis Lua atomic slot reservation**（v0.4.2 核心修复）
- 60s 超时重路由
- Cancel 传播
- 多 Project 隔离

## 1. Resolver 入口

### 1.1 触发

```
E3 写 messages + triggers 同步触发
  → 入 BullMQ 队列 mention.resolve (queue=default, priority=NORMAL)
  → Worker 并发：1 个 Node 进程 5 个 worker（默认）
```

### 1.2 工作流

```python
async def resolve(trigger_id: UUID):
    trigger = await get_trigger(trigger_id)
    collab = await get_collaboration_request(trigger_id)

    # 1. 决定 Resolver 范围
    if collab.target_agent_id:
        # 显式指定 → 跳过 Resolver，直接 tryAcquireSlot
        candidates = [collab.target_agent_id]
    else:
        # 自动解析
        candidates = await rank_candidates(collab)

    # 2. 遍历候选（按 score 降序）
    for agent_id in candidates:
        # 2.1 权限校验
        if not check_permission(agent_id, 'write_message', channel_scope):
            continue

        # 2.2 容量原子 reservation
        lease_id = f"collab-{collab.id}"
        ok, used = await redis_lua.try_acquire_slot(
            agent_id, lease_id, collab.id, ttl=600
        )
        if not ok:
            continue  # capacity 满，下一个

        # 2.3 写 collaboration_requests.slot_lease_id + target_agent_id
        await db.update_collab(collab.id,
            target_agent_id=agent_id,
            slot_lease_id=lease_id
        )

        # 2.4 推 E7 内部 API 创建 execution
        await create_execution(collab.id, agent_id, ...)

        # 2.5 推 collaboration_request.resolved WS 事件
        await ws_broadcast('collaboration.resolved', {
            'collaboration_request_id': collab.id,
            'target_agent_id': agent_id,
            'score': candidate.score
        })

        return  # 成功

    # 3. 全部失败
    await db.update_collab(collab.id, status='UNRESOLVED')
    await notify(trigger.from_actor, '未找到可用 Agent')
```

## 2. Capability Ranking

### 2.1 候选筛选

```sql
-- 在 channel member 中 + lifecycle=ACTIVE + permission 允许
SELECT a.id, a.lifecycle, a.max_concurrency,
       capabilities
FROM agents a
JOIN channel_members cm ON cm.member_id = a.id
                         AND cm.member_type = 'AGENT'
                         AND cm.channel_id = $1
WHERE a.lifecycle = 'ACTIVE'
  AND a.id IN (
      SELECT subject_id FROM permissions
      WHERE scope_type = 'CHANNEL'
        AND scope_id = $1
        AND subject_type = 'AGENT'
        AND perm_key = 'write_message'
        AND effect = 'ALLOW'
  )
```

### 2.2 评分公式

```python
score = 0.6 * capability_match
      + 0.25 * (1 - load)
      + 0.15 * accept_rate_30d
```

| 因子 | 计算 | 数据源 |
| --- | --- | --- |
| `capability_match` | 0-1：required_capabilities ∩ agent.capabilities 的覆盖率 | E4 collab.required_capabilities |
| `load` | 0-1：当前 in-flight executions / max_concurrency | E7 in-flight + E2 max_concurrency |
| `accept_rate_30d` | 0-1：30 天内 ACCEPT 占总决策的比例 | decision_records 聚合 |

注：v0.4.2 之前 `load` 读 `agents.active_slots`（DB 字段，racy）；现在改读 Redis `agent-capacity:{agent_id}.used`。

### 2.3 排序输出

```python
candidates = sorted(candidates, key=lambda c: c.score, reverse=True)[:3]
# Top-N=3（可配）
```

## 3. Redis Lua Atomic Slot Reservation（v0.4.2 核心修复）

### 3.1 旧方案的问题

**v0.4.1**：`agents.max_concurrency` + `agents.active_slots` 双事实源

```
T0:  Resolver A: SELECT active_slots=0 < 1 → 选 B
T1:  Resolver B: SELECT active_slots=0 < 1 → 选 B
T2:  Resolver A 写 active_slots=1
T3:  Resolver B 写 active_slots=1 (覆盖？)
T4:  实际值=1，但有两个 in-flight
```

**根因**：
- SELECT 和 UPDATE 不是原子
- 两个 Resolver 看到过期快照
- max_concurrency=1 形同虚设

### 3.2 新方案：单一事实源（Redis Lua）

**DB 字段**：
- `agents.max_concurrency`（durable config）

**Redis 字段**：
- `agent-capacity:{agent_id}` HASH
  - `used`：当前已 reservation 的 slot 数
  - `max`：从 agents.max_concurrency 启动时写入
  - `leases->{lease_id}`：{collab_request_id}（hash of hash）

### 3.3 Lua 脚本

```lua
-- tryAcquireSlot(agent_id, lease_id, collab_id, ttl_seconds)
-- KEYS[1] = "agent-capacity:{agent_id}"
-- ARGV[1] = lease_id
-- ARGV[2] = collab_id
-- ARGV[3] = ttl_seconds
-- Returns: {success (0|1), used, max}

local current_lease = redis.call('HGET', KEYS[1], 'leases->' .. ARGV[1])
if current_lease then
    -- 重复 reservation（idempotency）
    local used = tonumber(redis.call('HGET', KEYS[1], 'used') or '0')
    return {1, used, used}
end

local used = tonumber(redis.call('HGET', KEYS[1], 'used') or '0')
local max = tonumber(redis.call('HGET', KEYS[1], 'max') or '1')

if used >= max then
    return {0, used, max}
end

redis.call('HSET', KEYS[1], 'used', used + 1)
redis.call('HSET', KEYS[1], 'leases->' .. ARGV[1], ARGV[2])
redis.call('EXPIRE', KEYS[1], tonumber(ARGV[3]))
return {1, used + 1, max}
```

```lua
-- releaseSlot(agent_id, lease_id)
-- KEYS[1] = "agent-capacity:{agent_id}"
-- ARGV[1] = lease_id
-- Returns: {released (0|1), used}

local collab = redis.call('HGET', KEYS[1], 'leases->' .. ARGV[1])
if not collab then
    return {0, tonumber(redis.call('HGET', KEYS[1], 'used') or '0')}
end

redis.call('HDEL', KEYS[1], 'leases->' .. ARGV[1])
local used = tonumber(redis.call('HGET', KEYS[1], 'used') or '0')
used = math.max(0, used - 1)
redis.call('HSET', KEYS[1], 'used', used)
return {1, used}
```

### 3.4 关键不变量

```
DB.agents.max_concurrency = M
Redis.agent-capacity:{id}.max = M
Redis.agent-capacity:{id}.used = N
0 ≤ N ≤ M
leases 数量 == N（一致性）
N == 当前 in-flight executions 数量（实际）
```

### 3.5 启动时初始化

```
Agent 上线（首次 hello）：
  1. 读 agents.max_concurrency
  2. HSET agent-capacity:{id} max {M}
  3. 重新计算 used = COUNT(in-flight executions)
  4. HSET agent-capacity:{id} used {N}
  5. 重建 leases（从 in-flight executions 反查 collaboration_request_id）
```

### 3.6 Slot 提前 reservation（关键修正）

```
Resolver 选 Agent → 立即 tryAcquireSlot → 成功才 dispatch
```

**为什么不在 ACCEPT 后再 reservation**：
- 语义反了："我可以"（ACCEPT）但"你不行"（no capacity）
- 用户消息流已显示 "ACCEPT"，突然变 "FAILED" 很奇怪
- 提前 reservation 让 Resolver 选 Agent 时就考虑 capacity

### 3.7 Slot 释放时机

| 场景 | 释放者 | 释放时机 |
| --- | --- | --- |
| Agent REJECT | E4 | E4 收到 collaboration.decision.REJECT 后立即 |
| Agent NEED_CONTEXT | E4 | E4 收到 .NEED_CONTEXT 后立即 |
| 协作 timeout（60s） | E4 | 标 UNRESOLVED 后立即 |
| 管理员 cancel | E4 | 标 CANCELLED 后立即 |
| lifecycle 变 PAUSED/DISABLED | E2 → E4 | E4 收到 lifecycle 事件后取消所有 in-flight + release |
| Execution SUCCEEDED/FAILED/CANCELLED/TIMEOUT | E7 → E4 | E7 推 execution.completed 事件 → E4 release |
| 长时断线（> 5min）| E7 | cancel execution + release |

**关键**：`collaboration_requests.slot_lease_id` 字段记录 lease_id，E4 任何释放路径都用这个 lease_id。

## 4. 60s 超时重路由

### 4.1 倒计时

```
collab_request 写 PENDING → BullMQ delayed job @ 60s
```

### 4.2 超时处理

```python
async def on_collab_timeout(collab_id):
    collab = await get_collab(collab_id)
    if collab.status != 'PENDING':
        return  # 已 resolved

    # 1. 释放当前候选的 slot
    if collab.slot_lease_id:
        await release_slot(collab.target_agent_id, collab.slot_lease_id)
        await db.update_collab(collab_id, slot_lease_id=null, target_agent_id=null)

    # 2. 取下一个候选（重新 rank）
    candidates = await rank_candidates(collab)
    next_candidate = next((c for c in candidates if c.id != collab.last_attempted_agent_id), None)
    if not next_candidate:
        # 全部失败
        await db.update_collab(collab_id, status='UNRESOLVED')
        await notify(collab.from_actor, '未找到 Agent')
        return

    # 3. 重试
    await try_resolve_with(collab, next_candidate)
```

### 4.3 最大重试次数

```python
MAX_ROUTING_ATTEMPTS = 3  # 60s × 3 = 最多 3 分钟
```

超过 → 标 UNRESOLVED + 通知。

## 5. Cancel 传播

### 5.1 来自用户的 cancel

```
POST /collaboration-requests/:id/cancel
  → E4:
     a) CAS UPDATE collab_request SET status='CANCELLED' WHERE status IN ('PENDING','ACCEPTED')
     b) 释放 slot（如果有 slot_lease_id）
     c) 推 E7 cancel execution（如果 target_execution_id）
     d) WS 推 collab_request.cancelled 给发起人 channel
```

### 5.2 来自 lifecycle 变化的 cancel（E2 → E4）

```
PATCH /agents/:id { lifecycle: 'PAUSED' }
  → E2:
     a) UPDATE agents SET lifecycle='PAUSED'
     b) WS 推 agent.lifecycle_changed
     c) WS 推 E4：lifecycle_changed event
  → E4:
     a) 查所有 in-flight collab_request WHERE target_agent_id=? AND status='PENDING' or 'ACCEPTED'
     b) 对每个：
        - 标 CANCELLED
        - 释放 slot
        - 推 E7 cancel execution
     c) 通知 owner: "已取消 N 个协作"
```

### 5.3 来自 E7 execution completed 的 slot 释放

```
E7 Execution SUCCEEDED/FAILED/CANCELLED → WS 推 execution.completed
  → E4 收到 → 查 collab_request.slot_lease_id → 释放 slot
```

## 6. 消息流投影

### 6.1 消息流 timeline items（v0.4.1 投影）

| 状态变化 | 消息流事件 |
| --- | --- |
| Resolver 命中 | mention 胶囊 + 候选分数气泡（hover 展开） |
| Agent 推 collaboration.decision | DECISION 投影（content.decision_ref = decision_records.id） |
| E4 写 decision | （同上一行） |
| E7 Execution 完成 | AGENT_OUTPUT 投影（content.execution_ref = agent_executions.id） |
| 协作 timeout | SYSTEM 事件 "Backend Agent 60s 无响应" |
| 协作 cancel | SYSTEM 事件 "Jason 取消了协作 #42" |
| lifecycle 变化 | （不进消息流，UI 全局状态点变化） |

## 7. 路由不变式（DB）

```sql
-- 同一 active provider 同 project 唯一
CREATE UNIQUE INDEX uq_project_active_work_provider
  ON work_item_bindings(project_id)
  WHERE is_active = true;
```

（详见 E8 work-management-core.md）

## 8. 性能目标

| 指标 | P95 | P99 |
| --- | --- | --- |
| Resolver 选 Agent | < 500ms | < 1s |
| tryAcquireSlot（Redis Lua） | < 5ms | < 10ms |
| E4 → E7 内部 API | < 100ms | < 200ms |
| Mention 解析总耗时 | < 1s | < 2s |
| 60s 倒计时精度 | ±1s | ±2s |
| Slot 释放延迟 | < 200ms | < 500ms |

## 9. E2E 验收点

```
e2e/04-resolver-routing/
  test_001_basic_resolve.json
    Given 1 active agent in channel
    When User @agent
    Then collab_request 命中该 agent，slot_reservation 成功

  test_002_capacity_full_routes_to_next.json
    Given 2 active agents, agent A max_concurrency=1 with slot=1/1 used
    When User @any
    Then Resolver 选 A 失败 → 选 B

  test_003_slot_race_concurrent.json
    Given max_concurrency=1, slot=0/1
    When 100 并发 mention
    Then 只有 1 个 reservation 成功，其余 99 个走下一个候选或 UNRESOLVED

  test_004_timeout_reroute.json
    Given 2 agents A, B
    When mention to A，A 60s 不决策
    Then 释放 A 的 slot，尝试 B

  test_005_lifecycle_pause_cancels_inflight.json
    Given 1 in-flight collab on A
    When A.lifecycle=PAUSED
    Then collab_request 标 CANCELLED + slot 释放

  test_006_user_cancel.json
    When POST /collab/:id/cancel
    Then status=CANCELLED, slot 释放, E7 execution 收到 cancel

  test_007_reject_releases_slot.json
    Given Resolver 选 A, A REJECT
    Then slot 立即释放，UNRESOLVED 或下一个候选

  test_008_cross_project_isolation.json
    Given Agent A in Project X
    When Mention in Project Y
    Then A 不在候选
```

## 10. 与其他设计的关系

- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（端到端时序）
- 详见 [02-interrupt-and-cancel.md](./02-interrupt-and-cancel.md)（Cancel 完整路径）
- 详见 [07-permission-and-approval-orchestration.md](./07-permission-and-approval-orchestration.md)（permission 校验）
