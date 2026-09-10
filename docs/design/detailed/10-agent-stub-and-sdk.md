# Detailed Design · 10 · Agent Stub 与 SDK

> **状态**：v0.6（2026-09-10）已转正 —— S1 的 Agent 端固定用 stub。
> **配套**：[01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md) / [03-ws-connection-and-resume.md](./03-ws-connection-and-resume.md) / [09-implementation-checklist.md](./09-implementation-checklist.md) §1 §2.1 / SYSTEM_DESIGN v0.5 §5.2 §6.5 / E7 epic
> **前置结论**：「真 Agent 由谁提供（fork 上游 / 用户自部署 / 商业 API）」是**独立开放项**，不因 S1 用 stub 而被决定。

## 0. 为什么用 stub

V1 禁止 `execute_code`（PRD §8 Non Goals），但 S1 必须验证的是 **MateOS 自己的协议与状态机**，不是 LLM 输出质量。若 S1 卡在「Agent 进程从哪来」，等于把产品级开放问题绑进第一个可验收闭环。

因此 S1 的 Agent 端 = **测试替身**：

- **协议是真的**：完整实现 Connector 协议，Runtime 侧零改动即可换真 Agent。
- **产出是假的**：结果内容由 fixture 决定（可配置为固定 markdown / 故意失败 / 故意超时）。

**明确不做**的验收方式：「人类在界面手写产出」或「只推一段 mock 文本」。那条路径绕过 `dispatch → attempt → lease → event 幂等 → resume 位点 → outbox relay`，跑通也等于没验证 Execution 域。

## 1. 协议面（stub 必须实现的最小集合）

| 方向 | 消息 | stub 行为 | 依据 |
| --- | --- | --- | --- |
| Agent → Runtime | `hello` | 携带 `agent_token`，声明 `agent_id` | E7 §4 |
| Agent → Runtime | `heartbeat` | 每 30s 一次；收到 `SIGSTOP` 模拟器可停止 | 03 §7 |
| Runtime → Agent | `collaboration.request` | 按 `stub.mode` 决定 ACCEPT / REJECT / NEED_CONTEXT / 不响应 | 01 §T+7 |
| Agent → Runtime | `collaboration.decision` | 必须带 `reason`；ACCEPT 带 `analysis` 三件套 | 01 §T+11 |
| Runtime → Agent | `execution.dispatch` | —— | 01 §T+15 |
| Agent → Runtime | `execution.dispatch_ack` | **收到即回**（默认 `accepted=true`） | 03 §2 §7.2 |
| Agent → Runtime | `execution.event` | 带 `provider_event_id` + 单调 `seq`，可配置重复/乱序 | 03 §3.2 |
| Agent → Runtime | `execution.result` | 终态 envelope：`execution_id + attempt_no + status` | 01 §T+19 |
| Runtime → Agent | `execution.cancel` | stub 需响应并停止产出（可配置"抗命"分支） | 02 |
| Agent → Runtime | `execution.resume_request` | 断线重连后主动问 | 03 §3 |
| Runtime → Agent | `execution.resume_ack` | stub 从 `last_persisted_seq + 1` 续发 | 03 §3.2 |
| Agent → Runtime | `status` | 纯 activity（`THINKING` / `WORKING` / `AVAILABLE`…），**不承载 ACK** | 03 §2 |

> 命名空间约定：`collaboration.*` 属 E4，`execution.*` 属 E7，严格分离（SYSTEM_DESIGN §6.5）。

## 2. 行为矩阵（S1 必须覆盖的 8 类）

| # | 场景 | stub 配置 | 期望的 MateOS 行为（断言点） |
| --- | --- | --- | --- |
| B1 | 正常闭环 | `mode=ACCEPT_OK` | CR=ACCEPTED → Execution PENDING→RUNNING→SUCCEEDED；attempt 收尾 `COMPLETED`；`dispatch_acked_at` / `last_persisted_seq` 有值；AGENT_OUTPUT 回帖 |
| B2 | 拒收 | `mode=ACCEPT_THEN_REJECT_DISPATCH`（`dispatch_ack{accepted=false, reason=CAPACITY_FULL}`） | **立即** releaseLease + 换下一候选，不等 90s；CR 保持 ACCEPTED；不产生第二次 attempt |
| B3 | 决策拒绝 | `mode=REJECT` | CR=REJECTED，写 `decision_records`，投影决策卡，不创建 Execution |
| B4 | 决策缺上下文 | `mode=NEED_CONTEXT` | CR=NEED_CONTEXT，写 Inbox `NEEDS_HUMAN`，任务挂起等人类补齐 |
| B5 | 决策超时 | `mode=SILENT`（收 request 不响应） | 90s 后 E4 释放 slot → 取下一候选；全部超时 → CR=UNRESOLVED；**不落 TIMEOUT 决策** |
| B6 | 执行中断线重连 | `mode=ACCEPT_OK_DROP_AT_SEQ_5` | attempt **不换**；`resume_request/ack` 后从 seq 6 续发；事件无 gap 无重复（`UNIQUE(attempt_id, provider_event_id)`） |
| B7 | 重复投递 | `mode=DUPLICATE_EVENTS`（同 `provider_event_id` 发两次）+ 重复 `dispatch` | 第二次写入冲突被吞（`ON CONFLICT DO NOTHING`）；重复 dispatch 只重新绑定 WS + 再回 ACK，**不重新执行** |
| B8 | 迟到结果 | `mode=ACCEPT_OK_SLOW`（attempt 1 的结果在 retry 到 attempt 2 之后才到） | `active_attempt_no != attempt_no` → 结果被丢弃；attempt 2 的正常收尾不受影响 |

**S1 断言补充**：

- B1–B8 全部要求 `audit_logs` 有对应记录、`trace_id` 从 Trigger 贯穿到 Execution。
- 任何场景都不允许出现「状态在 DB 里被人工手改」才算通过（DoD，09 §7）。

## 3. stub 的形态与运行方式

```
services/agent-stub/            # 独立进程，不进 apps/api
  Program.cs                    # 出站 WSS 客户端（ASP.NET Core）
  Scenarios.cs                  # 上表 B1–B8 的场景枚举
  Fixtures/                     # 假产出（markdown / diff / 故意超时）
```

- 启动：`dotnet run --project services/agent-stub -- --agent <AGENT_ID> --token <TOKEN> --mode B1`
- 每个 e2e case 起一个独立 stub 进程（或在一个进程内跑多 agent 命名空间），用完即杀；**不写入业务表**（只通过协议交互）。
- 与真 Agent 的差别只体现在「谁造内容」：stub 用 fixture，真 Agent 调 LLM。**Runtime / Resolver / Execution 域代码不得出现任何 `stub` 分支**。

## 4. Agent SDK 契约（真 Agent 复用同一套）

stub 同时是 SDK 的参考实现。SDK 需暴露（语言无关的最小面）：

```
connect(agentToken)                     → hello / heartbeat 由 SDK 托管
on('collaboration.request', handler)    → handler 返回 decision（或抛错=不响应）
on('execution.dispatch',   handler)     → SDK 自动回 execution.dispatch_ack；handler 返回 accepted / reason
                                        → 必须按 (execution_id, attempt_no) 幂等：重复 dispatch 只重绑 WS
sendEvent(execution_id, attempt_no, type, payload)   → SDK 负责 provider_event_id + seq 单调递增
sendResult(execution_id, attempt_no, status, output, usage, artifacts)
on('execution.cancel', handler)
on('resume_ack', handler)               → SDK 负责从 last_persisted_seq+1 续发
sendStatus(activity, reason?)           → 纯 UI 语义
```

**幂等是客户端责任**：Runtime 重启后可能重派同一 `(execution_id, attempt_no)`；SDK 若重复执行，等于重复计费（03 §7.1）。

## 5. 与 Runtime 的边界（S1 冻结）

| 事项 | 归属 | 说明 |
| --- | --- | --- |
| execution_id 生成 | **E7** | Agent 永不自报；E4 直调创建后回写 CR（D9） |
| attempt 计数 / 当前 attempt | **E7** | Agent 只回带 `attempt_no`，不做判定 |
| ACK 回填 | **E7** | 由 `execution.dispatch_ack` 触发（03 §7） |
| 事件去重 | **E7**（DB 唯一约束） | 但客户端必须自己也不重复发（避免噪声） |
| 续传位点 | **E7 提供、Agent 消费** | `last_persisted_seq` 是连续位点（非 `MAX(seq)`） |
| 产出内容 | **Agent** | S1 阶段 = fixture；M8 之后才谈质量与成本 |

## 6. 非目标（S1 不做）

- 不做多 Agent 协作 / 委派（`DELEGATE` 仅保留枚举位）。
- 不做 Mission / WorkUnit / Scheduler（在 `docs/design/future/`）。
- 不做 `execute_code` / 沙箱（V3）。
- 不做 Prompt 工程与评审质量：S1 只证明「闭环跑通且协议正确」。

## 7. 验收（挂入 09 §7 的 S1 DoD）

1. B1–B8 八类场景全绿（e2e ≤ 15 条主链路的一部分）。
2. 换「真 Agent 适配器」（只需实现 §4 的 SDK 面）后，Runtime 侧**零改动**即可替换 stub——用一个假适配器验证一次即可。
3. B6 + B8 必须可重复跑 20 次无 flake（涉及时间与重连，最容易 flaky）。
