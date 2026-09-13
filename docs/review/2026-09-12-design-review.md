# Design 评审 · 2026-09-12

> 本文件登记 2026-09-12 跨 design 文档交叉 review 发现的**未在主架构 v0.7/v0.9 收口**的 P1 矛盾。
>
> 评审范围:SYSTEM_DESIGN v0.9 + 10 篇 detailed/ + 11 篇 requirement/ + spec/ + UI DS。
> 评审方法:explore 子代理只读事实收集 + 人工交叉对照。
>
> 关联 anchor:本报告所有 comment blockquote 引用本文件;相关 design 文档顶部均有 blockquote 标注。
> 优先级判据:同 2026-09-11-待拍板项.md(P1=影响实现 / P2=易引起歧义 / P3=纯文字)。

---

## P1-1 · `execution.dispatch_ack` 字段名:01 沿用 v0.5 旧字段,未跟进 v0.7 修订

| 项 | 内容 |
| --- | --- |
| 现状 | `detailed/01-single-agent-task-lifecycle.md:85` 写 `execution.dispatch_ack { execution_id, attempt_no, accepted: true }`(v0.5 字段名);`detailed/03-ws-connection-and-resume.md:111-115` 已 v0.7 改 `received: true` + 可选 `protocol_error?`;`detailed/10-agent-stub-and-sdk.md:27` 与 03 一致;`SYSTEM_DESIGN` v0.7 changelog 明确 "去掉 `accepted=false`..."(行 1080) |
| 影响 | SDK 解析两份不同字段名,推送 / 轮询两条投递通道会出现 "推送能收、轮询不能收" 这类极难排查的隐性 bug |
| 建议 | 改 01:85 字段名为 `received: true` + 可选 `protocol_error?: "UNKNOWN_EXECUTION" \| "STALE_ATTEMPT"`,与 03/10/schema 对齐 |
| 状态 | ✅ **已修 (commit `37a9ea7`)** — `detailed/01:89` `accepted: true` → `received: true` + `protocol_error?`;`detailed/03:298` / `:476` 同步 |

---

## P1-2 · `execution_attempt` 状态机:01 内部读写口径不一致,SD §5.2/§6.3 分离

| 项 | 内容 |
| --- | --- |
| 现状 | `detailed/01:79` INSERT `status='STARTED'`;`detailed/01:99` 收尾 WHERE `status='RUNNING'`;`detailed/01:408` 文档自述 5 态含 STARTED;`SYSTEM_DESIGN §6.3:738` 状态机 `RUNNING → COMPLETED / FAILED` 不含 STARTED 迁移;`SYSTEM_DESIGN §5.2:497` schema CHECK 含 STARTED |
| 影响 | 同一文档内 "写 STARTED / 查 RUNNING" 在 SQL 上能跑通但语义含混;SD 自身 schema 与状态机对不上(同源 §5.2 vs §6.3) |
| 建议 | 三选一:(a) 删 STARTED,统一从 PENDING/RUNNING 起步;(b) 保留 STARTED,补 `STARTED → RUNNING` 迁移,01:99 改 STARTED 找;(c) 在 SD §6.3 状态机加 STARTED 节点 |
| 状态 | **未决**(需单独拍,因为影响 E7 DDL 与 E4 dispatch 路径) |

---

## P1-3 · Permission 键:文档自述 8 键,DDL/TS/MVP 表 7 键

| 项 | 内容 |
| --- | --- |
| 现状 | `SYSTEM_DESIGN §3.1:171` 写 "8 键 + 3 态";`SYSTEM_DESIGN v0.4.5 变更摘要` 确认新增 `propose_memory`;`PRD FR-9:363` 8 键;`E6-authorization-approval.md:23,86,207` 文档自述 8 键;但 `E6:51-53` SQL CHECK、`E6:104` TypeScript 联合类型、`PRD:464` MVP 表都**只列 7 键** |
| 影响 | 实施层 DDL/TS 会拒收 `propose_memory` 写入,数据库反向成为协议最大者,与文档层口径冲突 |
| 建议 | (a) 改 E6:51-53 SQL CHECK 加 `'propose_memory'`;(b) 改 E6:104 TypeScript union 加 `'propose_memory'`;(c) 改 PRD:464 MVP 表 E6 行 "7 键" → "8 键" |
| 状态 | ✅ **已修 (commit `37a9ea7`)** — `E6:62-64` SQL CHECK / `E6:115` TS union / `PRD:464` MVP 表三处加 `propose_memory` |

---

## P1-4 · E5 实施步骤引用 P6 审批中心,与 UI DS v0.7 取消 P6 冲突

| 项 | 内容 |
| --- | --- |
| 现状 | `E5-shared-memory.md:226` E2E 命名 `e2e/E5-006-p6-batch`;`E5:252` "P6 / P7 UI" 实施顺序;`UI DS v0.7:16,177,215` 明确 "取消 P6 审批中心一级页面,统一进 Needs You → Approval" |
| 影响 | E5 实施计划跨阶段与人审路径不一致,实现时会按 E5 字面建一个 P6 一级页面,与 UI DS 入口冻结矛盾 |
| 建议 | (a) 改 E5:226 E2E 命名去掉 p6 段,改为 `e2e/E5-006-needs-you-approval`;(b) 改 E5:252 "P6 / P7 UI" 改 "Needs You → Approval UI";(c) E5 实施步骤表对人审路径全部收口 |
| 状态 | **未决**(已加 comment blockquote 在 E5) |

---

## P1-5 · 03 内部 `dispatch_acked_at` 回填触发条件自相矛盾

| 项 | 内容 |
| --- | --- |
| 现状 | `detailed/03-ws-connection-and-resume.md:296` 注释 "Agent 收到 dispatch 后推 status=WORKING 时回填";`detailed/03:449-461` 实现 `on_agent_dispatch_ack()` 显式由 `execution.dispatch_ack` 触发;`detailed/03:474` 仍用 v0.5 旧字段名 `accepted` |
| 影响 | 同一文档内 schema 注释与 §7 实现层不对齐,SDK 实现时按 296 注释推 WORKING 也会触发回填,与 449-461 二次回填会冲突 |
| 建议 | (a) 改 03:296 注释为 "Agent 收到 dispatch 后推 `execution.dispatch_ack` 时回填";(b) 改 03:474 字段名 `accepted` 为 `received`,与 P1-1 一致 |
| 状态 | ✅ **已修 (commit `37a9ea7`)** — `detailed/03:298` 注释 / `:476` 表格字段名同步 v0.7(同 P1-1) |

---

## P1-6 · `memory_proposals.status` 枚举:detailed 05 加 WITHDRAWN,SD §4.3 不提

| 项 | 内容 |
| --- | --- |
| 现状 | `detailed/05-memory-approval-flow.md:135` CHECK 4 值 `PROPOSED / APPROVED / REJECTED / WITHDRAWN`;`detailed/05:386` 业务用例 "status=WITHDRAWN, message removed";`SYSTEM_DESIGN §4.3:319-321` 只列 3 个迁移(无 WITHDRAWN);`SYSTEM_DESIGN §4.3:325-350` 描述无 WITHDRAWN 触发路径 |
| 影响 | detailed 自行加终态,但 SD 没记录迁移规则;若 SD 是 SSOT,05 多了;若 05 是 SSOT,SD 漏了 |
| 建议 | (a) 接受 WITHDRAWN,在 SD §4.3 补 "创建者主动撤销" 迁移规则;(b) 不接受,删 05:135 与 05:386 WITHDRAWN,业务用例改为 status=REJECTED + reason=USER_WITHDRAW |
| 状态 | **未决**(已加 comment blockquote 在 detailed/05) |

---

## P2 摘要(详见子代理报告;不在本文件展开,等 owner 决断 P1 后排 P2)

- P2-1:outbox `event_type` 命名风格不统一(`collab.timeout` vs `collaboration.accepted`)
- P2-2:`execution.timeout` outbox 事件在 SD / 01 / 04 均无定义(08 单独出现)
- P2-3:WorkItem.type 枚举三处口径不一(E8 DDL 4 值 vs domain-model 2 值 vs PRD 开放)
- P2-4:WorkItem 状态 vs canonical_status_category 不闭合(IN_REVIEW / CLOSED 无对应)
- P2-5:V1 `create_pr` 默认值 07 vs SD §7.1 不一致
- P2-6:`02` 取消 reason 枚举不闭合 + 部分路径不传 reason
- P2-7:`agent_executions` 6 态 vs `execution_attempts` 5 态命名不对称
- P2-8:PRD FR-10 缺"就地操作回源" invariant(SD §11.2 #4 独有)
- P2-9:`01:50` thinking progress 事件流"v0.4.3 改进"无对应协议消息类型

---

## P3 摘要(文档维护层面,可批量收口)

- P3-1:`epic-index.md` 引用旧版本(PRD v0.4 / SD v0.3 / UI DS v0.5)
- P3-2:全部 Epic 文档"上游"字段写 "PRD v0.4",实际 PRD v0.9
- P3-3:UI DS 文件名 v0.2 / 内容 v0.7(已知标注)
- P3-4:P0 同名异义(P0 Inbox vs P0 priority)
- P3-5:UI DS `Section` 用法混淆(Project Settings 子区 vs 文档章节)

---

## 关联到 2026-09-11-待拍板项

- **J6** Memory CR 取消是否发 `memory.updated` 事件 — 暂未触及
- **J7** `dispatch_ack.error_code` 与 `reason` 合并 — 建议**优先拍**,与 P1-1 / P2-2 强相关
- **J8** `agent_executions.result_text` 拆分 — 暂未触及

---

> **minimax m3** · 2026-09-12
> 评审方式:子代理 explore 只读事实收集 + 人工交叉对照
> 评审范围:30 个 design 文档(主架构 2 + detailed 10 + requirement 11 + spec + UI DS)

---

## 修复记录

| Commit | 范围 | 改动 |
| --- | --- | --- |
| `7c2482f` | 评审登记 | 6 篇相关 design 文档加 review comment blockquote + 本 review 报告 anchor |
| `37a9ea7` | P1-1 / P1-3 / P1-5 修复 | dispatch_ack 字段名 3 处同步(01:89, 03:298, 03:476)+ permission 8 键 3 处同步(E6:62-64, E6:115, PRD:464) |

## 剩余未决(待 owner 拍板)

- **P1-2** `execution_attempt` 状态机:三选一(删 STARTED / 保留 + 补迁移 / SD §6.3 加节点)
- **P1-6** `memory_proposals.status` 枚举:二选一(SD §4.3 补 WITHDRAWN / 删 05 WITHDRAWN)
- **P2-1 ~ P2-9** 9 项(详见上文 P2 摘要)
- **P3-1 ~ P3-5** 5 项(版本号/同义异名词典)
