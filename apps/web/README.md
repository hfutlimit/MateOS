# apps/web · MateOS Web Client

> Next.js 14 + TypeScript + antd 5（兼容 6）+ Zustand + TanStack Query。
> Phase **M0**（2026-09-14）：Work List 静态页 + tokens.css 接入 + Ant Design Theme 注入。

## 当前进度

| 状态 | 范围 |
| --- | --- |
| ✅ 已落地 | 骨架（Next 14.2 / React 18.3 / antd 5.21.6 / TS 5.6）+ `tokens.css` 镜像接入 + `AppShell`（侧栏导航 + 顶栏）+ `QueryProvider` + Work List 静态页（mock data，4 条样例 WorkItem） |
| ⏳ 下一刀 | 接 `/api/v1/*`（**注意：当前 REST 端点无 `/api/v1` 前缀，见 `docs/review/2026-09-11-待拍板项.md` P2**）+ auth（access/refresh token + axios interceptor）|
| ⏳ 后续 | Needs You / Channels / Team 三页 + Work Detail + Work 新建弹窗 + Provider 切换器 + Playwright e2e |

## 版本与 README 的偏差

README §"技术选型" 写 "antd 6"，本骨架用 **antd 5.21.6**。
- **原因**：antd 6 (2024-10) 与 Next 14.2 + React 18.3 的 SSR 兼容性未在本地端到端验证；antd 5.21.6 与 Next 14 已稳定 build。
- **后续**：第二刀目标之一是把 antd 升 6（视觉/交互 API 与 5 高度重叠，迁移成本低）；当前避免一次性升级阻塞骨架验收。
- 收到 antd 6 升级命令后，跑 `npm install antd@^6 @ant-design/icons@latest` + 重新 `npm run build`，观察 hydration warning。

## 开发命令

```bash
# 安装依赖（首次较慢，~2-3 min；只用 npm，本机没装 pnpm/yarn）
npm install

# dev server
npm run dev
# → http://localhost:3000  ；默认重定向到 /projects/demo/work

# 类型检查
npm run typecheck

# production build
npm run build && npm run start
```

不需要 docker compose；Work List 当前是 mock data，**不调后端**。

## 目录结构

```
apps/web/
├── app/
│   ├── layout.tsx              # 根 layout：ConfigProvider + QueryProvider + AppShell
│   ├── globals.css             # 引入 tokens.css + 基础 reset + antd Table 边框对齐
│   ├── page.tsx                # / → 重定向到 /projects/demo/work
│   └── projects/[projectId]/work/page.tsx   # Work List（filter + TanStack Query + antd Table）
├── components/
│   ├── shell/AppShell.tsx      # 侧栏 + 顶栏，DS v0.7 导航（Needs You / Channels / Work / Team）
│   ├── providers/QueryProvider.tsx
│   └── work/
│       ├── WorkListTable.tsx
│       └── WorkStatusBadge.tsx # 状态徽标：DS v0.7 §3.1 颜色
├── lib/
│   ├── types/work.ts           # DTO 类型（手写；待 OpenAPI codegen 覆盖）
│   ├── api/work.ts             # API client stub（Phase M0 = mock data）
│   └── mock/work.ts            # 4 条样例 WorkItem + 2 个 Provider
├── styles/tokens.css           # ⚠️ mirror of ../../../docs/UI design/tokens.css
├── package.json
├── tsconfig.json
├── next.config.ts
└── .gitignore
```

## SSOT 提醒

- **设计令牌**：`styles/tokens.css` 是 `docs/UI design/tokens.css` 的镜像；
  改色 / 改间距 / 改字体**先改 docs/UI design/tokens.css**，再同步本文件。
  这一刀不引入 build-time diff 检查（避免 PostCSS 插件增加复杂度），由人 review。
- **Work DTO**：`lib/types/work.ts` 当前手写；待 `contracts/openapi/` 落地后由 codegen 覆盖
  （`docs/review/2026-09-11-待拍板项.md` P3）。
- **REST 前缀**：`apps/web` 假设后端**不带** `/api/v1` 前缀（与当前实现一致）；
  若 P2 拍板要加，base URL 需在 `lib/api/client.ts`（待建）集中处理。

## 已知技术债（不影响 M0 验收）

1. **mock data 走 useQuery**：M0 把 listWorkItems 写成"同步返回 mock"；
   切真实 API 时改回 `async` + `fetch`，但 queryKey 已对齐后端 query 参数。
2. **Work Detail 路由占位**：`/projects/{id}/work/{id}` 现在是 `<Link>` 但还没建页；
   点击会 404，等 M0+1 补。
3. **AppShell 缺 avatar menu**：Settings 入口（DS v0.7 §6）暂未实现。
4. **a11y**：Card / Segmented 来自 antd 自带 a11y；自定义徽标（WorkStatusBadge）已加 `aria-hidden` 到装饰点。
5. **i18n**：先 zh-CN，en-US 待 v0.2 收口。

## 测试（M0 未做）

- Playwright e2e：等 M0+1 接入真实 API 后写。
- 单测：Vitest（仅前端）按 `detailed/09` §5 配置；M0 不引入。
