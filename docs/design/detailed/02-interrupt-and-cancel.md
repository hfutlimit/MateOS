# Detailed Design · 02 · Interrupt and Cancel

> **v0.4.3 修正**（P1-3）：Cancel 不再镜像 Execution 状态。Collaboration 与 Execution 是两条独立 lifecycle。
> 前置：[01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)

## 0. 全部打断场景

（同 v0.4.2，11 个场景）

| # | 触发源 | 时机 | 行为 |
| --- | --- | --- | --- |
| 1 | 用户主动 cancel collab（仅 PENDING） | 任意时刻 | 标 collab.CANCELLED + 释放 lease |
| 2 | 用户主动 cancel execution（任意 collab 状态） | 任意时刻 | 标 execution.CANCELLED（**不动 collab**） |
| 3 | lifecycle 变 PAUSED | in-flight | 取消 PENDING collab + 取消 RUNNING execution（独立） |
| 4 | lifecycle 变 DISABLED | in-flight | 同 PAUSED，更激进 |
| 5 | deadline_s 到 | Runtime 计时 | collab.timeout（90s）/ execution.deadline |
| 6 | WS 断开（Agent 端） | 网络瞬断 | 等 resume |
| 7 | WS 断开（长时） | > 5 分钟 | cancel + 通知 owner |
| 8 | Agent 进程崩溃 | OS 杀进程 | 同 WS 断开 |
| 9 | Runtime Gateway 重启 | 部署 | dispatch_acked_at 区分（详见 03） |
| 10 | Provider rate limit | LLM API 429 | activity=ERROR + retry |
| 11 | Provider 401 | 密钥失效 | 立即 FAILED + 通知 owner |

## 1. Cancel 协议（v0.4.3 修正 P1-3）

### 1.1 协议边界

```
collaboration_request.status (事实源，独立 lifecycle):
  PENDING  →  ACCEPTED   terminal decision（用户取消时不改回）
            →  REJECTED
            →  NEED_CONTEXT
            →  UNRESOLVED  (timeout, 无候选)
            →  CANCELLED   (仅 PENDING 状态可标)

execution.status (独立 lifecycle):
  PENDING  →  RUNNING  →  SUCCEEDED
                         →  FAILED
                         →  CANCELLED   (用户取消/lifecycle 变化/超时)
                         →  TIMEOUT
```

**关键修正**：
- ACCEPTED 后用户取消 Execution → collab 仍 ACCEPTED（Agent 接受了）
- execution 变 CANCELLED（用户仅取消执行）
- UI projection：`display_state = "Accepted · execution cancelled"`
- UNRESOLVED 含义：未获得 decision（不含 Execution 失败）

### 1.2 Runtime → Agent

```jsonc
// Cancel execution（v0.4.3 协议清晰）
{ "type": "execution.cancel", "payload": {
    "execution_id", "attempt_no",
    "reason": "USER_CANCEL|LIFECYCLE_PAUSED|LIFECYCLE_DISABLED|DEADLINE_EXCEEDED|TIMEOUT_NO_RESUME"
}}
```

Agent 必须：
1. 立即 abort LLM 调用
2. 清理本地 sandbox
3. 推 `result` envelope `status=CANCELLED`

### 1.3 CAS 处理 cancel + result race

```
Runtime 收到 cancel：
  UPDATE ... SET status='CANCELLED' WHERE id=? AND status IN ('PENDING','RUNNING')
  affected = 0 → 已 terminal → ignore

Runtime 收到 result (status=CANCELLED)：
  UPDATE ... SET status='CANCELLED' WHERE id=? AND status IN ('PENDING','RUNNING')
  affected = 0 → 已是 CANCELLED → ignore
```

## 2. 打断来源详解

### 2.1 用户主动 cancel collab（仅 PENDING）

```
POST /collaboration-requests/:id/cancel
  → E4:
     a) CAS collab.status='PENDING' → 'CANCELLED'
     b) 释放 pending_decision lease
     c) 写 outbox('collaboration.cancelled')
     d) WS 推 collab.cancelled 给发起人
```

### 2.2 用户主动 cancel execution（任意 collab 状态，**v0.4.3 新增**）

```
POST /executions/:id/cancel
  → E7:
     a) CAS execution.status='CANCELLED' WHERE status IN ('PENDING','RUNNING')
     b) 释放 execution lease
     c) 写 outbox('execution.cancelled')
     d) WS 推 execution.cancel 命令给 Agent
     e) 写 audit
  → collab.status 保持不变（不抹除 ACCEPTED）
  → UI 投影：`display_state = "Accepted · execution cancelled"`
```

### 2.3 lifecycle 变化（E2 → E4 跨域）

```
PATCH /agents/:id { lifecycle: 'PAUSED' }
  → E2 UPDATE agents.lifecycle
  → WS 推 agent.lifecycle_changed
  → E4 + E7 收到事件
  → E4: 取消该 agent 所有 PENDING collab
       - CAS collab.status='CANCELLED'
       - 释放 pending_decision lease
  → E7: 取消该 agent 所有 RUNNING execution
       - CAS execution.status='CANCELLED'
       - 释放 execution lease
  → 通知 owner: "已取消 3 个 PENDING + 2 个 RUNNING"
```

**v0.4.3 关键**：
- E4 取消 PENDING collab
- E7 取消 RUNNING execution
- **两条独立路径**，不相互触发
- collab 已经是 ACCEPTED 的：保留（不抹除接受历史），仅 cancel execution

### 2.4 deadline 超时

```
两种超时，独立计时：
  collaboration.timeout (90s)：collab 还没接受决策
    → 标 UNRESOLVED + 释放 pending_decision lease
  execution.deadline：execution 还在跑
    → 标 TIMEOUT + 释放 execution lease
```

### 2.5 WS 断开（短时 → resume）

（同 v0.4.2，详见 03）

### 2.6 WS 断开（长时）

```
WS 断开 > 5 分钟（V1 配置）：
  1. 扫描 in-flight attempts WHERE last_heartbeat < now - 5min
  2. CAS execution.status='CANCELLED' (reason=TIMEOUT_NO_RESUME)
  3. 释放 execution lease
  4. 通知 owner
```

### 2.7 Runtime Gateway 重启（v0.4.3 修正 P1-1）

```
启动时分类处理（详见 03 §4）：
  dispatch_acked_at = null → re-dispatch
  dispatch_acked_at set   → 不动，等 resume
```

### 2.8 Provider rate limit

（同 v0.4.2，详见 08）

### 2.9 Provider 401

（同 v0.4.2）

## 3. 一致性保证

### 3.1 Slot 不泄漏

（同 v0.4.2，v0.4.3 增加 execution lease 维度）

```
所有 cancel/exit 路径必须 release lease:
  - collab CANCELLED → 释放 pending_decision lease
  - execution CANCELLED/TIMEOUT/FAILED → 释放 execution lease
  - collab.timeout（90s 无响应）→ 释放 pending_decision lease
  - lifecycle PAUSED → 批量取消 + 释放
```

### 3.2 不会重复 cancel

（同 v0.4.2，CAS 防重）

### 3.3 Agent 收到 cancel 后必须停止

（同 v0.4.2，Agent 端 SDK 责任）

## 4. 用户视角体验

| 场景 | 用户在 P5 看到什么 |
| --- | --- |
| 协作 PENDING 用户取消 | mention 胶囊消失 + SYSTEM 事件 "已取消" |
| 协作 ACCEPTED 用户取消执行 | DECISION 投影保留（显示 ACCEPTED）+ AGENT_OUTPUT 投影变 "Cancelled" + SYSTEM 事件 "execution cancelled" |
| lifecycle 变 PAUSED 取消全部 | 多个 collab/execution 同时变状态 |
| Agent REJECT | DECISION 投影（REJECT）+ 原因文字 |
| NEED_CONTEXT | DECISION 投影 + 缺失项列表 |
| activity=ERROR | Agent 头像变红 + 工具提示 |
| 长期断线 | Agent 头像变灰，5 分钟后 execution 自动取消 |

## 5. 已知风险

（同 v0.4.2）

| 风险 | 缓解 |
| --- | --- |
| Agent 客户端忽略 cancel | V2 health check |
| Cancel 传播中 LLM 已返回 | 接受浪费（V1）；V2 cost cap |
| Slot 泄漏 | E2E 监控 + DB 重建 |
| 批量 lifecycle cancel 阻塞 | 限速 100/s |
| Cancel 命令丢失（WS 断） | 长时断线超时兜底 |

## 6. E2E 验收点

```
e2e/02-interrupt-cancel/
  test_001_user_cancel_collab_pending.json       # v0.4.3 改
    When user POST /collab/:id/cancel (PENDING)
    Then collab.status=CANCELLED, lease 释放

  test_002_user_cancel_execution_keeps_collab.json  # v0.4.3 新增 P1-3
    Given collab ACCEPTED, execution RUNNING
    When user POST /executions/:id/cancel
    Then execution.status=CANCELLED
    And  collab 仍 ACCEPTED（不抹除）
    And  UI projection "Accepted · execution cancelled"

  test_003_lifecycle_pause_cancel_pending.json
    Given 1 PENDING collab on A
    When A.lifecycle=PAUSED
    Then collab PENDING → CANCELLED, pending_decision lease 释放

  test_004_lifecycle_pause_cancel_running.json
    Given 1 collab ACCEPTED + execution RUNNING on A
    When A.lifecycle=PAUSED
    Then execution → CANCELLED, collab 保持 ACCEPTED
    And  execution lease 释放

  test_005_deadline_collab_timeout.json
    When collab 90s 无响应
    Then UNRESOLVED, pending_decision lease 释放

  test_006_deadline_execution.json
    When execution 超 deadline
    Then TIMEOUT, execution lease 释放

  test_007_reconnect_within_window.json
    Given WS dropped
    When agent reconnects within 5min
    Then execution continues with resume_ack, no status change

  test_008_long_disconnect_cancels.json
    Given WS dropped
    When 5min passes
    Then execution.status=CANCELLED (TIMEOUT_NO_RESUME), lease 释放

  test_009_idempotent_cancel.json
    When cancel called twice rapidly
    Then second is no-op (CAS affected_rows=0)
```

## 7. 与其他设计的关系

- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（完整时序）
- 详见 [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md)（WS resume 细节）
- 详见 [04-resolver-and-routing.md](./04-resolver-and-routing.md)（Slot 释放时机）
- 详见 [08-error-and-retry.md](./08-error-and-retry.md)（ERROR 触发 + retry）
