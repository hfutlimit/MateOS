# E4 · Collaboration & Routing

| 字段 | 值 |
| --- | --- |
| Epic ID | E4 |
| 标题 | Collaboration & Routing |
| 阶段 | MVP（M4） |
| 上游 | PRD v0.4 / SYSTEM_DESIGN v0.3.2 / v0.3.1 协议收口 / v0.3.2 capacity 原子化 |
| 下游 | E3（消息载体 + Trigger 提取）、E7（Accept → 创建 Execution）、E10（audit） |
| 状态 | Draft（v0.4.2 capacity 原子化） |

## 1. 背景与动机

v0.4.1 完成了 E4/E7 协议边界收口。**v0.4.2 关键收口**：

1. **Resolver 选 Agent 后立即 reservation slot**（不是 ACCEPT 后才 acquire）—— 消除并发 race
2. **Slot 计数改为 Redis Lua 原子**（不是 DB + Redis 双事实源）—— agents 表删 `active_slots` 字段
3. **E2 不再持有 Resolver 规则**——E2 只提供 `lifecycle` + `max_concurrency`；调度细节归 E4 维护

## 2. 范围

### 2.1 In Scope

- 4 种 Trigger：MENTION / WORK_ITEM / API / AUTOMATION
- CollaborationRequest status 收敛为 `PENDING | ACCEPTED | REJECTED | NEED_CONTEXT | UNRESOLVED | CANCELLED`
- Mention Resolver（lifecycle=ACTIVE + slot reservation 成功）
- Capability ranking
- Decision 状态机
- Analysis 三件套
- 超时重路由（60s 默认）
- @all 投递
- 协议边界：`collaboration.*`（E4）vs `execution.*`（E7）
- **v0.4.2** Redis Lua atomic slot reservation
- **v0.4.2** Resolver 选 Agent → tryAcquireSlot → send collaboration_request；REJECT/NEED_CONTEXT/timeout → release

### 2.2 Out of Scope

- Delegate 实际路由（V2）
- @all 仲裁阈值动态调整

## 3. 数据模型

```sql
-- agents 表（v0.4.2 改：删 active_slots，保留 max_concurrency）
CREATE TABLE agents (
  id              UUID PRIMARY KEY,
  owner_user_id   UUID NOT NULL REFERENCES users(id),
  credential_id   UUID NOT NULL REFERENCES credentials(id),
  name            TEXT NOT NULL,
  role            TEXT NOT NULL,
  capabilities    JSONB NOT NULL DEFAULT '[]',
  lifecycle       TEXT NOT NULL DEFAULT 'ACTIVE'
                  CHECK (lifecycle IN ('ACTIVE','PAUSED','DISABLED')),
  activity        TEXT NOT NULL DEFAULT 'OFFLINE'
                  CHECK (activity IN ('OFFLINE','AVAILABLE','THINKING','WORKING','WAITING_CONTEXT','ERROR')),
  activity_reason TEXT,
  -- v0.4.2 改：只存 max_concurrency 配置；active_slots 不再是 DB 字段
  -- 原因：active_slots 是 Runtime scheduling state，不是 durable config；用 Redis atomic
  max_concurrency INT NOT NULL DEFAULT 1,
  daily_limit_usd NUMERIC(10,2) DEFAULT 5.00,
  monthly_budget_usd NUMERIC(10,2) DEFAULT 50.00,
  created_at      TIMESTAMPTZ DEFAULT now()
);
-- activity 字段说明：v0.4.2 起仅做 UI derived（E2 仍推 activity 是因为 UI 需要显示；
-- 调度依据改为 Redis semaphore 持有的 active slot 数）

-- 删：agents.active_slots 字段

CREATE TABLE triggers (
  id              UUID PRIMARY KEY,
  trigger_type    TEXT NOT NULL CHECK (trigger_type IN ('MENTION','WORK_ITEM','API','AUTOMATION')),
  trigger_ref     JSONB NOT NULL,
  from_actor_type TEXT NOT NULL,
  from_actor_id   UUID NOT NULL,
  captured_at     TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE collaboration_requests (
  id                      UUID PRIMARY KEY,
  trigger_id              UUID NOT NULL REFERENCES triggers(id),
  trigger_type            TEXT NOT NULL,
  trigger_ref             JSONB NOT NULL,
  request_kind            TEXT NOT NULL
                          CHECK (request_kind IN ('MESSAGE_RESPONSE','WORK_ITEM_EXECUTION','API_CALL','AUTOMATION_RUN')),
  from_actor_type         TEXT NOT NULL,
  from_actor_id           UUID NOT NULL,
  target_agent_id         UUID REFERENCES agents(id),
  required_capabilities   JSONB NOT NULL DEFAULT '[]',
  context_refs            JSONB NOT NULL DEFAULT '{}',
  status                  TEXT NOT NULL DEFAULT 'PENDING'
                          CHECK (status IN ('PENDING','ACCEPTED','REJECTED','NEED_CONTEXT','UNRESOLVED','CANCELLED')),
  -- v0.4.2 新增：slot reservation 引用（用于 release 时反查）
  slot_lease_id           TEXT,                          -- Redis slot lease id（Lua 返回）
  target_execution_id     UUID,                          -- E4 调 E7 API 后回填
  deadline_s              INT NOT NULL DEFAULT 600,
  idempotency_key         TEXT UNIQUE,
  created_at              TIMESTAMPTZ DEFAULT now(),
  resolved_at             TIMESTAMPTZ
);

CREATE TABLE decision_records (
  id                       UUID PRIMARY KEY,
  collaboration_request_id UUID NOT NULL REFERENCES collaboration_requests(id),
  agent_id                 UUID NOT NULL REFERENCES agents(id),
  decision                 TEXT NOT NULL CHECK (decision IN ('ACCEPT','REJECT','NEED_CONTEXT','DELEGATE')),
  reason                   TEXT,
  needs                    JSONB,
  analysis                 JSONB NOT NULL,
  delegate_to              UUID,
  decided_at               TIMESTAMPTZ,
  created_at               TIMESTAMPTZ DEFAULT now()
);
```

## 4. Agent Capacity 原子化（v0.4.2 核心修复）

### 4.1 旧方案的问题

```
agents.max_concurrency = 1
agents.active_slots = 0  (DB 字段)

Request A → Resolver → SELECT agents WHERE active_slots < max_concurrency → 选 B
Request B → Resolver → SELECT agents WHERE active_slots < max_concurrency → 选 B  (同一时刻)
两个都到 B ACCEPT
→ E7 写 active_slots++（两次）→ active_slots = 2，但 max_concurrency = 1
```

**双事实源 + 不可序列化 → race condition。**

### 4.2 新方案：Redis Lua 原子 semaphore（v0.7：per-lease ZSET + fencing）

> 中间的 **Hash 版**（`used` + `leases` hash + 整 key `EXPIRE`）已废弃：`EXPIRE` 作用于整 key，任一 lease 续期会延长其它 lease；`used` 与 `leases` 双字段容易漂移。v0.7 统一为 **ZSET**（每个 lease 自己的 `score = expires_at`），细节与 rebuild 见 `detailed/04` §3.6 与 SYSTEM_DESIGN §4.2.1。

```
# Redis 结构
#   agent-capacity:{agent_id}:leases   ZSET  member=lease_id, score=expires_at(ms)
#   agent-capacity:{agent_id}:config   hash  { max }        ← 来自 agents.max_concurrency
#   agent-capacity:{agent_id}:fence    int   递增（多实例 rebuild 互斥）
#   agent-capacity:{agent_id}:rebuilding  fence 值，SET NX EX 30
```

**EVAL Lua tryAcquireSlot(agent_id, lease_id, ttl_ms)**：

```lua
-- KEYS[1] = agent-capacity:{agent_id}:leases
-- KEYS[2] = agent-capacity:{agent_id}:rebuilding
-- KEYS[3] = agent-capacity:{agent_id}:config
-- ARGV[1] = lease_id
-- ARGV[2] = ttl_ms
-- ARGV[3] = now_ms

if redis.call('EXISTS', KEYS[2]) == 1 then
    return {0, 'REBUILDING'}          -- 重建窗口内不放行
end
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[3])   -- 惰性回收过期 lease
if redis.call('ZSCORE', KEYS[1], ARGV[1]) then
    return {1, 'EXISTS'}              -- 重复 reservation（幂等）
end
local max = tonumber(redis.call('HGET', KEYS[3], 'max') or '1')
if redis.call('ZCARD', KEYS[1]) >= max then
    return {0, 'FULL'}
end
redis.call('ZADD', KEYS[1], tonumber(ARGV[3]) + tonumber(ARGV[2]), ARGV[1])
return {1, 'OK'}
```

**EVAL Lua releaseSlot(agent_id, lease_id)**：

```lua
-- KEYS[1] = agent-capacity:{agent_id}:leases
-- ARGV[1] = lease_id
return redis.call('ZREM', KEYS[1], ARGV[1]) == 1 and 1 or 0
```

**EVAL Lua promoteLease / renewLease**：`ZREM from → ZADD to`（promote）；只改该 member 的 `score`（renew）。**所有写操作带 fence 校验**，小于当前 fence 的写入被丢弃。

### 4.3 单一事实源

- **`max_concurrency`**：DB 字段（durable config）
- **占用集合与到期时间**：Redis ZSET（Runtime scheduling state，可由 DB 重建）
- 不再有 DB + Redis 双写；**没有 `used` 计数**（`ZCARD` 即占用）

## 5. 关键流程

### 5.1 v0.4.2 Resolver 流程（修正 race）

```
1. Trigger 写入 triggers + collaboration_requests (status=PENDING)
2. Resolver 跑 Capability ranking 产出 Top-N 候选
3. 遍历候选（按 score 降序）：
   a) 校验 permission
   b) **tryAcquireSlot(agent_id, lease_id)**
      - 成功 → 写 collaboration_requests.slot_lease_id + status 保持 PENDING，dispatch
      - 失败 → 下一个候选
4. 全部失败 → status=UNRESOLVED + 通知发起人
5. Agent 收到 dispatch 推 collaboration.decision
6a. ACCEPT → **promoteLease(pending_decision → execution)** + E4 事务外直调 E7 API 创建 execution（lease 不释放）
6b. REJECT / NEED_CONTEXT / timeout / 管理员 cancel → **releaseSlot(lease_id)**
7. E7 terminal（execution.completed / failed / timeout）→ outbox → E4 **releaseSlot**
```

**关键**：
- Slot 在 Resolver 投递时 reservation，不等 ACCEPT
- ACCEPT 后 lease 不释放，而是 `promoteLease` 转为 execution lease（E7 负责 renew，E4 负责 release）
- 任何路径失败（REJECT/NEED_CONTEXT/timeout/cancel/E7 终态）→ release
- **v0.7**：ACCEPT 之后 Resolver 的职责结束。dispatch 阶段不存在「本地容量满 → 换候选」分支（容量已由 lease 预占，若出现即 invariant 破坏 → 告警 + 按送达失败处理），详见 `detailed/03` §7.2

### 5.2 超时重路由

```
PENDING 倒计时到：
  → 释放原 agent 的 slot（lease_id）
  → 取下一个候选
  → tryAcquireSlot
  → 成功 → 重新 dispatch
  → 失败 → 继续下一个或标 UNRESOLVED
```

### 5.3 容量边界

```
max_concurrency = 1 时：
  - 同时只能有 1 个 collaboration_request 持有该 agent 的 slot
  - 第二个触发 SELECT agents WHERE max_concurrency=1 → 排队
  
max_concurrency = 3 时：
  - 同 agent 并发 3 个
  - 第 4 个触发 → 选下一个候选
```

## 6. 协议（不变）

```jsonc
{ "type": "collaboration.decision", "payload": {
    "collaboration_request_id": "...",
    "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason": "...", "needs": [...],
    "analysis": { "capability": true, "context_score": 88, "permission": true }
}}
```

## 7. UI

- 决策卡片从 `decision_records` 投影
- mention 胶囊命中结果气泡
- @all 预警文案

## 8. 验收标准

### 8.1 功能

- **F1** Resolver 在选 Agent 后立即 `tryAcquireSlot`，dispatch 前 reservation
- **F2** **v0.4.2 新增** Redis Lua atomic tryAcquireSlot，10k 并发无 race
- **F3** REJECT / NEED_CONTEXT / timeout / 管理员 cancel → releaseSlot
- **F4** ACCEPT 后 lease 由 E7 持有，E7 完成时 release
- **F5** `max_concurrency = 1` 时同 agent 真正串行（2 个请求不同时持有 slot）
- **F6** **v0.4.2 改** agents 表无 `active_slots` 字段（schema 校验）
- **F7** CollaborationRequest status 不含 EXECUTING/COMPLETED/FAILED
- **F8** 超时重路由：释放原 slot + 新 slot

### 8.2 E2E

- `e2e/E4-001-resolver-rank`
- `e2e/E4-002-decision-state-machine`
- `e2e/E4-003-timeout-reroute`
- `e2e/E4-004-all-timeout`
- `e2e/E4-005-mention-visibility`
- `e2e/E4-006-accept-creates-execution`
- `e2e/E4-007-protocol-boundary`
- `e2e/E4-008-resolver-uses-concurrency`
- `e2e/E4-009-cancel-from-actor`
- `e2e/E4-010-slot-reservation-race`（v0.4.2 新）—— 100 并发请求 max_concurrency=1 agent → 只有 1 个 reservation 成功
- `e2e/E4-011-slot-release-on-reject`（v0.4.2 新）—— REJECT 后 slot 立即可用
- `e2e/E4-012-slot-release-on-timeout`（v0.4.2 新）—— PENDING 超时后 slot 释放
- `e2e/E4-013-slot-idempotency`（v0.4.2 新）—— 重复 reservation 同 lease_id 返回成功不增计数

## 9. 与其他 Epic 的关系

- **被依赖**：E7（Accept → 调 E7 API 创建 Execution）
- **依赖**：E2（lifecycle + max_concurrency）/ E3（消息载体）/ E6（permission）
- **冲突裁决**：
  - **v0.4.2 改** 调度细节归 E4；E2 不再含 Resolver 规则
  - activity 字段 E2 仍写（UI derived），但 E4 调度不再依赖

## 10. 风险与开放问题

- **R1**：Redis Lua 脚本在 Cluster 模式下要保证 `agent-capacity:{agent_id}` 落到同一 hash slot → 用 `{agent_id}` 哈希 tag
- **R2**：Redis 故障时 slot 不可用 → 降级：DB 加 `agents.active_slots_fallback`（V2 启用，不进 MVP）
- **R3**：lease TTL 600s 与 deadline_s 600s 对齐；超时后 Lua 自动过期，但要确保 releaseSlot 在超时前调用，否则 30s 内不可用

## 11. 实施顺序（M4）

1. Redis Lua 脚本（tryAcquireSlot / releaseSlot）
2. E2 删 `active_slots` 字段（migration）
3. E4 Resolver 改用 slot reservation
4. ACCEPT / REJECT / timeout / cancel 路径都加 release
5. lease 引用写入 collaboration_requests.slot_lease_id
6. P5 决策卡 projection（不变）
7. E2E 套件
