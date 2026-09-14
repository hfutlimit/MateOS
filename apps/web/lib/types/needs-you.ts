// Needs You DTO types — Human Attention 投影（不是新事实源）
// 由 CR.NEED_CONTEXT / MemoryProposal.PENDING / Execution 失败 / Agent 异常 /
// WorkItem 阻塞 / 预算阈值汇聚而成（README §106 + SYSTEM_DESIGN §11）。
//
// DS v0.7 §6 把这些投影收成 4 个 UI 分类：Decision / Information / Approval / Problems。
// 每条必带可执行动作（DS v0.7 设计原则 1：Human attention first）。
//
// 后续切真实 API 时，DTO 字段名与后端 NeedsYou 聚合端点对齐；
// M0 阶段手写。

export type NeedsYouCategory = "DECISION" | "INFORMATION" | "APPROVAL" | "PROBLEMS";

export type NeedsYouActionKind =
  | "VIEW_DETAIL" // 跳详情
  | "APPROVE" // 批准（memory proposal 等）
  | "REJECT" // 拒绝
  | "REPLY" // 给 CR 回复上下文
  | "RESOLVE"; // 标记解决（problems）

export interface NeedsYouAction {
  kind: NeedsYouActionKind;
  label: string;
  // 客户端占位路由：M0 切真实 API 时用 entity_ref 拼路径
  href: string;
}

export interface NeedsYouItem {
  id: string;
  category: NeedsYouCategory;
  // 一句话给用户看的标题（DS v0.7 §1：Outcome before system events）
  title: string;
  // 为什么需要你 + 影响什么（DS v0.7 §1：Human attention first）
  reason: string;
  // 来源事实投影：cr / memory_proposal / execution / work_item / agent / budget
  source_kind:
    | "COLLABORATION_REQUEST"
    | "MEMORY_PROPOSAL"
    | "EXECUTION"
    | "WORK_ITEM"
    | "AGENT"
    | "BUDGET";
  source_ref: string;
  // 谁提的（"@Backend Agent" / "PO Alice" / "MateOS"），影响用户对 action 的判断
  raised_by: { actor_type: "HUMAN" | "AGENT" | "SYSTEM"; display_name: string };
  // 紧迫度（DS v0.7 §1 鼓励用人类语言而非技术态）
  urgency: "WHEN_YOU_CAN" | "TODAY" | "BLOCKING";
  // 触发时间戳（ms）
  raised_at_ms: number;
  // 可执行动作（DS v0.7 §6：每条必带）
  actions: NeedsYouAction[];
}
