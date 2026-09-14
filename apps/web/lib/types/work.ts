// Work domain DTO types — 与 src/MateOS.Api/Work/WorkItemEndpoints.cs 对齐
// snake_case 全局策略由后端 ConfigureHttpJsonOptions 决定，前端不另做转换。
//
// ⚠️ 在 contracts/openapi/ 落地前，本文件是手写 DTO；OpenAPI 落地后由
//    codegen 覆盖（README §"技术选型 · 契约" 明确 TS 由 OpenAPI 生成）。

export type WorkItemType = "TASK" | "STORY" | "BUG" | "EPIC";

export type WorkItemStatus =
  | "OPEN"
  | "IN_PROGRESS"
  | "IN_REVIEW"
  | "DONE"
  | "BLOCKED"
  | "CANCELLED";

export type WorkAssigneeType = "HUMAN" | "AGENT";

export type CanonicalStatusCategory =
  | "OPEN"
  | "IN_PROGRESS"
  | "IN_REVIEW"
  | "DONE"
  | "CLOSED";

export interface WorkItemSummary {
  id: string;
  project_id: string;
  type: WorkItemType;
  title: string;
  description: string | null;
  status: WorkItemStatus;
  canonical_status_category: CanonicalStatusCategory;
  assignee_type: WorkAssigneeType | null;
  assignee_id: string | null;
  due_at_ms: number | null;
  created_by_type: string;
  created_by_id: string;
  binding_id: string;
  provider_key: string;
  external_ref: string | null;
  external_url: string | null;
  provider_status: string | null;
  provider_updated_at_ms: number | null;
  created_at_ms: number;
  updated_at_ms: number;
  allowed_transitions: WorkItemStatus[];
}

export interface WorkItemBindingSummary {
  id: string;
  project_id: string;
  provider_key: string;
  connection_id: string | null;
  external_project_ref: string | null;
  is_active: boolean;
  created_at_ms: number;
  updated_at_ms: number;
}

export interface WorkItemProviderInfo {
  key: string;
  display_name: string;
  description: string;
  builtin: boolean;
}
