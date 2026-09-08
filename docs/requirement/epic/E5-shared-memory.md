# E5 · Shared Memory & Knowledge Base

| 字段 | 值 |
| --- | --- |
| Epic ID | E5 |
| 标题 | Shared Memory & Knowledge Base |
| 阶段 | MVP（M5，v0.3 起从 V2 提前） |
| 上游 | PRD v0.3 §5 FR-7 / SYSTEM_DESIGN v0.2 §4.3 / §5.1 memory_items + memory_chunks / UI DS §5.1 记忆申请卡 + §6 P6/P7 |
| 下游 | E4（Need Context 可触发记忆搜索）、E7（dispatch 注入 memory_refs）、E9（外部系统同步） |
| 状态 | Draft |

## 1. 背景与动机

团队知识的复用是 AI Coding 的核心价值。Shared Memory 把"谁在某频道说过什么"固化为可检索的项目级知识。**人审门禁**是 PRD 硬性要求（FR-7）——记忆质量优先于数量。

本 epic：
- 4 类 Memory（Personal / Project / Decision / Knowledge）落库 + Source 溯源
- Agent 申请 → 人类批准 → 入库
- P6 审批中心统一收口
- P7 Memory 文档详情页（Markdown + 类型标签 + 版本 + 批准人 + Source）
- embedding 索引与检索（V2 全文 + 向量，V1 仅强约束字段）

## 2. 范围

### 2.1 In Scope

- 4 类 Memory CRUD（Personal/Project/Decision/Knowledge）
- 状态机：PROPOSED → APPROVED / REJECTED
- Source 三件套（`source_type` / `source_channel_id` / `source_message_seq`）强约束
- Memory 申请消息（MEMORY_REQUEST，E3 5 形态之一）
- P6 审批中心（待办列表 + 批准/驳回/编辑后批准）
- P7 Memory 文档（Markdown 渲染 + 类型 + 状态 + Source + 批准人 + 版本）
- 引用：消息流通过 `/` 触发记忆引用（v0.4 UI DS §5.4）
- 跨 Project 隔离：检索时按 project_id 强制过滤

### 2.2 Out of Scope

- 自动 Memory Extraction（V1 Non Goal，PRD §11）
- 知识图谱 / 实体抽取（V2+）
- 跨项目记忆共享（V2+）
- Memory 自动修订 / 评论（V2+）

## 3. 数据模型

```sql
-- memory_items
CREATE TABLE memory_items (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  owner_user_id       UUID REFERENCES users(id),     -- Personal Memory 才填
  type                TEXT NOT NULL CHECK (type IN ('PERSONAL','PROJECT','DECISION','KNOWLEDGE')),
  title               TEXT NOT NULL,
  content             TEXT NOT NULL,                  -- Markdown
  status              TEXT NOT NULL DEFAULT 'PROPOSED'
                      CHECK (status IN ('PROPOSED','APPROVED','REJECTED')),
  -- Source 溯源三件套（v0.2 强约束）
  source_type         TEXT NOT NULL,                  -- 'CHANNEL_MESSAGE' | 'HUMAN_DIRECT' | 'AGENT_OBSERVATION'
  source_channel_id   UUID REFERENCES channels(id),  -- CHANNEL_MESSAGE 时必填
  source_message_seq  BIGINT,                          -- CHANNEL_MESSAGE 时必填
  source_message_id   UUID,                            -- 冗余便于 join
  -- 批准信息
  proposed_by_agent_id UUID REFERENCES agents(id),    -- Agent 申请时填
  proposed_by_user_id  UUID REFERENCES users(id),    -- Human 直接申请时填
  approved_by         UUID REFERENCES users(id),     -- APPROVED 时填
  rejected_by         UUID REFERENCES users(id),     -- REJECTED 时填
  reject_reason       TEXT,
  -- 版本（V2 编辑引入，V1 仅 1）
  version             INT NOT NULL DEFAULT 1,
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now(),
  approved_at         TIMESTAMPTZ,
  -- 约束
  CONSTRAINT chk_source_chmsg CHECK (
    (source_type = 'CHANNEL_MESSAGE' AND source_channel_id IS NOT NULL AND source_message_seq IS NOT NULL)
    OR (source_type <> 'CHANNEL_MESSAGE')
  ),
  CONSTRAINT chk_owner_for_personal CHECK (
    (type = 'PERSONAL' AND owner_user_id IS NOT NULL)
    OR (type <> 'PERSONAL')
  )
);
CREATE INDEX idx_memory_project_status ON memory_items(project_id, status);
CREATE INDEX idx_memory_type ON memory_items(project_id, type, status);
CREATE INDEX idx_memory_source ON memory_items(source_channel_id, source_message_seq);

-- memory_chunks（V2 索引；V1 占位 + full-text）
CREATE TABLE memory_chunks (
  id          UUID PRIMARY KEY,
  memory_id   UUID NOT NULL REFERENCES memory_items(id) ON DELETE CASCADE,
  chunk_text  TEXT NOT NULL,
  -- V1：仅 tsv；V2 加 embedding
  tsv         tsvector
);
CREATE INDEX idx_memory_chunks_tsv ON memory_chunks USING gin(tsv);
CREATE INDEX idx_memory_chunks_memory ON memory_chunks(memory_id);

-- memory_review_actions（审计）
CREATE TABLE memory_review_actions (
  id          UUID PRIMARY KEY,
  memory_id   UUID NOT NULL REFERENCES memory_items(id),
  actor_id    UUID NOT NULL REFERENCES users(id),
  action      TEXT NOT NULL CHECK (action IN ('APPROVE','REJECT','EDIT_APPROVE','WITHDRAW')),
  note        TEXT,
  created_at  TIMESTAMPTZ DEFAULT now()
);
```

### 3.1 状态机

```
PROPOSED ─┬─► APPROVED（approved_by 必填，approved_at 必填）
          ├─► REJECTED（rejected_by 必填，reject_reason 必填，保留 1 年）
          └─► WITHDRAW（申请人主动撤回，仅 PROPOSED 可撤回）
```

### 3.2 Source 溯源

- `CHANNEL_MESSAGE` → 必须填 channel_id + seq + message_id
- `HUMAN_DIRECT` → Human 直接创建（不开审批？）→ **MVP：Human 直接创建也走 PROPOSED 流程**
- `AGENT_OBSERVATION` → Agent 离线整理（V2）

## 4. API

### 4.1 REST

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET | `/projects/:id/memories` | 列表（按 type/status 过滤） | project member |
| GET | `/memory-items/:id` | 详情 | project member（Personal 仅 owner） |
| POST | `/memory-items` | 申请（Agent 走 PROPOSED 流程） | Agent token / login |
| POST | `/memory-items/:id/approve` | 批准 | project owner（write_memory permission） |
| POST | `/memory-items/:id/reject` | 驳回 | 同上 |
| POST | `/memory-items/:id/edit-approve` | 编辑后批准 | 同上（body 带新 content） |
| GET | `/memory-items/pending` | 待审批列表（P6 用） | project owner |
| GET | `/memory-items/search?q=...` | 搜索 | project member |

### 4.2 WebSocket

- `memory.proposed` — Agent 申请推送给 project owner
- `memory.approved` / `memory.rejected` — 状态变更广播

### 4.3 错误码

- 400 缺 Source 三件套（CHANNEL_MESSAGE 时）
- 403 非 owner 申请 Personal Memory
- 409 重复申请（同 source_message_id + agent_id）

## 5. 关键流程

### 5.1 Agent 申请记忆

```
1. Agent 在消息流产生记忆申请卡（MEMORY_REQUEST，E3 5 形态）
2. POST /memory-items
   { type, title, content, source_type:'CHANNEL_MESSAGE', source_channel_id, source_message_seq }
3. 服务端：
   a) 校验 type（Agent 只能申请 PROJECT / DECISION / KNOWLEDGE，不能 PERSONAL）
   b) 校验 Source 三件套
   c) 写入 memory_items status=PROPOSED, proposed_by_agent_id=...
   d) WS 推 memory.proposed 给所有 project owner 在线
4. 关联 message 写入 decision/proposal 标记
```

### 5.2 人类审批

```
1. P6 审批中心列出当前 user 所有 PROPOSED 记忆（按 type/时间分组）
2. 点 "批准"：
   POST /memory-items/:id/approve
   → 写入 approved_by + approved_at + status=APPROVED
   → 触发 memory.index async：分块 + tsv 写 memory_chunks（V1）
   → 写 memory_review_actions
   → WS 推 memory.approved
3. 点 "驳回"：必填 reject_reason，写 memory_review_actions
4. 点 "编辑后批准"：上传新 content，原 memory 标 v2，旧版保留（V1 简化：仅覆盖）
```

### 5.3 Source 溯源展示

- 记忆详情页（P7）必带一行：**Source · #webhook-retry · seq 42 · 2026-09-08 21:44**
- 记忆卡（消息流 MEMORY_REQUEST）同样带 Source 行（v0.4 UI DS §5.1）
- 缺 Source 的记忆：UI 显示红字 "Source 缺失（PRD FR-7 必填）"

### 5.4 检索（V1 简化）

```
GET /memory-items/search?q=...
  → 仅在已 APPROVED 中按 tsv @@ websearch_to_tsquery 检索
  → 强制 WHERE project_id = current_user_scope
  → Personal Memory 只对 owner 可见（owner_user_id = current_user_id）
  → 返回 { id, title, type, snippet, source: {...} }
```

V2 加 embedding + rerank。

## 6. UI

### 6.1 页面

- **P6 审批中心**（待做）：左侧 type 分组，右侧卡片列表 + 批准/驳回/编辑
- **P7 Memory 文档**（待做）：Markdown 渲染 + 元信息（Source / 批准人 / 版本）+ 关联消息跳转

### 6.2 关键组件

- `<MemoryCard>`：消息流中嵌入（MEMORY_REQUEST 形态）
  - 顶部：主色浅底 + 徽标「记忆写入申请 · 需人类批准」
  - 标题、Markdown 正文（mono 字体）
  - Source 行（v0.4 起必带）
  - 标签（PROJECT / DECISION / KNOWLEDGE + status）
  - 三按钮：批准 / 驳回 / 编辑后批准
- `<PendingBadge>`：右栏「待办」Tab 中聚合待审批计数

### 6.3 状态

- Agent 申请：消息流卡片 + 顶栏计数 +1 + 右栏待办
- 批准：卡片转「已写入项目记忆 · Jason 批准」（done 样式，灰底）
- 驳回：消息流卡片消失，生成系统事件「Jason 驳回了记忆写入申请《重试策略约定》」

## 7. 验收标准

### 7.1 功能

- **F1** Agent 申请记忆：必须填 Source 三件套，否则 400
- **F2** 批准后 status=APPROVED，触发 memory.index 异步落 memory_chunks
- **F3** 驳回必填 reject_reason，记录保留 1 年
- **F4** Personal Memory 仅 owner 可见
- **F5** 跨项目检索强制 project_id 过滤（V1 单元测试覆盖）
- **F6** 编辑后批准：content 替换，写 memory_review_actions('EDIT_APPROVE')
- **F7** 同一 source_message_id + agent_id 不重复申请（409）

### 7.2 E2E

- `e2e/E5-001-agent-propose-approve`：Agent 申请 → owner 批准 → APPROVED
- `e2e/E5-002-reject-with-reason`：驳回带 reason → REJECTED + audit
- `e2e/E5-003-source-required`：缺 Source 返 400
- `e2e/E5-004-personal-isolation`：他人看不到我的 Personal Memory
- `e2e/E5-005-cross-project-leak`：用 project A 的 token 搜不到 project B 的记忆
- `e2e/E5-006-p6-batch`：P6 列出 5 条待审，批量批准 3 条

### 7.3 非功能

- 申请记忆 P95 < 200ms
- memory.index 异步落 chunk P95 < 1s
- 10k memory_items 下 search P95 < 500ms

## 8. 与其他 Epic 的关系

- **被依赖**：
  - E7 dispatch 注入 memory_refs
  - E9 外部系统双向同步 memory
  - E8 audit 写记忆审批动作
- **依赖**：E1（Project）、E3（消息载体）、E6（write_memory permission）
- **冲突裁决**：Source 三件套强约束（PRD FR-7 硬性）— 任何记忆入库必须带

## 9. 风险与开放问题

- **R1**：embedding 模型选型与维度（SD §13 开放）— V1 不引入；V2 引入时需要迁移
- **R2**：Memory 引用频次如何计入 Agent 表现评分？→ V2 引入
- **R3**：跨项目记忆（如通用规范）V1 不支持；V2 通过"公开标志"开放
- **R4**：Markdown XSS 防护 → 服务端 sanitize（DOMPurify 等价实现）
- **R5**：V1 简化"编辑后批准"为覆盖，V2 引入版本表

## 10. 实施顺序（M5）

1. PG schema（memory_items / chunks / review_actions）+ 强约束
2. Agent 申请端点 + Source 校验
3. 审批中心 REST + WS 推送
4. 异步 memory.index worker（V1 仅 tsv）
5. 检索端点（V1 websearch_to_tsquery）
6. P6 / P7 UI（前端）
7. E2E 套件
