import { MOCK_NEEDS_YOU } from "@/lib/mock/needs-you";
import type { NeedsYouCategory, NeedsYouItem } from "@/lib/types/needs-you";

// API client stub — Phase M0 同步返回 mock。
// 后续切真实 API 时：
//   - GET  /needs-you?project_id=&category=&limit= （聚合 4 类投影，SYSTEM_DESIGN §11）
// 切真实后把过滤从客户端挪到服务端；M0 阶段 4 条样例量级，前端过滤零成本。

export interface ListNeedsYouParams {
  project_id?: string;
  category?: NeedsYouCategory;
  limit?: number;
}

export async function listNeedsYou(
  _userId: string,
  params: ListNeedsYouParams = {},
): Promise<NeedsYouItem[]> {
  const limit = params.limit ?? 50;
  let items = [...MOCK_NEEDS_YOU];
  if (params.project_id) {
    // M0 mock 不带 project 维度，保留钩子
  }
  if (params.category) {
    items = items.filter((i) => i.category === params.category);
  }
  // 按 raised_at 倒序（最新在前）
  items.sort((a, b) => b.raised_at_ms - a.raised_at_ms);
  return items.slice(0, limit);
}
