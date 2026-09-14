import type {
  WorkItemProviderInfo,
  WorkItemSummary,
} from "@/lib/types/work";

// API client stub — Phase M0 永远返回 mock。
//
// 后续切真实 API 时把 fetchXxx 接入：
//   - GET  /projects/{id}/work-items?status=&type=&assignee_type=&assignee_id=&limit=
//   - GET  /work-management/providers
//   - GET  /work-items/{id}
//   - POST /work-items/{id}/transition    body: { status }
//   - POST /work-items/{id}/assign        body: { agent_id, deadline_s? }
//   - GET  /work-items/{id}/executions
// snake_case 全局策略由后端 ConfigureHttpJsonOptions 决定，DTO 字段名与
// src/MateOS.Api/Work/WorkItemEndpoints.cs 严格对齐。

import { MOCK_PROVIDERS, MOCK_WORK_ITEMS } from "@/lib/mock/work";

export interface ListWorkItemsParams {
  status?: string;
  type?: string;
  assignee_type?: "HUMAN" | "AGENT";
  assignee_id?: string;
  limit?: number;
}

export async function listWorkItems(
  _projectId: string,
  params: ListWorkItemsParams = {},
): Promise<WorkItemSummary[]> {
  // Phase M0: 同步返回 mock；过滤在前端做（量级 ≤ 100，DS §5.5）。
  // 切真实 API 后这里改成 fetch + await。
  const limit = params.limit ?? 50;
  let items = [...MOCK_WORK_ITEMS];
  if (params.status) {
    items = items.filter((w) => w.status === params.status);
  }
  if (params.type) {
    items = items.filter((w) => w.type === params.type);
  }
  if (params.assignee_type) {
    items = items.filter(
      (w) => w.assignee_type === params.assignee_type,
    );
  }
  if (params.assignee_id) {
    items = items.filter((w) => w.assignee_id === params.assignee_id);
  }
  return items.slice(0, limit);
}

export async function getWorkItem(_id: string): Promise<WorkItemSummary | null> {
  // Phase M0: 同步返回 mock 中匹配 id 的一条；切真实 API 后改 fetch。
  return MOCK_WORK_ITEMS.find((w) => w.id === _id) ?? null;
}

export async function listProviders(): Promise<WorkItemProviderInfo[]> {
  return MOCK_PROVIDERS;
}
