# Detailed Design · 02 · Interrupt and Cancel

> 用户关键问题：**Agent 执行能否被打断？**
> 答案：**能，从多个维度都可以打断**。本文档列全部打断场景、传播路径、一致性保证。
> 前置：[01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)

## 0. 全部打断场景

| # | 触发源 | 时机 | 行为 | 一致性 |
| --- | --- | --- | --- | --- |
| 1 | **用户主动 cancel** | 任意时刻 | Runtime 推 cancel 命令，Agent 收到后停 | CAS 终态 |
| 2 | **lifecycle 变 PAUSED** | in-flight execution | Runtime 推 cancel | 需通知 Agent |
| 3 | **lifecycle 变 DISABLED** | in-flight execution | 同 PAUSED，更激进 | 同时 reject 新 dispatch |
| 4 | **deadline_s 到** | Runtime 计时 | Runtime 推 cancel（reason='DEADLINE_EXCEEDED'） | 不需 Agent 确认 |
| 5 | **WS 断开（Agent 端）** | 网络瞬断 | Runtime 标记 attempt.in_flight=true，等 resume | 不立即 cancel |
| 6 | **WS 断开（长时）** | > N 分钟 | E2: 5 分钟没 resume → Runtime 主动 cancel | 可配置 |
| 7 | **Agent 进程崩溃** | OS 杀进程 | WS 关闭同 #5 | 同 #5 |
| 8 | **Runtime Gateway 重启** | 部署 | 从 DB 读 in-flight executions，重新 dispatch | 状态靠 DB 重建 |
| 9 | **Provider rate limit** | LLM API 返回 429 | Agent 推 error → 转 activity=ERROR | 不 cancel 当前 attempt；可选新建 attempt |
| 10 | **Provider 401** | 密钥失效 | 立即 ERROR + 通知 owner | 不重试，需 owner 修 |
| 11 | **Sandbox 启动失败** | V3+ | ERROR + 通知 | 不重试 |

## 1. Cancel 协议（v0.4.2）

### 1.1 Runtime → Agent

```jsonc
// Runtime 主动 cancel
{ "type": "cancel", "id": "uuid", "ts": ..., "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "reason": "USER_CANCEL|LIFECYCLE_PAUSED|LIFECYCLE_DISABLED|DEADLINE_EXCEEDED|TIMEOUT_NO_RESUME"
}}
```

Agent 必须：
1. 立即停 LLM 调用（abort）
2. 清理本地 sandbox（如有）
3. 推 `result` envelope `status=CANCELLED`（与 #2 race 时谁先谁赢）

### 1.2 Agent 主动 cancel（Agent 决定提前终止）

```jsonc
// Agent 推 result，status=CANCELLED
{ "type": "result", "id": "uuid", "ts": ..., "payload": {
    "execution_id": "...",
    "attempt_no": 1,
    "status": "CANCELLED",
    "output": { "summary": "user requested cancel mid-execution" }
}}
```

Runtime 走 CAS（详见 1.3）。

### 1.3 CAS 处理 cancel + result race

```
Runtime 收到 cancel 路径：
  1. UPDATE agent_executions SET status='CANCELLED' WHERE id=? AND status IN ('PENDING','RUNNING')
  2. affected_rows = 0 → 已 terminal（result 先到）→ 忽略 cancel
  3. affected_rows = 1 → 真 cancel → release slot, audit, broadcast

Runtime 收到 result 路径（status=CANCELLED）：
  1. UPDATE ... SET status='CANCELLED' WHERE id=? AND status IN ('PENDING','RUNNING')
  2. affected_rows = 0 → 已是 CANCELLED（cancel 先到）→ 忽略
  3. affected_rows = 1 → 真 cancel → release slot, audit, broadcast
```

**关键**：两个路径都做同一个 CAS，谁先到谁赢。envelope.id 全局唯一防重复。

## 2. 打断来源详解

### 2.1 用户主动 cancel

**路径**：

```
User 在 P5 Channel 长按某条 AGENT_OUTPUT 消息 → 选「取消执行」
  → P5 客户端 POST /collaboration-requests/:id/cancel
  → E4 REST 端点
  → E4 推 E7 内部 API POST /internal/agent-executions/:id/cancel
  → E7 Runtime Gateway:
     a) CAS UPDATE agent_executions SET status='CANCELLED' ...
     b) WS push cancel 命令给 Agent
     c) 写 audit
     d) release slot（E4 持有 lease → E4 release）
  → E4 → E3 消息流写 SYSTEM 事件 "Jason 取消了协作请求 #42"
  → WS 推给 channel
```

**时序约束**：cancel 命令可能在 result 之前或之后到达。E7 CAS 保证一致性。

### 2.2 lifecycle 变化

```
Owner 改 Agent.lifecycle PAUSED（E2 REST API）：
  1. PATCH /agents/:id { lifecycle: 'PAUSED' }
  2. E2:
     a) UPDATE agents SET lifecycle='PAUSED' WHERE id=?
     b) WS 推 agent.lifecycle_changed
     c) E4 Resolver 自动排除（lifecycle≠ACTIVE）
     d) **E7 检查 in-flight executions for this agent**：
        - 对每个 in-flight:
          - CAS 标 CANCELLED
          - WS 推 cancel 命令
          - 写 audit
  3. 通知 owner: "已取消 3 个进行中的执行"
```

**关键**：lifecycle 变更必须 cancel 所有 in-flight，不能只影响新 dispatch（否则 Agent 收到 cancel 命令会很奇怪："我没在执行啊？"）。

### 2.3 deadline 超时

```
Runtime 侧 BullMQ 延迟任务 @ deadline_s：
  1. 查 agent_executions.status
  2. if status IN ('PENDING','RUNNING') and now > started_at + deadline_s:
     a) CAS 标 TIMEOUT
     b) WS 推 cancel 命令 (reason=DEADLINE_EXCEEDED)
     c) 写 audit
  3. else: 已终态，不处理
```

**vs Execution timeout vs Collaboration timeout**：
- **Collaboration timeout**（E4）：60s PENDING 无决策 → 标 UNRESOLVED → 下一个候选
- **Execution timeout**（E7）：deadline_s 标 TIMEOUT → cancel execution

两者独立。

### 2.4 WS 断开（短时 → resume）

```
Agent 进程：WS 断开
  → Agent 端：重连 hello（带 agent_token）
  → Runtime:
     a) 校验 hello 成功
     b) 查 in-flight executions WHERE agent_id=? AND active_attempt_no IS NOT NULL
     c) 对每个：记 runtime_session_id = new_session_id
     d) **不发新 dispatch**（attempt 还在）
     e) 等 Agent 主动推 execution.resume_request（v0.4.2 反向协议）

Agent 端重连后：
  → 推 execution.resume_request { execution_id, attempt_no }
  → Runtime: resume_ack { last_persisted_seq: 42, snapshot: <重新发 dispatch> }
  → Agent 从 seq=43 续发 event
```

详见 [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)。

### 2.5 WS 断开（长时）

```
Agent WS 断开 > 5 分钟（V1 配置）：
  1. 后台扫描 in-flight executions WHERE last_heartbeat < now - 5min
  2. 对每个：
     a) CAS 标 CANCELLED (reason=TIMEOUT_NO_RESUME)
     b) 不推 cancel 命令（Agent 已离线）
     c) 写 audit
     d) release slot
     e) 通知 owner: "Agent X 5 分钟无响应，Execution #N 已取消"
```

### 2.6 Agent 进程崩溃

进程崩溃 → OS 关 socket → Runtime 端 WS 收到 close frame。
行为同 2.4（短时）→ 2.5（长时）。

### 2.7 Runtime Gateway 重启

```
Runtime 重启：
  1. 启动时从 DB 加载：
     - 所有 in-flight executions (status IN ('PENDING','RUNNING'))
     - 所有 active_slots（按 execution.attempt_count 重新计算）
  2. 对每个 in-flight:
     a) runtime_session_id = null（旧的 session 没了）
     b) 重新 dispatch（带 idempotency_key）
     c) Agent 收到 dispatch 时会校验 execution_id 仍存在 → 继续
     d) 如 agent_token 已过期 → 401 → 通知 owner
  3. 启动时 Redis 状态：
     a) Redis 持久化 RPO（v0.4.2 v1 简化：AOF everysec）
     b) 启动时恢复 semaphore state
     c) 如 Redis 数据丢失 → 重建（从 DB 计算 active_slots）
```

**DB 是事实源，Redis 是 cache**。即使 Redis 重启，DB 里有 ground truth。

### 2.8 Provider rate limit

```
Agent 收到 Provider 429：
  1. 推 error envelope { code: 'RATE_LIMIT', retry_after_s: 60 }
  2. Runtime:
     a) 写 audit
     b) 不立即 cancel execution
     c) 通知 E2 推 status=ERROR (activity 维度)
     d) V1 简化：可选新建 attempt（v0.4.2 retry）
        - 如 attempt_count < MAX_RETRY → 新建 attempt_no=N+1，重新 dispatch
        - 否则 → 标 FAILED
  3. E7 → E4 通知：execution 终态
```

详见 [08-error-and-retry.md](./08-error-and-retry.md)。

### 2.9 Provider 401

```
Agent 收到 401：
  1. 推 error envelope { code: 'PROVIDER_401' }
  2. Runtime:
     a) 立即 cancel current execution (reason=auth_failed)
     b) activity=ERROR
     c) 通知 owner: "Backend Agent 凭据失效，请更新 credential"
     d) 不重试（V1）：凭据失效必须 owner 修
  3. Lifecycle 不变（仍是 ACTIVE），Activity 变 ERROR
```

## 3. 一致性保证

### 3.1 Slot 不泄漏

**所有 cancel/exit 路径必须 release slot**：

```python
# E4 持有 lease_id（来自 tryAcquireSlot）
# 在以下路径必须 release:
#   - Execution SUCCEEDED/FAILED/CANCELLED/TIMEOUT
#   - E4 自身 UNRESOLVED（无 Agent 选中）
#   - lifecycle 变 PAUSED/DISABLED（取消所有 in-flight）
#   - Runtime Gateway 关闭（Redis 持久化恢复）
```

**E2E 验证**：

```
e2e/slot-leak-prevention/
  test_001_no_leak_on_cancel.json
    Given slot=1/1 used
    When cancel execution
    Then slot=0/1 used within 1s
```

### 3.2 不会重复 cancel

```
每个 execution 至多一次 CAS 成功（affected_rows=1）。
后续 cancel / result 全部走 CAS 失败路径 → 忽略。
```

### 3.3 Agent 收到 cancel 后必须停止

**这是用户问的核心**："Agent 执行能否被打断"？**能**——Runtime 推 cancel 命令后，Agent 客户端 SDK 必须：

1. 立即 abort LLM 调用（cancel HTTP request）
2. 清理 sandbox 状态
3. 推 result envelope `status=CANCELLED`
4. 不再推任何新 event

**V1 SDK 责任**（不在 MateOS 侧）：
- 实现 cancel handler
- LLM 客户端必须支持 abort（多数 SDK 支持 context cancellation）
- 资源清理（sandbox / temp file / network connection）

## 4. 用户视角体验

| 场景 | 用户在 P5 看到什么 |
| --- | --- |
| Agent 正常完成 | mention 胶囊 → 决策卡（ACCEPT）→ Agent 输出卡 |
| 用户主动 cancel | 决策卡变灰色 "已取消" 标签 + SYSTEM 事件 |
| Agent 内部决定 REJECT | 决策卡（REJECT）+ 文字 "原因：缺少 API 规格" |
| Agent 状态 ERROR | Agent 头像变红 + 工具提示 "调用失败：API key 无效" |
| 长期断线 | Agent 头像变灰，5 分钟后 execution 自动取消 + SYSTEM 事件 |

## 5. 已知风险

| 风险 | 缓解 |
| --- | --- |
| Agent 客户端忽略 cancel 命令 | V1 不强制；V2 引入 health check（Agent 必须定期推 health 帧） |
| 取消传播中 LLM 已返回 token | 接受浪费（V1）；V2 引入 cost cap |
| Slot 泄漏 | E2E 监控 + 自动回收（DB 是 ground truth） |
| E2 lifecycle 变 PAUSED 时 in-flight 太多 | 批量 cancel + 限速（100/s） |
| Cancel 命令丢失（WS 断） | 长时断线超时兜底（2.5） |

## 6. E2E 验收点

```
e2e/02-interrupt-cancel/
  test_001_user_cancel.json
    When user POST /collab/:id/cancel mid-execution
    Then execution.status=CANCELLED, slot=0/1

  test_002_lifecycle_pause_cancels_inflight.json
    Given agent has 2 in-flight executions
    When PATCH /agents/:id { lifecycle: PAUSED }
    Then both executions become CANCELLED within 1s

  test_003_deadline_timeout.json
    When execution exceeds deadline_s
    Then status=TIMEOUT, slot=0/1

  test_004_reconnect_within_window.json
    Given WS dropped
    When agent reconnects within 5min
    Then execution continues with resume_ack, no status change

  test_005_long_disconnect_cancels.json
    Given WS dropped
    When 5min passes
    Then status=CANCELLED (TIMEOUT_NO_RESUME), slot=0/1

  test_006_provider_401_no_retry.json
    When Provider returns 401
    Then status=FAILED (auth_failed), no retry, owner notified

  test_007_runtime_restart_resumes.json
    Given 3 in-flight executions
    When Runtime restarts
    Then all 3 re-dispatched with same execution_id, no data loss

  test_008_idempotent_cancel.json
    When cancel called twice rapidly
    Then second is no-op (CAS affected_rows=0)
```

## 7. 与其他设计的关系

- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（完整时序）
- 详见 [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)（WS resume 细节）
- 详见 [04-resolver-and-routing.md](./04-resolver-and-routing.md)（Slot 释放时机）
- 详见 [08-error-and-retry.md](./08-error-and-retry.md)（ERROR 触发 + retry）
