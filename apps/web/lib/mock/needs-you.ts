import type { NeedsYouItem } from "@/lib/types/needs-you";

// Mock 4 条样例 — 一条覆盖 DS v0.7 §6 的 4 个分类。
// 后续切真实 API 时由 GET /needs-you 聚合端点替换。

const now = Date.now();
const HOUR = 60 * 60 * 1000;
const MIN = 60 * 1000;

export const MOCK_NEEDS_YOU: NeedsYouItem[] = [
  {
    id: "ny-001",
    category: "DECISION",
    title: "Backend Agent 想把 channel 模块拆成读写两个文件",
    reason:
      "Agent 跑完 lint 后判定现有 channel.ts 超过 600 行，建议拆为 channel-write.ts / channel-read.ts。需要你确认是否同意这个拆法。",
    source_kind: "COLLABORATION_REQUEST",
    source_ref: "cr-11111111",
    raised_by: { actor_type: "AGENT", display_name: "Backend Agent" },
    urgency: "TODAY",
    raised_at_ms: now - 25 * MIN,
    actions: [
      { kind: "APPROVE", label: "同意拆", href: "#approve-cr-11111111" },
      { kind: "REJECT", label: "拒绝", href: "#reject-cr-11111111" },
      { kind: "VIEW_DETAIL", label: "看 diff", href: "#detail-cr-11111111" },
    ],
  },
  {
    id: "ny-002",
    category: "INFORMATION",
    title: "Memory 申请：把『S3 主链路已收口』写入项目记忆",
    reason:
      "Backend Agent 在跑完 M3 commit 后自动申请这条记忆；批准后下次 dispatch 会自动注入。Source = execution outcome（可追溯）。",
    source_kind: "MEMORY_PROPOSAL",
    source_ref: "mp-22222222",
    raised_by: { actor_type: "AGENT", display_name: "Backend Agent" },
    urgency: "WHEN_YOU_CAN",
    raised_at_ms: now - 2 * HOUR,
    actions: [
      { kind: "APPROVE", label: "批准", href: "#approve-mp-22222222" },
      { kind: "REJECT", label: "拒绝", href: "#reject-mp-22222222" },
      { kind: "VIEW_DETAIL", label: "看 Source", href: "#detail-mp-22222222" },
    ],
  },
  {
    id: "ny-003",
    category: "APPROVAL",
    title: "EPIC 收口 S3 主链路 派给 Backend Agent（24h deadline）",
    reason:
      "PO Alice 在 Work 页面新建 EPIC 并选择自动派给 Backend Agent。派单后默认 24h deadline，可调整；批准后触发 E7 dispatch。",
    source_kind: "WORK_ITEM",
    source_ref: "11111111-1111-1111-1111-111111111111",
    raised_by: { actor_type: "HUMAN", display_name: "PO Alice" },
    urgency: "BLOCKING",
    raised_at_ms: now - 8 * MIN,
    actions: [
      { kind: "APPROVE", label: "确认派单", href: "#approve-wi-1111" },
      { kind: "REJECT", label: "改派他人", href: "#reject-wi-1111" },
    ],
  },
  {
    id: "ny-004",
    category: "PROBLEMS",
    title: "TASK 补 contracts/schemas/memory/ 三件套 阻塞 > 24h",
    reason:
      "WorkItem 当前无下游响应；Agent 也没在跑。可能原因：assignee 未指派 / Agent 离线 / 等待外部依赖。",
    source_kind: "WORK_ITEM",
    source_ref: "44444444-4444-4444-4444-444444444444",
    raised_by: { actor_type: "SYSTEM", display_name: "MateOS" },
    urgency: "BLOCKING",
    raised_at_ms: now - 26 * HOUR,
    actions: [
      { kind: "REPLY", label: "指派 Agent", href: "#assign-wi-4444" },
      { kind: "VIEW_DETAIL", label: "看 WorkItem", href: "#detail-wi-4444" },
      { kind: "RESOLVE", label: "标记解决", href: "#resolve-wi-4444" },
    ],
  },
];
