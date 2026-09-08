# Detailed Design · 04 · Resolver and Routing

> **v0.4.3 修正**：
> 1. **P0-2**：Redis Slot 改 per-lease ZSET（不是 EXPIRE 整个 key）
> 2. **P0-1 配合**：Slot reservation 阶段**不**创建 Execution；lease 类型分 `pending_decision` / `execution` 两种
> 3. **P1-3**：Cancel 不再 mirror Execution 状态（ACCEPTED 独立）
> 前置：[00-overview.md](./00-overview.md) / [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)

## 0. 范围

- Resolver 触发与流程（v0.4.3 改：只到 tryAcquireSlot + 推 collaboration.request，**不**创建 execution）
- Capability ranking 算法
- **v0.4.3 新增**：Per-lease ZSET slot semaphore + lease 类型 / TTL / promotion / renewal
- 60s 超时重路由
- Cancel 传播（**v0.4.3 改**：不镜像 Execution 状态）
- 多 Project 隔离

## 1. Resolver 入口

### 1.1 触发（同 v0.4.2）

```
E3 写 messages + triggers 同步触发
  → 入 BullMQ 队列 mention.resolve
  → Worker 并发：1 个 Node 进程 5 个 worker（默认）
```

### 1.2 v0.4.3 工作流（**关键修正**）

```python
async def resolve(trigger_id: UUID):
    trigger = await get_trigger(trigger_id)
    collab = await get_collaboration_request(trigger_id)

    # 1. 决定 Resolver 范围
    if collab.target_agent_id:
        candidates = [collab.target_agent_id]
    else:
        candidates = await rank_candidates(collab)

    # 2. 遍历候选（按 score 降序）
    for agent_id in candidates:
        # 2.1 权限校验
        if not check_permission(agent_id, 'write_message', channel_scope):
            continue

        # 2.2 ★ v0.4.3 改：tryAcquireSlot 申请 pending_decision lease
        #     TTL 60-90s（覆盖整个 decision 阶段）
        lease_id = f"collab-{collab.id}-PENDING"
        ok, used = await redis_lua.try_acquire_pending_decision(
            agent_id, lease_id, collab.id, ttl=90
        )
        if not ok:
            continue  # capacity 满

        # 2.3 写 collaboration_requests（注意：NOT 创建 execution）
        await db.update_collab(collab.id,
            target_agent_id=agent_id,
            slot_lease_id=lease_id,
            lease_type='pending_decision'  # v0.4.3 新增字段
        )

        # 2.4 ★ v0.4.3 改：推 collaboration.request（不调 E7 创建 execution）
        await runtime_gateway.push_to_agent(agent_id, {
            'type': 'collaboration.request',
            'payload': {
                'collaboration_request_id': collab.id,
                'from_actor': collab.from_actor,
                'context_refs': collab.context_refs,
                'required_capabilities': collab.required_capabilities,
                'deadline_s': 90
            }
        })

        # 2.5 WS 推 collab_request.resolved 给发起人
        await ws_broadcast('collaboration.resolved', {
            'collaboration_request_id': collab.id,
            'status': 'PENDING_DECISION',
            'target_agent_id': agent_id,
            'score': candidate.score
        })

        return  # 成功

    # 3. 全部失败
    await db.update_collab(collab.id, status='UNRESOLVED')
    await notify(trigger.from_actor, '未找到可用 Agent')
```

**关键变化（v0.4.3）**：
- ❌ v0.4.2：在 Resolver 调 `create_execution()` — **Execution 提前到 ACCEPT 之前**
- ✅ v0.4.3：Resolver 只到 `tryAcquireSlot(pending_decision)` + 推 `collaboration.request` — **Execution 在 ACCEPT 后由 outbox worker 创建**

## 2. Capability Ranking

### 2.1 候选筛选

```sql
SELECT a.id, a.lifecycle, a.max_concurrency
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
  -- v0.4.2 改：activity 不再用于过滤
```

### 2.2 评分公式

```python
score = 0.6 * capability_match
      + 0.25 * (1 - load)
      + 0.15 * accept_rate_30d
```

| 因子 | 计算 | 数据源 |
| --- | --- | --- |
| `capability_match` | required_capabilities ∩ agent.capabilities 的覆盖率 | E4 collab.required_capabilities |
| `load` | 当前 active leases / max_concurrency | v0.4.3：Redis ZCARD（per-lease ZSET） |
| `accept_rate_30d` | 30 天 ACCEPT 占总决策的比例 | decision_records 聚合 |

## 3. Redis Per-Lease ZSET Slot Semaphore（v0.4.3 修复 P0-2）

### 3.1 旧方案的问题

**v0.4.2**：

```
key: agent-capacity:{agent_id}
  HASH { used, max, leases->{lease_id} }
  TTL 整个 key = 600s
```

**bug 1：长任务丢 semaphore**
- lease TTL = 600s（覆盖 EXPIRE 整个 key）
- Agent 执行 30 分钟
- 10 分钟后整个 key 过期，used=0，leases={}
- Resolver 误判 capacity available
- max_concurrency=1 实际并发 2

**bug 2：不同 lease 互相续命**
- A 在 00:00 reservation，EXPIRE 600s
- B 在 00:05 reservation，EXPIRE 600s（刷新整个 key TTL）
- A 实际过期时间被延长到 00:05+600=10:05
- per-lease 隔离失效

### 3.2 新方案：Per-Lease ZSET

```
key: agent-capacity:{agentId}:leases
  ZSET
    score = expires_at_ms
    member = lease_id
key: agent-capacity:{agentId}:config
  HASH { max }
```

**关键优势**：
- ZSET 按 score 排序 → ZREMRANGEBYSCORE 一次清理过期 lease
- ZCARD = 当前 active leases 数
- 每个 lease 独立过期

### 3.3 Lua 脚本

```lua
-- tryAcquirePendingDecision(agent_id, lease_id, collab_id, ttl_seconds)
-- KEYS[1] = "agent-capacity:{agent_id}:leases"
-- KEYS[2] = "agent-capacity:{agent_id}:config"
-- ARGV[1] = lease_id
-- ARGV[2] = collab_id
-- ARGV[3] = ttl_seconds (e.g. 90)
-- ARGV[4] = now_ms
-- Returns: {success (0|1), used, max, expires_at_ms}

local now = tonumber(ARGV[4])
local ttl_ms = tonumber(ARGV[3]) * 1000

-- 1. 清理过期 leases
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)

-- 2. 查 max
local max = tonumber(redis.call('HGET', KEYS[2], 'max') or '1')

-- 3. 查当前 used
local used = redis.call('ZCARD', KEYS[1])

-- 4. 检查重复（同 lease_id 视为幂等）
if redis.call('ZSCORE', KEYS[1], ARGV[1]) then
    return {1, used, max, tonumber(redis.call('ZSCORE', KEYS[1], ARGV[1]))}
end

-- 5. 检查容量
if used >= max then
    return {0, used, max, 0}
end

-- 6. 写入
local expires_at = now + ttl_ms
redis.call('ZADD', KEYS[1], expires_at, ARGV[1])
return {1, used + 1, max, expires_at}
```

```lua
-- releaseLease(agent_id, lease_id)
-- KEYS[1] = "agent-capacity:{agent_id}:leases"
-- ARGV[1] = lease_id
-- Returns: {released (0|1), used}

local removed = redis.call('ZREM', KEYS[1], ARGV[1])
local used = redis.call('ZCARD', KEYS[1])
return {removed, used}
```

```lua
-- promoteLease(agent_id, old_lease_id, new_lease_id, new_ttl_seconds)
-- KEYS[1] = "agent-capacity:{agent_id}:leases"
-- ARGV[1] = old_lease_id
-- ARGV[2] = new_lease_id
-- ARGV[3] = new_ttl_seconds
-- ARGV[4] = now_ms
-- Returns: {success (0|1), expires_at_ms}

-- 1. 验证 old lease 存在
if not redis.call('ZSCORE', KEYS[1], ARGV[1]) then
    return {0, 0}  -- 已过期或不存在
end

-- 2. 删 old
redis.call('ZREM', KEYS[1], ARGV[1])

-- 3. 加 new
local now = tonumber(ARGV[4])
local expires_at = now + tonumber(ARGV[3]) * 1000
redis.call('ZADD', KEYS[1], expires_at, ARGV[2])
return {1, expires_at}
```

```lua
-- renewExecutionLease(agent_id, lease_id, extend_ttl_seconds)
-- KEYS[1] = "agent-capacity:{agent_id}:leases"
-- ARGV[1] = lease_id
-- ARGV[2] = extend_ttl_seconds
-- ARGV[3] = now_ms
-- Returns: {success (0|1), expires_at_ms}

if not redis.call('ZSCORE', KEYS[1], ARGV[1]) then
    return {0, 0}  -- 已过期
end

local now = tonumber(ARGV[3])
local new_expires = now + tonumber(ARGV[2]) * 1000
redis.call('ZADD', KEYS[1], 'GT', new_expires, ARGV[1])  -- 仅当 new > current 时更新
return {1, new_expires}
```

### 3.4 关键不变量（v0.4.3 修正）

```
DB.agents.max_concurrency = M
Redis ZCARD agent-capacity:{id}:leases = N
0 ≤ N ≤ M
每个 lease 独立 expires_at

leases 数量 == active collab_request 数量
其中 pending_decision leases + execution leases

PENDING_DECISION 阶段（ACCEPT 前）：
  lease TTL 60-90s（覆盖 decision 阶段）

EXECUTION 阶段（ACCEPT 后）：
  lease TTL 300s（默认）
  Agent 每 60s 调 renewExecutionLease 续期
  长任务 24h 也没问题
```

### 3.5 Lease 类型与生命周期（v0.4.3 核心）

| 阶段 | lease 类型 | TTL | 续期 | 谁来 release |
| --- | --- | --- | --- | --- |
| ACCEPT 前 | `pending_decision` | 60-90s | 否（短 TTL） | E4：REJECT / NEED_CONTEXT / timeout / cancel / ACCEPT→promote |
| ACCEPT 后 | `execution` | 300s | Agent 每 60s renewExecutionLease | E7：execution.completed → E4 release |

**v0.4.3 关键**：
- ACCEPT 不释放 lease，而是 `promoteLease(pending_decision → execution)`
- 同 agent 同一个 slot 流转，无 capacity 释放 + 重占
- 长任务靠 renewLease 续期
- 旧 pending_decision lease 永远不超 90s（即使 Agent 无限 hang）

### 3.6 启动时初始化

```python
async def init_agent_capacity(agent_id):
    # 1. 读 agents.max_concurrency
    agent = await get_agent(agent_id)
    max = agent.max_concurrency
    await redis.hset(f'agent-capacity:{agent_id}:config', 'max', max, ex=86400)

    # 2. 从 DB 重建 active leases
    collabs = await db.query("""
        SELECT id FROM collaboration_requests
        WHERE target_agent_id = $1
          AND status IN ('PENDING', 'ACCEPTED')
    """, agent_id)
    for collab in collabs:
        await redis.zadd(f'agent-capacity:{agent_id}:leases', {f'collab-{collab.id}-PENDING': now_ms + 90*1000})
```

**V1 简化**：不重建 execution lease（执行中的 lease 丢了→execution ATTEMPT 会因 capacity 满而 retry，无数据丢失）。V2 重建。

## 4. 60s 超时重路由

### 4.1 倒计时

```python
# v0.4.3 改：仅 reservation 后启动倒计时（不依赖 execution 创建）
await bullmq.enqueue('collab.timeout',
    {'collab_id': collab.id},
    delay=90 * 1000  # 与 pending_decision lease TTL 对齐
)
```

### 4.2 超时处理

```python
async def on_collab_timeout(collab_id):
    collab = await get_collab(collab_id)
    if collab.status != 'PENDING':
        return  # 已 resolved

    # 1. 释放当前候选的 pending_decision lease
    if collab.slot_lease_id:
        await release_lease(collab.target_agent_id, collab.slot_lease_id)
        await db.update_collab(collab_id, slot_lease_id=null, target_agent_id=null)

    # 2. 取下一个候选
    candidates = await rank_candidates(collab)
    next_candidate = next((c for c in candidates if c.id != collab.last_attempted_agent_id), None)
    if not next_candidate:
        await db.update_collab(collab_id, status='UNRESOLVED')
        await notify(collab.from_actor, '未找到 Agent')
        return

    # 3. 重试（同 1.2 流程）
    await try_resolve_with(collab, next_candidate)
```

## 5. Cancel 传播（v0.4.3 修正 P1-3）

### 5.1 v0.4.2 的错误做法

```
User cancel →
  collab.status: PENDING or ACCEPTED → CANCELLED
  release lease
  cancel execution
```

**问题**：如果 collab 已是 ACCEPTED，cancel 后 collab=ACCEPTED→CANCELLED 抹掉了 Agent 接受的历史事实。

### 5.2 v0.4.3 修正

```
CollaborationRequest.status (事实源，独立 lifecycle):
  PENDING  →  ACCEPTED   terminal decision（用户取消时不改回）
            →  REJECTED
            →  NEED_CONTEXT
            →  UNRESOLVED  (timeout, 无候选)
            →  CANCELLED   (仅 PENDING 状态可标)

Execution.status (独立 lifecycle):
  PENDING  →  RUNNING  →  SUCCEEDED
                         →  FAILED
                         →  CANCELLED   (用户取消/lifecycle 变化/超时)
                         →  TIMEOUT
```

**关键**：
- ACCEPTED 后用户取消 Execution → collab 仍 ACCEPTED（Agent 接受了），execution 变 CANCELLED
- UI projection：`display_state = "Accepted · execution cancelled"`
- 仅 collab 在 PENDING 状态可标 CANCELLED

### 5.3 完整 Cancel 流程

```python
async def on_user_cancel(collab_id, actor):
    collab = await get_collab(collab_id)
    if collab.status != 'PENDING':
        raise InvalidStateError(f'Cannot cancel: status={collab.status}')

    async with db.transaction() as tx:
        # 1. CAS collab → CANCELLED
        affected = await tx.execute("""
            UPDATE collaboration_requests
            SET status='CANCELLED', resolved_at=NOW()
            WHERE id=$1 AND status='PENDING'
        """, collab_id)

        if not affected:
            raise StaleStateError()

        # 2. 释放 pending_decision lease
        if collab.slot_lease_id:
            await release_lease(collab.target_agent_id, collab.slot_lease_id)

        # 3. 写 outbox
        await tx.execute("""
            INSERT INTO outbox_events (event_type, payload, idempotency_key)
            VALUES ('collaboration.cancelled', $1, $2)
        """, {...}, f'collab-cancelled-{collab_id}')

    # 4. 通知发起人
    await notify(actor, '协作请求已取消')
```

```python
async def on_user_cancel_execution(execution_id, actor):
    """v0.4.3 新增：仅取消 execution，不动 collab"""
    async with db.transaction() as tx:
        affected = await tx.execute("""
            UPDATE agent_executions
            SET status='CANCELLED', completed_at=NOW()
            WHERE id=$1 AND status IN ('PENDING', 'RUNNING')
        """, execution_id)

        if not affected:
            raise StaleStateError()

        await tx.execute("""
            INSERT INTO outbox_events (event_type, payload, idempotency_key)
            VALUES ('execution.cancelled', $1, $2)
        """, {...}, f'exec-cancelled-{execution_id}')

    # collab.status 保持 ACCEPTED（用户仅取消了执行，未撤销接受）
```

### 5.4 Lifecycle 变化（E2 → E4）

```
PATCH /agents/:id { lifecycle: 'PAUSED' }
  → E2 UPDATE agents.lifecycle
  → E4 收到 lifecycle_changed 事件
  → E4: 取消该 agent 所有 PENDING collab + 取消所有 RUNNING execution
  → collab 取消（仅 PENDING 状态）
  → execution 取消（独立 lifecycle）
```

## 6. 消息流投影

（不变，详见 01 §1.1）

## 7. 路由不变式（DB）

（不变，详见 E8 work-management-core.md）

## 8. 性能目标

| 指标 | P95 | P99 |
| --- | --- | --- |
| Resolver 选 Agent | < 500ms | < 1s |
| tryAcquirePendingDecision（Redis Lua ZSET） | < 5ms | < 10ms |
| promoteLease（pending → execution） | < 5ms | < 10ms |
| renewExecutionLease | < 5ms | < 10ms |
| E4 推 `collaboration.request` WS | < 200ms | < 500ms |
| Mention 解析总耗时 | < 1s | < 2s |
| 60s/90s 倒计时精度 | ±1s | ±2s |
| Lease 释放延迟 | < 200ms | < 500ms |

## 9. E2E 验收点

```
e2e/04-resolver-routing/
  test_001_basic_resolve.json
    Given 1 active agent in channel
    When User @agent
    Then collab_request 命中该 agent，pending_decision lease reservation 成功
    And  push collaboration.request to agent

  test_002_execution_after_accept_only.json   # v0.4.3 新增
    Given Resolver 选中 Agent
    When Agent REJECT
    Then NO agent_executions row created (verified by SELECT COUNT)
    And  pending_decision lease released

  test_003_capacity_full_routes_to_next.json
    Given 2 active agents, A max_concurrency=1 with 1 active lease
    When User @any
    Then Resolver 选 A 失败（ZCARD=1 >= max=1）→ 选 B

  test_004_slot_race_concurrent.json          # v0.4.3 ZSET 版本
    Given max_concurrency=1, ZCARD=0
    When 100 并发 mention
    Then 只有 1 个 ZADD 成功，ZCARD=1

  test_005_long_execution_lease_renewal.json  # v0.4.3 新增
    Given Execution 1 小时未完成
    When Agent 每 60s 调 renewExecutionLease
    Then ZSCORE 持续更新，ZCARD 稳定为 1
    And  不被误判 capacity 满

  test_006_long_execution_no_renewal_timeout.json
    Given Execution 1 小时不调 renew（Agent 死）
    When 300s 后
    Then ZREMRANGEBYSCORE 清理，ZCARD=0
    And  execution FAILED (lease_expired)

  test_007_timeout_reroute.json
    Given 2 agents A, B
    When mention to A，A 90s 不决策
    Then 释放 A 的 pending_decision lease，尝试 B

  test_008_promote_lease_at_accept.json        # v0.4.3 新增
    Given Reserver reservation pending_decision lease
    When Agent ACCEPT
    Then promoteLease: pending_decision 删，execution 加
    And  ZCARD 不变（同 slot 流转）
    And  execution lease TTL = 300s

  test_009_lifecycle_pause_cancel_prompt_inflight.json
    Given 1 in-flight collab on A
    When A.lifecycle=PAUSED
    Then collab PENDING → CANCELLED
    And  pending_decision lease 释放

  test_010_lifecycle_pause_doesnt_revert_accept.json  # v0.4.3 新增
    Given 1 collab ACCEPTED on A, execution RUNNING
    When A.lifecycle=PAUSED
    Then collab 仍 ACCEPTED（不抹除）
    And  execution → CANCELLED（独立 lifecycle）
    And  execution lease 释放

  test_011_user_cancel_pending.json
    When POST /collab/:id/cancel (PENDING)
    Then collab.status=CANCELLED, lease 释放

  test_012_user_cancel_accepted_only_execution.json  # v0.4.3 新增
    Given collab ACCEPTED, execution RUNNING
    When POST /executions/:id/cancel
    Then execution.status=CANCELLED, collab 仍 ACCEPTED
    And  UI projection 显示 "Accepted · execution cancelled"

  test_013_cross_project_isolation.json
    Given Agent A in Project X
    When Mention in Project Y
    Then A 不在候选
```

## 10. 与其他设计的关系

- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（端到端时序）
- 详见 [02-interrupt-and-cancel.md](./02-interrupt-and-cancel.md)（Cancel 完整路径）
- 详见 [07-permission-and-approval-orchestration.md](./07-permission-and-approval-orchestration.md)（permission 校验）
