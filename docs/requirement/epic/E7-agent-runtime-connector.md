# E7 · Agent Runtime & Connector Protocol

| 字段 | 值 |
| --- | --- |
| Epic ID | E7 |
| 标题 | Agent Runtime & Connector Protocol |
| 阶段 | MVP（M3-M4） |
| 上游 | PRD v0.3 §4.3 / §5 FR-3 / SYSTEM_DESIGN v0.2 §6 / §8 协议汇总 |
| 下游 | E2（6 态上报）、E4（dispatch 任务）、E8（runtime 监控）、E9（collaboration 协议同构） |
| 状态 | Draft |

## 1. 背景与动机

MateOS 自研 Agent 运行时（SYSTEM_DESIGN §1.1 自洽前提）。Runtime 是 Agent 与 MateOS API 之间的桥梁——Agent 主动连入，本地/云端统一接入。本 epic：

- Connector 协议（v1 JSON envelope over WSS）
- 出站连接 + JWT agent token
- 6 态上报、dispatch 任务下发、流式回传
- 心跳保活（90s 过期）
- ERROR 触发与恢复

## 2. 范围

### 2.1 In Scope

- WSS Connector 协议（v1）
- Agent 出站连接（NAT 穿透）
- JWT agent token 签发 + 校验
- 6 态 status envelope
- dispatch 任务下发（携带 memory_refs + recent_messages + permissions）
- 流式回传（progress / result / error）
- 心跳 90s 保活 + 过期扫描
- 沙箱预占接口（V3 启用，V1 仅协议层）

### 2.2 Out of Scope

- Sandbox 容器实际执行（V3+，架构预留）
- 第三方 Agent 接入 SDK（V1 仅协议，V2 出官方 SDK）
- Multi-tenant 隔离运行时（V1 单租户）

## 3. 数据模型

无新表。Runtime Gateway 主要是网络层 + Redis 状态层。

```sql
-- presence:{agent_id}（Redis Hash）
--   field=status, since, node, last_heartbeat, error_reason
--   TTL 90s（心跳续期）

-- agent_tokens（与 agents 一对多，允许轮换）
CREATE TABLE agent_tokens (
  id          UUID PRIMARY KEY,
  agent_id    UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  token_hash  TEXT NOT NULL,             -- 哈希存储，明文只下发一次
  label       TEXT,                      -- 'laptop-1' / 'cloud-worker-1'
  last_seen_at TIMESTAMPTZ,
  expires_at  TIMESTAMPTZ,
  revoked     BOOLEAN NOT NULL DEFAULT false,
  created_at  TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_agent_tokens_agent ON agent_tokens(agent_id);

-- llm_calls（E2 用量统计来源）
CREATE TABLE llm_calls (
  id            UUID PRIMARY KEY,
  agent_id      UUID NOT NULL REFERENCES agents(id),
  call_id       UUID,                     -- LLM provider 侧
  model         TEXT NOT NULL,
  tokens_in     INT NOT NULL,
  tokens_out    INT NOT NULL,
  cost_usd      NUMERIC(10,4),
  duration_ms   INT,
  created_at    TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_llm_calls_agent_time ON llm_calls(agent_id, created_at DESC);
```

## 4. 协议

### 4.1 通用 Envelope

```jsonc
// 所有消息通用
{ "v": 1, "type": "<message_type>", "id": "uuid", "ts": 1736380800000, "payload": { } }
```

### 4.2 消息类型

| 方向 | type | 用途 |
| --- | --- | --- |
| Agent → Runtime | `hello` | 首次连接，携带 agent_id + agent_token |
| 双向 | `heartbeat` | 30s 一次保活 |
| Agent → Runtime | `status` | 上报 6 态变更 |
| Runtime → Agent | `dispatch` | 任务下发 |
| Agent → Runtime | `progress` | 流式进度（可选） |
| Agent → Runtime | `result` | 任务完成（含 decision） |
| Agent → Runtime | `error` | 调用失败 / 限额 |

### 4.3 Hello

```jsonc
// Agent → Runtime
{ "type": "hello", "payload": {
    "agent_id": "...",
    "agent_token": "jwt...",        // 短期 token（1h）
    "runtime_version": "1.0.0",
    "capabilities": ["coding", "review"]
}}

// Runtime → Agent
{ "type": "hello_ack", "payload": {
    "session_id": "...",
    "server_version": "1.0.0",
    "config": { "heartbeat_interval_s": 30, "max_idle_s": 90 }
}}
```

### 4.4 Status（6 态）

```jsonc
// Agent → Runtime
{ "type": "status", "payload": {
    "status": "OFFLINE|AVAILABLE|THINKING|WORKING|WAITING_CONTEXT|ERROR",
    "reason": "rate_limit_exceeded",   // ERROR 时携带
    "since": 1736380800                // epoch seconds
}}
```

Runtime 收到后：
1. 写 Redis `presence:{agent_id}` (status, since, last_heartbeat)
2. 写 DB `agents.status` 缓存
3. WS 推 `agent.status_changed` 给相关 client

### 4.5 Dispatch

```jsonc
// Runtime → Agent
{ "type": "dispatch", "payload": {
    "task_id": "...",
    "channel_id": "...",
    "context": {
      "memory_refs": ["mem:..."],         // 引用 ID 列表
      "recent_messages": ["msg:..."],     // 上下文窗口
      "permissions": { "can_execute": false, "can_review": false }
    },
    "deadline_s": 600,
    "idempotency_key": "..."
}}
```

Agent 必须用 `idempotency_key` 防重（重连后 Runtime 可能重发）。

### 4.6 Result（含 Decision）

```jsonc
// Agent → Runtime
{ "type": "result", "payload": {
    "task_id": "...",
    "decision": "ACCEPT|REJECT|NEED_CONTEXT|DELEGATE",
    "reason": "...",
    "needs": ["api spec", "db design"],
    "analysis": { "capability": true, "context_score": 88, "permission": true },
    "delegate_to": "agent:...",
    "output": {                         // WORKING 完成后
      "markdown": "...",
      "code": "...",
      "tokens_in": 0, "tokens_out": 0,
      "latency_ms": 0
    }
}}
```

### 4.7 Error

```jsonc
// Agent → Runtime
{ "type": "error", "payload": {
    "task_id": "...",
    "code": "PROVIDER_5XX|PROVIDER_401|RATE_LIMIT|SANDBOX_INIT_FAILED",
    "message": "...",
    "retry_after_s": 60                 // 可选
}}
```

## 5. 关键流程

### 5.1 Agent 上线

```
1. Agent 进程启动
2. 出站 WSS 连接到 wss://api.mateos/runtime
3. 发送 hello
4. Runtime 校验 agent_token → Redis presence: { status: AVAILABLE, since: now }
5. WS 广播 agent.status_changed
6. 30s 一次 heartbeat
```

### 5.2 Dispatch → Decision 全链路

```
1. 用户消息触发 E4 Mention Resolver → 命中 agent X
2. Runtime 收到 decision 落库需求，向 agent X 发 dispatch
3. agent X 推 status=THINKING（心跳前已 AVAILABLE，dispatch 触发状态变更）
4. agent X 调用 LLM（V1：同步；V2：流式 SSE）
5. agent X 推 status=WORKING
6. agent X 发 result { decision, analysis, output }
7. Runtime：
   a) 写 decision_records（E4）
   b) 如果 ACCEPT：触发 E5 任务占位（V2 真实执行）
   c) WS 推 decision.proposed + message.created（DECISION 形态，E3）
   d) 更新 agent status → AVAILABLE
```

### 5.3 心跳过期扫描

```
Background worker 每 30s 扫所有 presence:*：
  last_heartbeat < now - 90s:
    a) Redis DEL presence:{id}
    b) DB UPDATE agents SET status='OFFLINE'
    c) WS 广播 agent.status_changed
```

### 5.4 ERROR 触发

```
Agent 端检测（自主上报 error envelope）：
- Provider 5xx 连续 3 次 → error code=PROVIDER_5XX
- 401 → error code=PROVIDER_401
- 日限额 ≥ 100% → error code=RATE_LIMIT

Runtime 处理：
1. 写 DB agents.status=ERROR, status_reason=code
2. 写 Redis presence status=ERROR
3. WS 广播
4. 不再向该 agent 派发新任务
```

### 5.5 Agent Token 签发

```
1. Owner 在 P3 Agent Card 点 "生成新 Token"
2. 后端生成随机 256-bit secret → 哈希存储 agent_tokens.token_hash
3. 明文 token 仅返回一次（弹窗展示，要求复制保存）
4. token 短期（V1 30d，可手动 revoke）
5. 设备绑定（V2）：token 绑 IP/设备指纹
```

## 6. UI

### 6.1 页面

- P3 Agent Card（已有 v0.4 原型）：状态点 / 6 态切换 / Token 管理入口

### 6.2 状态

- 状态变更：顶栏通知「Backend Agent 进入 ERROR 状态：rate_limit_exceeded」+ fix_hint
- 顶栏按 6 态聚合计数（WAITING 优先）

## 7. 验收标准

### 7.1 功能

- **F1** Agent 出站连接 hello 校验成功 → status=AVAILABLE + WS 广播
- **F2** 30s 心跳保活，90s 过期扫描自动转 OFFLINE
- **F3** dispatch 携带 memory_refs 至少 1 条（V1 简化：缺则 dispatch 仍可发，但 message 提示 "无记忆上下文"）
- **F4** Agent 上报 result.decision 落 decision_records（E4 验证）
- **F5** ERROR 触发：连续 3 次 5xx 立即 ERROR + 移除 Resolver 候选
- **F6** Token 泄露后 owner 可 revoke → 当前连接断开
- **F7** 流式 progress V1 可选；V2 必选
- **F8** idempotency_key 重发相同 dispatch 只产生 1 个 result

### 7.2 E2E

- `e2e/E7-001-connect-hello`：Agent hello → status=AVAILABLE 广播
- `e2e/E7-002-dispatch-result`：dispatch → THINKING → WORKING → result
- `e2e/E7-003-error-trigger`：模拟 3 次 5xx → ERROR
- `e2e/E7-004-heartbeat-expire`：90s 无心跳 → 自动 OFFLINE
- `e2e/E7-005-token-revoke`：revoke 当前 token → 连接断开
- `e2e/E7-006-idempotency`：重发相同 dispatch → 1 个 result

### 7.3 非功能

- 单 Runtime 实例支持 1000 并发 agent 连接
- 状态广播 P99 < 200ms
- 1000 agent × 30s 心跳 = 33/s 入 Redis，无瓶颈

## 8. 与其他 Epic 的关系

- **被依赖**：E2（status 缓存）、E4（dispatch 是 decision 触发源）、E8（runtime metrics）
- **依赖**：E2（agent 必须存在）
- **冲突裁决**：协议 v1 envelope 与 SD §6.2 一致；ERROR 触发与 SD §6.1 一致

## 9. 风险与开放问题

- **R1**：Agent token 长期/短期策略（SD §13 开放）— V1 30d，V2 设备绑定
- **R2**：WSS 压缩（permessage-deflate）— SD §13 开放，V1 不启用，V2 评估
- **R3**：Agent 重连后 resume — V1 简化：重连后由 Orchestrator 重发 in-flight dispatch
- **R4**：跨可用区 Runtime 多实例 → 统一通过 Pub/Sub 转发（Redis presence 是真源）

## 10. 实施顺序（M3-M4）

1. agent_tokens 表 + Token 签发 REST
2. WSS 网关（NestJS Gateway）+ hello 校验
3. status / heartbeat / dispatch / result / error envelope
4. presence Redis + 心跳过期扫描
5. 状态广播 WS 推 client
6. E2 Agent status 缓存同步
7. E4 Decision 落库联动
8. E2E 套件
