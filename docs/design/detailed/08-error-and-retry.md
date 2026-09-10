# Detailed Design · 08 · Error and Retry

> **v0.4.3 修正**：
> 1. **P1-4**：Retry 公式统一为 `max(retry_after, base * 5^n)`，命名 `max_attempts=4` 防 off-by-one
> 2. **P1-5**：ERROR activity 不再参与调度；引入 `AgentHealth`（HEALTHY/DEGRADED/UNHEALTHY）
> 前置：[01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md) / [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)

## 0. 范围

- ERROR 触发分类
- Retry 策略（v0.4.3 改：max_attempts=4 替代 MAX_RETRY=3）
- 永久失败降级
- 取消传播
- **v0.4.3 改**：AgentHealth 替代 ERROR activity 调度

## 1. ERROR 触发分类

| 类别 | code | 可重试？ | 行为 |
| --- | --- | --- | --- |
| Provider 5xx | `PROVIDER_5XX` | ✅ | retry with backoff |
| Provider 429 | `RATE_LIMIT` | ✅ | retry with `retry_after_s` |
| Provider 401 | `PROVIDER_401` | ❌ | 立即 FAILED（auth_failed），通知 owner |
| Provider 403 | `PROVIDER_403` | ❌ | 立即 FAILED（permission denied） |
| Sandbox 启动失败（V3） | `SANDBOX_INIT_FAILED` | ❌ | 立即 FAILED |
| Deadline 超时 | `DEADLINE_EXCEEDED` | ❌ | 立即 TIMEOUT |
| 内部错误 | `INTERNAL_ERROR` | ✅ | retry with backoff |

## 2. Retry 策略（v0.4.3 修复 P1-4）

### 2.1 v0.4.2 的问题

```python
# v0.4.2 表中：
def compute_backoff(attempt_count, retry_after_s=None):
    base = retry_after_s or 5
    return base * (5 ** attempt_count)
# 示例：retry_after=30, attempt 1: 30 * 5^0 = 30

# v0.4.2 同时又说：
backoff_s = max(retry_after_s, 5 * (5 ** attempt_count))
# 示例：retry_after=30, attempt 1: max(30, 5) = 30
# 同一文档两个公式
```

**bug**：
- 两个公式不一致
- `MAX_RETRY=3` 含义模糊（3 total vs 1+3=4 total）

### 2.2 v0.4.3 修复

```python
# v0.4.3 统一公式
def compute_backoff(retry_index, retry_after_s=None):
    """
    retry_index: 0 = after first failure, 1 = after second failure, ...
    retry_after_s: Provider 给的最低等待（如 429 retry-after 头）
    """
    exponential = min(600, 5 * (5 ** retry_index))   # 5s, 25s, 125s, 600s
    if retry_after_s is None:
        return exponential
    return max(exponential, retry_after_s)            # 尊重 Provider hint

# 命名
max_attempts = 4     # 总尝试次数（1 initial + 3 retries）
```

| attempt_no | retry_index | 退避（默认 base=5） | retry_after_s=30 |
| --- | --- | --- | --- |
| 1 (initial) | — | 0（立即） | 0 |
| 2 (1st retry) | 0 | 5s | max(5, 30) = 30s |
| 3 (2nd retry) | 1 | 25s | max(25, 30) = 30s |
| 4 (3rd retry) | 2 | 125s | max(125, 30) = 125s |
| 5+ (MAX) | — | terminal_fail | — |

**关键变化**：
- 单一公式 `max(exponential, retry_after_s)`
- `max_attempts = 4` 明确：1 initial + 3 retries = 4 total
- 退避上限 600s

### 2.3 完整流程

```python
async def handle_error(execution_id, error_envelope):
    code = error_envelope.payload.code

    # 不可重试：立即终态
    if code in ('PROVIDER_401', 'PROVIDER_403', 'SANDBOX_INIT_FAILED'):
        await terminal_fail(execution_id, code)
        return

    # v0.4.4：deadline 触顶是**超时终态**，不是失败
    # agent_executions.status 有 TIMEOUT 枚举（E7 / SYSTEM_DESIGN §4），
    # 写成 FAILED 会让 UI 与统计把"超时"和"执行失败"混为一谈，
    # 也违反 E7 状态机 RUNNING → TIMEOUT 的迁移
    if code == 'DEADLINE_EXCEEDED':
        execution = await get_execution(execution_id)
        await terminal_timeout(execution_id, execution.active_attempt_no, code)
        return

    # 可重试：判断 attempt_count
    execution = await get_execution(execution_id)
    if execution.attempt_count >= max_attempts:
        await terminal_fail(execution_id, 'MAX_ATTEMPTS_EXCEEDED')
        return

    # 计算 backoff
    # v0.4.4：attempt_count = "已发生的尝试次数"，首次失败时为 1。
    # 直接当 retry_index 会得到 5 * 5^1 = 25s，而 §2.2 表格要求首次重试 5s
    backoff_s = compute_backoff(
        retry_index=execution.attempt_count - 1,
        retry_after_s=error_envelope.payload.retry_after_s
    )

    # 入 BullMQ delayed job
    await bullmq.enqueue('execution.retry', {
        'execution_id': execution_id,
        'attempt_no': execution.attempt_count + 1
    }, delay=backoff_s * 1000)
```

## 3. 永久失败降级

（同 v0.4.2，仅命名变化）

```python
async def terminal_fail(execution_id, failure_code, attempt_no=None):
    """
    v0.4.4：attempt_no 由调用方带入（重试路径 = 当前 attempt；超时路径 = 计时器持有的 attempt）。
    不传时退化为旧 CAS，**不具备旧 attempt 隔离能力**，仅用于兼容。
    """
    async with db.transaction() as tx:
        # 1. CAS（带 attempt 条件，与 detailed/01 §4.4 v0.4.4 同模式）
        affected = await tx.execute("""
            UPDATE agent_executions
            SET status='FAILED', completed_at=NOW(),
                failure_code=$1, active_attempt_no=NULL,
                terminal_envelope_id=$2
            WHERE id=$3 AND status IN ('PENDING', 'RUNNING')
              AND ($4::int IS NULL OR active_attempt_no = $4)
        """, failure_code, envelope_id, execution_id, attempt_no)

        if not affected:
            return  # 重复 / attempt 已切换

        # 1b. 收尾该 attempt（用入参，不再反查已被置 NULL 的 active_attempt_no）
        if attempt_no is not None:
            await tx.execute("""
                UPDATE execution_attempts
                SET status='FAILED', completed_at=NOW()
                WHERE execution_id=$1 AND attempt_no=$2 AND status='RUNNING'
            """, execution_id, attempt_no)

        # 2. 写 audit + outbox
        await tx.execute("""
            INSERT INTO outbox_events (event_type, payload, idempotency_key)
            VALUES ('execution.failed', $1, $2)
        """, {...}, f'exec-failed-{execution_id}')

    # 3. 通知 E4（outbox worker）
    # 4. AGENT_OUTPUT 投影 + SYSTEM 事件
```

### 3.1 超时终态（v0.4.4 新增）

`DEADLINE_EXCEEDED` 属于 `agent_executions.status='TIMEOUT'`，**不得**写成 `FAILED`：UI 与 Agent 评分口径里"超时"和"执行失败"是两类信号，E7 / SYSTEM_DESIGN §4 的终态枚举也单独给了 `TIMEOUT`。

```python
async def terminal_timeout(execution_id, attempt_no, failure_code='DEADLINE_EXCEEDED'):
    async with db.transaction() as tx:
        affected = await tx.execute("""
            UPDATE agent_executions
            SET status='TIMEOUT', completed_at=NOW(),
                failure_code=$1, active_attempt_no=NULL,
                terminal_envelope_id=$2
            WHERE id=$3 AND status IN ('PENDING', 'RUNNING')
              AND active_attempt_no = $4
        """, failure_code, envelope_id, execution_id, attempt_no)

        if not affected:
            return  # 重复 / attempt 已切换

        await tx.execute("""
            UPDATE execution_attempts
            SET status='INTERRUPTED', completed_at=NOW()
            WHERE execution_id=$1 AND attempt_no=$2 AND status='RUNNING'
        """, execution_id, attempt_no)

        await tx.execute("""
            INSERT INTO outbox_events (event_type, payload, idempotency_key)
            VALUES ('execution.timeout', $1, $2)
        """, {...}, f'exec-timeout-{execution_id}-{attempt_no}')
```

超时后同样要 `releaseLease`（交 outbox worker），否则 slot 会挂到 lease TTL 到期。

## 4. ERROR 活动状态（v0.4.3 修复 P1-5）

### 4.1 v0.4.2 的问题

```
旧：activity=ERROR → Resolver 降权（"不希望新任务过去"）
```

**问题**：
- E2 文档明确：activity 仅做 UI derived，**不**参与调度
- E4 文档（v0.4.2）明确：调度用 capacity + lifecycle
- 但 E8 又说"activity=ERROR 降权"——自相矛盾

### 4.2 v0.4.3 修复：引入 AgentHealth

```
新维度：AgentHealth（独立于 lifecycle + activity）
  HEALTHY    正常
  DEGRADED   部分失败（如 1 次 5xx），仍可接任务但降权
  UNHEALTHY  连续失败 / auth 失败，不可接新任务
```

```sql
ALTER TABLE agents
  ADD COLUMN health TEXT NOT NULL DEFAULT 'HEALTHY'
    CHECK (health IN ('HEALTHY', 'DEGRADED', 'UNHEALTHY'));
```

**调度规则（v0.4.3 冻结）**：

```
Resolver 选 Agent 条件：
  lifecycle = 'ACTIVE'
  health != 'UNHEALTHY'   # v0.4.3 改：activity 不参与
  Redis tryAcquirePendingDecision 成功
  permission ALLOW
```

**health 转换规则**：

| 事件 | health 转换 |
| --- | --- |
| 1 次 5xx | HEALTHY → DEGRADED（暂不升级回 HEALTHY） |
| 连续 3 次 5xx | DEGRADED → UNHEALTHY |
| 1 次 401/403 | * → UNHEALTHY（auth_failed） |
| 1 次 SUCCEEDED | DEGRADED → HEALTHY（**v0.4.3 改**） |
| owner 调 `/agents/:id/health` POST `{ health: 'HEALTHY' }` | UNHEALTHY → HEALTHY（强制） |

**v0.4.3 关键**：
- Provider 401 **不会**因为 WebSocket 重连就自动恢复
- 必须 owner 更新 credential 后调 health endpoint 才恢复
- 之前的 `status envelope → activity=AVAILABLE` 不能 clear 401

### 4.3 activity 重新明确

```
v0.4.3 冻结 activity 用途：
  - UI 显示（绿/紫/蓝/琥珀/红/灰点）
  - 不参与 Resolver 调度
  - 不参与 ERROR retry 决策
```

### 4.4 ERROR 信息展示（不变）

```
P3 Agent Card:
  状态: ERROR
  原因: 调用失败：API key 无效 [查看日志]
  
  [由 owner 修复后] health: HEALTHY
```

## 5. Cancel vs Error

（同 v0.4.2，详见 02）

## 6. 关键不变量

```
agents.activity  ∈  {OFFLINE, AVAILABLE, THINKING, WORKING, WAITING_CONTEXT, ERROR}
                   仅 UI derived

agents.health    ∈  {HEALTHY, DEGRADED, UNHEALTHY}
                   参与 Resolver 调度（v0.4.3 改）

agents.lifecycle ∈  {ACTIVE, PAUSED, DISABLED}
                   参与 Resolver 调度
```

## 7. E2E 验收点

```
e2e/08-error-retry/
  test_001_5xx_retries.json
    Given Provider returns 5xx 3 times
    When Agent reports error
    Then 4 attempts created (max_attempts=4), backoff applied, final FAILED

  test_002_401_no_retry_no_recovery.json             # v0.4.3 改
    When Provider returns 401
    Then immediately FAILED, no retry
    And  agents.health='UNHEALTHY' (NOT just activity=ERROR)
    And  WebSocket reconnect does NOT auto-recover health
    And  Owner must POST /agents/:id/health { health: 'HEALTHY' } after fixing

  test_003_rate_limit_backoff.json
    When Provider returns 429 retry_after_s=60
    Then backoff=max(5*5^n, 60) for n=0,1,2

  test_004_max_attempts_exceeded.json                # v0.4.3 改命名
    When 4 attempts all fail (1 initial + 3 retries)
    Then status=FAILED (MAX_ATTEMPTS_EXCEEDED), slot released

  test_005_health_degraded_after_one_5xx.json        # v0.4.3 新增
    Given HEALTHY
    When 1 5xx
    Then health=DEGRADED (still routable)

  test_006_health_unhealthy_after_three_5xx.json
    Given HEALTHY
    When 3 consecutive 5xx
    Then health=UNHEALTHY, Resolver excludes

  test_007_health_recovery_after_success.json        # v0.4.3 新增
    Given DEGRADED
    When SUCCEEDED execution
    Then health=HEALTHY

  test_008_health_owner_force_recover.json           # v0.4.3 新增
    Given UNHEALTHY (after 401)
    When owner POST /agents/:id/health { health: 'HEALTHY' }
    Then health=HEALTHY (after credential fixed)

  test_009_activity_does_not_affect_routing.json     # v0.4.3 新增
    Given agents.activity='ERROR'
    When Resolver runs
    Then activity NOT considered; only lifecycle + health + capacity + permission

  test_010_idempotent_error.json
    When same error envelope.id reported twice
    Then second is no-op
```

## 8. 与其他设计的关系

- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（完整 lifecycle）
- 详见 [02-interrupt-and-cancel.md](./02-interrupt-and-cancel.md)（cancel vs error）
- 详见 [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)（WS error envelope）
- 详见 [04-resolver-and-routing.md](./04-resolver-and-routing.md)（slot 释放 + Resolver 不看 activity）
