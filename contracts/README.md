# contracts/ — 协议事实源（SSOT）

> 本目录是**跨语言协议边界**的事实源。`docs/design/detailed/09` §4 与 `README.md`「技术选型 · 契约」都指向这里。
> 定位：**协议层技术中立**——只定义语义与 wire 形状，不绑定实现语言（SYSTEM_DESIGN §1 裁决）。

## 1. 为什么需要它

在它存在之前，同一份协议有三处独立表述，且**已经真实分叉过**：

| 表述 | 位置 | 会不会漂移 |
| --- | --- | --- |
| 设计文档（散文） | `SYSTEM_DESIGN` §6.5 / `detailed/03` §2 | 会——两处已经互相矛盾（见 §4） |
| C# 契约记录 | `src/MateOS.Contracts/Protocol/` | 不会编译错，但字段名/形状可以悄悄改 |
| 服务端实际发出的 JSON | `AgentDispatchNotifier` / `ExecutionEndpoints` | 匿名对象拼错字段名**没有任何编译期提示** |

`contracts/schemas/` 是第三份表述——**独立于实现的**校验依据。`ConnectorContractTests` 把「C# 契约记录序列化后的 JSON」喂给这些 schema 校验，于是三方分叉会立即变成测试失败，而不是等客户端集成才发现。

一次真实收益（本目录落地时抓到）：`ExecutionDispatchAckPayload.IsProtocolError` 是给服务端判别用的派生属性，不标 `[JsonIgnore]` 就会多序列化出 `is_protocol_error` 字段；schema 是 `additionalProperties: false`，校验当场失败。

## 2. 目录结构

```
contracts/
├── README.md
└── schemas/                      # JSON Schema 2020-12
    ├── connector/envelope.json   # 通用外层信封（id / type / ts / payload）
    ├── collaboration/decision.json
    └── execution/
        ├── dispatch.json         # Runtime → Agent 派发
        ├── dispatch-ack.json     # Agent → Runtime 送达收据
        └── resume-ack.json       # Runtime → Agent 续传位点
```

约定：

- 每个文件**自包含**（不用跨文件 `$ref`）。跨文件解析依赖 schema 注册顺序，会让校验结果受加载方式影响；两份重复定义的一致性由测试对**同一组 C# 记录**做双向校验来保证。
- 每个文件必须有 `$id`（`https://mateos.local/schemas/<domain>/<message>.json`）与 `$schema`。
- `additionalProperties: false`：多出字段即违约，不允许「顺手加个字段」。
- 可空字段**必须显式出现为 `null`**，不用「字段缺失」表达「没有值」——否则客户端无法区分「不支持该字段」与「本次为空」。因此序列化时**不要**开 `DefaultIgnoreCondition.WhenWritingNull`。

## 3. 校验方式

```bash
dotnet test tests/MateOS.UnitTests/MateOS.UnitTests.csproj --filter "FullyQualifiedName~ConnectorContractTests"
```

测试直接读**仓库里的** `contracts/schemas/**`（不复制到输出目录）：契约校验的价值就在于「测试读的那份 = 提交进仓库的那份」，复制会制造第二个可能过期的副本。

覆盖的关键断言：
- 所有 schema 合法且声明 draft 2020-12
- envelope 接受全部契约类型、拒绝未知类型、`id` 同时接受 D 与 N 两种 UUID 写法
- `execution.dispatch` 完整形状与「可空字段全为 null」两种情形都合法
- **WS 推送与 HTTP 轮询的 payload 逐字段一致**（同一份事实，两条通道，SDK 只应有一份解析代码）
- 映射层（`jsonb` → 契约形状）不丢信息

## 4. 文档冲突的裁决（已在本目录固化）

`SYSTEM_DESIGN` §6.5 与 `detailed/03` §2 对同一份 Connector 协议有**真实矛盾**。按 README「冲突时以 PRD + SYSTEM_DESIGN 为准并回改子文档」的规则本应取 §6.5，但 §6.5 **自相矛盾**（同一份类型清单里 `dispatch`/`event`/`result` 无前缀，`execution.resume_request` 却有前缀），因此按下表逐项裁决：

| 项 | `SYSTEM_DESIGN` §6.5 | `detailed/03` §2 | 取值 | 依据 |
| --- | --- | --- | --- | --- |
| 消息命名 | `dispatch` / `event` / `result`（混用） | 全部带 `execution.*` / `collaboration.*` | **带命名空间前缀** | §6.5 自己的设计原则写着「`collaboration.*` 与 `execution.*` 严格分离」，其类型清单违反自己的原则；且 `EnvelopeTypes` 已按前缀落地 |
| envelope 版本字段 `v` | 有 `"v": 1` | 无 | **无**（记入待拍板） | 实现按 03；给所有帧（含 E3 channel envelope）加 `v` 属协议版本升级，需单独拍板 |
| `attempt_no` | dispatch payload 未列 | payload 未列，但 §7.2 实现带 | **必带** | `detailed/10` §4 要求 SDK 按 `(execution_id, attempt_no)` 去重，缺它无法幂等 |
| `work_item_ref` | 对象 `{provider_key, work_item_id}` | 未定义形状 | **对象** | Agent 必须知道走哪个 Provider；裸 id 无法决定回写路径 |
| `deadline_s` | 相对秒 | 相对秒 | **相对秒** | Agent 与 Runtime 时钟未必同步，绝对时间戳会让快时钟的 Agent 提前放弃 |
| `input` | `{prompt, params}` | `{prompt, params}` | **`{prompt, params}`** | 两处一致；DB 里存的是自由形态快照，由 `ExecutionPayloadMapper` 归一 |
| `context` | `{memory_refs, recent_messages, permissions}` | `context` | **上述三项 + `refs` 透传位** | 注入三件套 S1 未实现；只留它们会把 channel/work_item/project 引用丢掉 |

已同步的实现侧改动：`ExecutionContext` 增加 `refs`、`WorkItemRef.ExternalRef` 改为可空、HTTP inbox 与 WS 推送共用 `ExecutionDispatchPayload`（原先 inbox 用 `context_refs`/裸 `work_item_ref`/`deadline_at_ms`，与推送形状不同）。

### 4.1 仍需回改文档的项（不在本目录范围内）

- `SYSTEM_DESIGN` §6.5：类型清单补命名空间前缀；补 `attempt_no`；补 `work_item_ref` 形状。
- `SYSTEM_DESIGN` §2 仓库结构：写的是 `packages/contracts/` 与 `services/orchestrator|memory|work-management`，与「模块化单体」裁决及 `detailed/09` §4 的根级 `contracts/` 冲突——§2 是旧 monorepo 残留。
- `SYSTEM_DESIGN` §8：写 `Client ↔ API = REST /api/v1`，而实现**没有** `/api/v1` 前缀（端点是 `/auth/*`、`/channels/*` …）。要么加前缀，要么改文档——属产品决定。
- `detailed/03` §2：`execution.dispatch` payload 补 `attempt_no`。

## 5. 待补（尚未落地，此处显式记录而不是假装覆盖）

- `schemas/memory/`、`schemas/work-item/`、`schemas/permission/`、`schemas/collaboration/request.json` 等：`detailed/09` §4 列出了这些目录。当前只固化**已实现且被 SDK 直接消费**的 Connector 消息；其余消息的 wire 形状还没有实现侧消费者，先写 schema 会变成又一份会漂移的表述。
- `openapi/`：REST 契约尚未落地。当前 REST 只有 `ConfigureHttpJsonOptions` 的全局 snake_case 策略 + 各端点 DTO，**没有**可执行的契约。
  下一步优先级：先决定「运行时生成（`AddOpenApi`，与实现零漂移）」还是「手写 OpenAPI」（可评审但会漂移），再补 OpenAPI ↔ DTO 的一致性校验。**在决定之前不要手写一份大而全的 OpenAPI**：那是一份一定会过期的第三表述。
- TS 类型生成：等 `openapi/` 落地后再做（`README.md` 说的是「TS / C# 由契约生成或校验」）。
- `$defs` 共享化：目前 `execution/dispatch.json` 与 `execution/resume-ack.json` 各有一份 `input`/`context` 定义（见 §2 约定）。若 schema 数量增长，改为统一 registry + 跨文件 `$ref`，并保留现有测试作为防漂移网。
