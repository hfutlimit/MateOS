# Detailed Design · 08 · Error and Retry

> ERROR 触发 + retry attempt + 退避 + 永久失败降级。
> 前置：[01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md) / [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)

## 0. 范围

- ERROR 触发分类
- Retry 策略（attempt_count + 退避）
- 永久失败降级
- 取消/取消传播
- ERROR 活动状态 + UI 反馈

## 1. ERROR 触发分类

| 类别 | code | 可重试？ | 行为 |
| --- | --- | --- | --- |
| Provider 5xx | `PROVIDER_5XX` | ✅ | retry with backoff |
| Provider 429 | `RATE_LIMIT` | ✅ | retry with `retry_after_s` |
| Provider 401 | `PROVIDER_401` | ❌ | 立即 FAILED（auth_failed），通知 owner |
| Provider 403 | `PROVIDER_403` | ❌ | 立即 FAILED（permission denied） |
| Sandbox 启动失败（V3） | `SANDBOX_INIT_FAILED` | ❌ | 立即 FAILED |
| Deadline 超时 | `DEADLINE_EXCEEDED` | ❌ | 立即 TIMEOUT |
| 内部错误（代码 bug） | `INTERNAL_ERROR` | ✅ | retry with backoff（最后兜底） |

## 2. 数据流

### 2.1 触发 ERROR

```
Agent 收到 Provider 异常:
  1. 推 error envelope:
     { type: 'error', id, payload: {
         execution_id, attempt_no,
         code: 'PROVIDER_5XX',
         message: '...',
         retry_after_s: 30
       }}
  2. Runtime 收到:
     a) 校验 envelope id 去重
     b) 写 audit
     c) 推 status=ERROR (activity 维度，Agent.lifecycle 不变)
     d) 根据 code 决定 retry 还是终态
```

### 2.2 retry 决策

```python
async def handle_error(execution_id, error_envelope):
    code = error_envelope.payload.code

    # 不可重试：立即终态
    if code in ('PROVIDER_401', 'PROVIDER_403', 'SANDBOX_INIT_FAILED', 'DEADLINE_EXCEEDED'):
        await terminal_fail(execution_id, code)
        return

    # 可重试：判断 attempt_count
    execution = await get_execution(execution_id)
    if execution.attempt_count >= MAX_RETRY:
        await terminal_fail(execution_id, 'MAX_RETRY_EXCEEDED')
        return

    # 计算 backoff
    backoff_s = compute_backoff(execution.attempt_count, error_envelope.payload.retry_after_s)

    # 入 BullMQ delayed job
    await bullmq.enqueue('execution.retry', {
        'execution_id': execution_id,
        'attempt_no': execution.attempt_count + 1
    }, delay=backoff_s * 1000)
```

### 2.3 backoff 公式

```python
def compute_backoff(attempt_count, retry_after_s=None):
    base = retry_after_s or 5  # Provider 给的优先
    # 指数退避：5s, 25s, 125s, 625s, 3125s
    return base * (5 ** attempt_count)  # 5^attempt_count
    # capped at 600s（10 分钟）
```

| attempt | backoff（默认 base=5） |
| --- | --- |
| 1 | 5s |
| 2 | 25s |
| 3 | 125s |
| 4 | 625s → capped 600s |
| 5 | MAX_RETRY_EXCEEDED |

### 2.4 MAX_RETRY

```python
MAX_RETRY = 3  # V1：最多 3 次重试
```

## 3. Retry 完整流程

### 3.1 时序

```
T+0  Agent attempt 1 推 execution
T+1  Provider 5xx
T+2  Agent 推 error envelope (code=PROVIDER_5XX, retry_after_s=30)
T+3  Runtime 收到 error:
     a) write audit
     b) agents.activity='ERROR' (lifecycle 不变)
     c) 计算 backoff = max(30, 5 * 5^0) = 30s
     d) BullMQ delayed job @ 30s
T+4  Runtime WS 推 agent.activity_changed
T+5  30s 后:
     a) 重新 dispatch（带新 idempotency_key 或同 key）
     b) 新 attempt_no=2
     c) 写 execution_attempts (status=STARTED, attempt_no=2)
     d) active_attempt_no=2
T+6  Agent 推 status=WORKING, attempt_no=2
T+7  Agent 推 result SUCCEEDED / FAILED
T+8  Runtime: CAS 终态
```

### 3.2 attempt_count 累加

```sql
-- 写新 attempt
INSERT INTO execution_attempts (execution_id, attempt_no, status, started_at)
VALUES ($1, $2, 'STARTED', NOW());

-- 更新 execution
UPDATE agent_executions
SET attempt_count = attempt_count + 1,
    active_attempt_no = $2
WHERE id = $1;
```

### 3.3 旧 attempt 状态

每次新 attempt 时：

```sql
UPDATE execution_attempts
SET status = 'FAILED', completed_at = NOW(), error = 'superseded by attempt N+1'
WHERE execution_id = $1 AND attempt_no < $2 AND status IN ('STARTED', 'RUNNING');
```

## 4. 永久失败降级

### 4.1 触发

- MAX_RETRY_EXCEEDED
- PROVIDER_401 / PROVIDER_403 / SANDBOX_INIT_FAILED / DEADLINE_EXCEEDED

### 4.2 行为

```python
async def terminal_fail(execution_id, failure_code):
    # 1. CAS UPDATE
    affected = await db.update("""
        UPDATE agent_executions
        SET status = 'FAILED', completed_at = NOW(),
            failure_code = $1, active_attempt_no = NULL,
            terminal_envelope_id = $2
        WHERE id = $3 AND status IN ('PENDING', 'RUNNING')
    """, failure_code, envelope_id, execution_id)

    if not affected:
        return  # 重复或 stale

    # 2. 写 audit
    await audit_log('execution.failed', execution_id, failure_code)

    # 3. 释放 slot（E4 持有 lease_id → E4 release）
    # Runtime 推 execution.failed 事件 → E4 收到 → release

    # 4. 通知 E4
    await notify_e4_failure(execution_id, failure_code)

    # 5. WS 推给发起人 channel
    collab = await get_collab_by_execution(execution_id)
    if collab and collab.context_refs.channel_id:
        await ws_broadcast(collab.context_refs.channel_id, 'message.created', {
            'system_event': 'execution_failed',
            'execution_id': execution_id,
            'failure_code': failure_code
        })

    # 6. 通知 owner（如果 PROVIDER_401/403 等）
    if failure_code in ('PROVIDER_401', 'PROVIDER_403'):
        await notify_owner(agent.owner_user_id, f'Agent {agent.name} 凭据失效，请更新')
```

### 4.3 retry budget 监控

```
每个 Agent：
  - 1 分钟内 retry 次数 > 10 → activity=ERROR（rate_limit_exceeded_reason）
  - 1 小时 retry 次数 > 100 → 通知 owner 检查 Provider 配置
```

## 5. ERROR activity 状态

### 5.1 activity = ERROR 的具体含义

```python
agents.activity = 'ERROR' 表示：
  - Agent 在线（lifecycle=ACTIVE）
  - 但当前无法正常处理请求
  - 原因：retry exhausted / 401 / 403 / 长时间 Provider 失败
```

**关键**：
- activity=ERROR 时 Agent 仍可以**接收**新 dispatch（V1 简化），但 Resolver 调度时**降权**（不再排第一）
- V2: activity=ERROR 时 Resolver 完全排除

### 5.2 activity 恢复

```
activity=ERROR → 恢复条件:
  - 下次执行成功（SUCCEEDED）→ 立即 → AVAILABLE
  - 下次连接 hello → 立即 → AVAILABLE
  - owner PATCH /agents/:id/activate → 立即 → AVAILABLE
  - 24h 无人处理 → 不自动恢复，需 owner 介入
```

### 5.3 ERROR 信息展示

```
agent.activity_reason TEXT 字段
V1 枚举:
  - 'rate_limit_exceeded'
  - 'auth_failed'
  - 'permission_denied'
  - 'provider_5xx'
  - 'max_retry_exceeded'
  - 'deadline_exceeded'
```

UI 展示：
```
P3 Agent Card:
  状态: ERROR
  原因: 调用失败：API key 无效 [查看日志]
```

## 6. 取消（Cancel）vs 重试（Retry）的区别

| 维度 | Cancel | Retry |
| --- | --- | --- |
| 触发 | 用户/lifecycle/超时主动 | Provider 错误被动 |
| 终态 | CANCELLED | FAILED（retry 耗尽） |
| slot 释放 | 是 | 是 |
| 协作状态 | collab.CANCELLED | collab.UNRESOLVED（重试失败后） |
| 消息流 | SYSTEM 事件 "已取消" | AGENT_OUTPUT 投影（错误信息） |
| 状态机 | 不会重试 | 可以重试 N 次后终态 |

## 7. 与 decision 的交互

### 7.1 Reject 不是 Error

Agent 推 `collaboration.decision: REJECT`：
- 不算 ERROR（agent 主动拒绝）
- activity 不变（仍 AVAILABLE）
- 释放 slot（与 retry 一样）

### 7.2 NeedContext 不算 Error

Agent 推 `collaboration.decision: NEED_CONTEXT`：
- 不算 ERROR
- activity 变 WAITING_CONTEXT
- 释放 slot
- 等人类补齐

## 8. ERROR 触发整体时序

```
T+0  Agent 推 status=WORKING
T+1  Agent 调 LLM
T+2  Provider 返回 5xx
T+3  Agent 推 error envelope
     { code: 'PROVIDER_5XX', message: '...', retry_after_s: 30 }
T+4  Runtime:
     a) 写 audit
     b) agents.activity='ERROR', activity_reason='provider_5xx'
     c) 写 error_envelope_id 落 terminal_envelope_id（防重复）
     d) 计算 backoff=30s
     e) BullMQ enqueue execution.retry @ 30s
     f) WS 推 agent.activity_changed
T+5  30s 后:
     a) BullMQ worker 触发
     b) UPDATE attempt 1 status='FAILED'
     c) INSERT attempt 2
     d) active_attempt_no=2
     e) 重新 dispatch 给 Agent
T+6  Agent 推 status=WORKING (attempt 2)
T+7  Provider 5xx again
T+8  Agent 推 error envelope (id 不同于 T+3)
T+9  Runtime: 继续 backoff (5*5^1=25s)
T+10 25s 后 attempt 3
T+11 Provider 5xx again
T+12 attempt 4? NO, attempt_count=3 >= MAX_RETRY=3 → terminal_fail
T+13 execution.status='FAILED' (failure_code='MAX_RETRY_EXCEEDED')
T+14 通知发起人 + 释放 slot
```

## 9. v0.4.2 关键变更

### 9.1 协议级去重

`error` envelope 也有 `id`，Runtime 用 `terminal_envelope_id` 防重复。

### 9.2 attempt ≠ WS session

`error` 不需要新建 attempt（仅 retry 时建）；retry 是 logical attempt。

### 9.3 ERROR 不重置 active_slots

ERROR 不立即释放 slot（slot 在 execution 终态时才释放）；ERROR 期间 slot 仍占用。

**例外**：MAX_RETRY_EXCEEDED → 立即 FAILED → 释放 slot。

## 10. E2E 验收点

```
e2e/08-error-retry/
  test_001_5xx_retries.json
    Given Provider returns 5xx 3 times
    When Agent reports error
    Then 3 attempts created, backoff applied, final FAILED

  test_002_401_no_retry.json
    When Provider returns 401
    Then immediately FAILED, no retry, owner notified

  test_003_rate_limit_backoff.json
    When Provider returns 429 retry_after_s=60
    Then backoff=60s, retry after 60s

  test_004_max_retry_exceeded.json
    When 3 retries all fail
    Then status=FAILED (MAX_RETRY_EXCEEDED), slot released

  test_005_activity_error.json
    When any error reported
    Then agents.activity='ERROR', activity_reason set, broadcast

  test_006_activity_recovery.json
    Given activity=ERROR
    When Agent reports successful result
    Then activity=AVAILABLE

  test_007_owner_activate_clears_error.json
    Given activity=ERROR
    When PATCH /agents/:id { activity: 'AVAILABLE' } (via /activate)
    Then activity=AVAILABLE, activity_reason=null

  test_008_idempotent_error.json
    When same error envelope.id reported twice
    Then second is no-op (terminal_envelope_id collision)
```

## 11. 与其他设计的关系

- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（完整 lifecycle）
- 详见 [02-interrupt-and-cancel.md](./02-interrupt-and-cancel.md)（cancel vs error）
- 详见 [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)（WS error envelope）
- 详见 [04-resolver-and-routing.md](./04-resolver-and-routing.md)（slot 释放）
