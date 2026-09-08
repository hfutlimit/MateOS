# E5 · Shared Context & Memory

| 字段 | 值 |
| --- | --- |
| Epic ID | E5 |
| 标题 | Shared Context & Memory |
| 阶段 | MVP（M5，v0.3 起从 V2 提前） |
| 上游 | PRD v0.4 §5 FR-6 / SYSTEM_DESIGN v0.3 §4.3 / §5.2 memory_proposals + memory_items / UI DS v0.5 §5.1 |
| 下游 | E4（Need Context 触发记忆搜索）、E7（dispatch 注入 memory_refs）、E8（WorkItem 可选引用 memory） |
| 状态 | Draft（v0.4 微调） |

## 1. 背景与动机

团队知识的复用是 AI Coding 的核心价值。v0.4 改动很小：**memory_proposals 表从 `memory_items` 拆出**（之前是直接 status=PROPOSED 在 memory_items 里，v0.4 拆成申请与已批准两个表，避免"已批准的记忆"和"待审批的记忆"混在同一表）。

其余边界不变：4 类 Memory（Personal/Project/Decision/Knowledge）+ Source 溯源强约束 + 人审门禁。

## 2. 范围

### 2.1 In Scope

- **v0.4 拆表** `memory_proposals`（PROPOSED 状态）+ `memory_items`（APPROVED 状态）两表
- 4 类 Memory：Personal / Project / Decision / Knowledge
- Source 三件套强约束（CHANNEL_MESSAGE 必填 channel_id + seq）
- Memory 申请消息（MEMORY_REQUEST 形态，**v0.4 改**只引 `memory_proposal_ref`）
- P6 审批中心
- P7 Memory 文档（Markdown + 类型 + Source + 批准人 + 版本）
- 引用：消息流 `/` 触发记忆引用
- 跨 Project 隔离

### 2.2 Out of Scope

- 自动 Memory Extraction（V1 Non Goal）
- Memory 检索（V1 简化：仅 tsv；V2 加 embedding + rerank）
- Memory 自动修订（V2+）
- 跨项目记忆共享（V2+）

## 3. 数据模型

```sql
-- memory_proposals（v0.4 拆出：申请阶段的事实源）
CREATE TABLE memory_proposals (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  type                TEXT NOT NULL CHECK (type IN ('PERSONAL','PROJECT','DECISION','KNOWLEDGE')),
  title               TEXT NOT NULL,
  content             TEXT NOT NULL,
  status              TEXT NOT NULL DEFAULT 'PROPOSED'
                      CHECK (status IN ('PROPOSED','APPROVED','REJECTED','WITHDRAWN')),
  -- Source 溯源三件套（强约束）
  source_type         TEXT NOT NULL,
  source_channel_id   UUID REFERENCES channels(id),
  source_message_seq  BIGINT,
  source_message_id   UUID,
  -- 申请人
  proposed_by_agent_id UUID REFERENCES agents(id),
  proposed_by_user_id  UUID REFERENCES users(id),
  -- 审批
  approved_by         UUID REFERENCES users(id),
  rejected_by         UUID REFERENCES users(id),
  reject_reason       TEXT,
  -- 时间
  created_at          TIMESTAMPTZ DEFAULT now(),
  approved_at         TIMESTAMPTZ,
  CONSTRAINT chk_source_chmsg CHECK (
    (source_type = 'CHANNEL_MESSAGE' AND source_channel_id IS NOT NULL AND source_message_seq IS NOT NULL)
    OR (source_type <> 'CHANNEL_MESSAGE')
  ),
  CONSTRAINT chk_owner_for_personal CHECK (
    (type = 'PERSONAL' AND proposed_by_user_id IS NOT NULL)
    OR (type <> 'PERSONAL')
  )
);
CREATE INDEX idx_memory_proposal_project_status ON memory_proposals(project_id, status);

-- memory_items（v0.4 拆出：已批准的事实源）
CREATE TABLE memory_items (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  proposal_id         UUID NOT NULL REFERENCES memory_proposals(id),  -- 必填，v0.4 起 proposal 不可绕过
  owner_user_id       UUID REFERENCES users(id),                       -- Personal 时填
  type                TEXT NOT NULL,
  title               TEXT NOT NULL,
  content             TEXT NOT NULL,
  source_type         TEXT NOT NULL,
  source_channel_id   UUID REFERENCES channels(id),
  source_message_seq  BIGINT,
  approved_by         UUID NOT NULL REFERENCES users(id),
  version             INT NOT NULL DEFAULT 1,
  created_at          TIMESTAMPTZ DEFAULT now(),
  updated_at          TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX idx_memory_items_project_type ON memory_items(project_id, type);

-- memory_chunks（V1 仅 tsv；V2 加 embedding）
CREATE TABLE memory_chunks (
  id          UUID PRIMARY KEY,
  memory_id   UUID NOT NULL REFERENCES memory_items(id) ON DELETE CASCADE,
  chunk_text  TEXT NOT NULL,
  tsv         tsvector
);
CREATE INDEX idx_memory_chunks_tsv ON memory_chunks USING gin(tsv);

-- memory_review_actions（审计）
CREATE TABLE memory_review_actions (
  id          UUID PRIMARY KEY,
  proposal_id UUID NOT NULL REFERENCES memory_proposals(id),  -- v0.4 改：记 proposal 而非 memory_item
  actor_id    UUID NOT NULL REFERENCES users(id),
  action      TEXT NOT NULL CHECK (action IN ('APPROVE','REJECT','EDIT_APPROVE','WITHDRAW')),
  note        TEXT,
  created_at  TIMESTAMPTZ DEFAULT now()
);
```

### 3.1 状态机

```
memory_proposals
  PROPOSED ─┬─► APPROVED（approved_by 必填 + approved_at + 写 memory_items）
            ├─► REJECTED（rejected_by 必填 + reject_reason 必填）
            └─► WITHDRAWN（申请人主动撤回，仅 PROPOSED 可撤回）

memory_items
  v1:1（一旦创建不可改，编辑 = 新建 v2 proposal，V2 起实现版本表）
```

## 4. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| GET | `/projects/:id/memory-proposals` | 申请列表 | project member |
| GET | `/memory-proposals/:id` | 详情（**v0.4 新增**） | project member |
| POST | `/memory-proposals` | 申请（Agent token / login） | 任意成员 |
| POST | `/memory-proposals/:id/approve` | 批准 | project owner（approve_memory） |
| POST | `/memory-proposals/:id/reject` | 驳回 | 同上 |
| POST | `/memory-proposals/:id/edit-approve` | 编辑后批准 | 同上 |
| POST | `/memory-proposals/:id/withdraw` | 撤回 | 申请人 |
| GET | `/projects/:id/memory-items` | 已批准列表 | project member |
| GET | `/memory-items/:id` | 详情 | project member（Personal 仅 owner） |
| GET | `/memory-items/search?q=...` | 检索 | project member |

### 4.2 WebSocket

- `memory.proposal_created`（v0.4 改）
- `memory.proposal_approved`
- `memory.proposal_rejected`

## 5. 关键流程

### 5.1 Agent 申请记忆

```
1. Agent 在消息流产生 MEMORY_REQUEST 投影（content.memory_proposal_ref 占位）
2. POST /memory-proposals
   { type, title, content, source_type:'CHANNEL_MESSAGE', source_channel_id, source_message_seq }
3. 服务端：
   a) 校验 type（Agent 只能 PROJECT / DECISION / KNOWLEDGE）
   b) 校验 Source 三件套
   c) 写入 memory_proposals status=PROPOSED
4. WS 推 memory.proposal_created 给 project owner
5. v0.4 改：消息流 MEMORY_REQUEST 形态的 content 只引 memory_proposal_ref，**不复制** proposal.title / content
```

### 5.2 人类审批

```
POST /memory-proposals/:id/approve
  → 写 memory_items（v0.4 改：从 proposals 复制字段 + proposal_id 引用）
  → proposal.status = APPROVED，approved_by / approved_at 必填
  → 触发 memory.index async：分块 + tsv 写 memory_chunks
  → 写 memory_review_actions
  → WS 推 memory.proposal_approved
```

### 5.3 检索（V1 简化）

同 v0.3：websearch_to_tsquery + 强制 project_id 过滤。

## 6. UI

### 6.1 页面

- **P6 审批中心**（待做）：包含 Memory proposal + WorkItem 审批
- **P7 Memory 文档**（待做）：从 memory_items 详情页

### 6.2 关键组件

- `<MemoryRequestCard>`：从 memory_proposals 投影（v0.4 改）—— Source 行必带
- `<MemoryCard>`：已批准展示
- `<PendingBadge>`：右栏待办聚合（v0.4 改：同时聚合 Memory + WorkItem + Agent 入频道审批）

## 7. 验收标准

### 7.1 功能

- **F1** Agent 申请：必填 Source 三件套，否则 400
- **F2** **v0.4 新增** proposal 必须先创建 + APPROVED → 才能写 memory_items（DB trigger 兜底）
- **F3** approve_memory 仅 project owner
- **F4** Personal Memory 仅 owner 可见
- **F5** 跨项目检索强制 project_id 过滤
- **F6** 消息流 MEMORY_REQUEST 形态只引 `memory_proposal_ref`，不复制 title/content
- **F7** 消息流 MEMORY_REQUEST → GET /memory-proposals/:id 拉取完整字段渲染

### 7.2 E2E

- `e2e/E5-001-propose-approve`
- `e2e/E5-002-reject-with-reason`
- `e2e/E5-003-source-required`
- `e2e/E5-004-personal-isolation`
- `e2e/E5-005-cross-project-leak`
- `e2e/E5-006-p6-batch`
- `e2e/E5-007-proposal-projection`（v0.4 新）

### 7.3 非功能

- 申请 P95 < 200ms
- memory.index 异步落 chunk P95 < 1s

## 8. 与其他 Epic 的关系

- **被依赖**：E4（Need Context 触发搜索）/ E7（dispatch 注入 memory_refs）/ E8（WorkItem 可选引用 memory）/ E10
- **依赖**：E1（Project）/ E3（消息载体）/ E6（write_memory = REQUIRE_APPROVAL）

## 9. 风险与开放问题

- **R1**：embedding 选型（V2 引入时需 migration）
- **R2**：Markdown XSS 防护（服务端 sanitize）
- **R3**：proposal 撤回后能否恢复？→ V1 不可逆（WITHDRAWN 终态）；V2 引入 reopen

## 10. 实施顺序（M5）

1. **v0.4 拆表** memory_proposals + memory_items 两表 + DB trigger
2. Agent 申请端点
3. 审批中心 REST + WS
4. 异步 memory.index worker
5. 检索端点
6. P6 / P7 UI
7. E2E 套件
