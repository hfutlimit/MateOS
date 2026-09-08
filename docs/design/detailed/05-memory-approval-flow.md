# Detailed Design · 05 · Memory Approval Flow

> E5 Shared Memory + 人审门禁 + Source 溯源。
> 前置：[00-overview.md](./00-overview.md) / [07-permission-and-approval-orchestration.md](./07-permission-and-approval-orchestration.md)

## 0. 范围

- Memory 4 类（Personal / Project / Decision / Knowledge）
- Source 三件套强约束
- Agent 申请 → 人类审批 → 写入
- P6 审批中心
- 消息流 MEMORY_REQUEST 投影
- 检索（V1 简化：tsv + 强制 project_id 过滤）

## 1. 4 类 Memory

| 类型 | 归属 | 谁能读 | 谁能写（v0.4.2） |
| --- | --- | --- | --- |
| `PERSONAL` | owner_user_id | 仅 owner | `write_memory` permission (REQUIRE_APPROVAL) → 走 E5 流程 |
| `PROJECT` | project_id（所有 member） | 所有 project member | 同上 |
| `DECISION` | project_id | 所有 project member | 同上 |
| `KNOWLEDGE` | project_id | 所有 project member | 同上 |

**关键**：v0.4.2 之前 Agent 可能写过（v0.4 删 can_execute 字段；现在 Capability 决定能不能申请，Permission 决定能否走）。

## 2. Source 三件套强约束

```sql
source_type         TEXT NOT NULL,             -- 'CHANNEL_MESSAGE' | 'HUMAN_DIRECT' | 'AGENT_OBSERVATION'
source_channel_id   UUID REFERENCES channels(id),  -- CHANNEL_MESSAGE 时必填
source_message_seq  BIGINT,                       -- CHANNEL_MESSAGE 时必填
source_message_id   UUID,                         -- 冗余便于 join
```

### 2.1 校验（v0.4.2 改）

```
INSERT/UPDATE memory_proposals:
  if source_type = 'CHANNEL_MESSAGE':
    必填 source_channel_id AND source_message_seq
  elif source_type = 'HUMAN_DIRECT':
    可空（用户直接写不引用消息）
  elif source_type = 'AGENT_OBSERVATION':
    可空（V2）
```

V1 强制 DB CHECK 约束：

```sql
CONSTRAINT chk_source_chmsg CHECK (
  (source_type = 'CHANNEL_MESSAGE' AND source_channel_id IS NOT NULL AND source_message_seq IS NOT NULL)
  OR (source_type <> 'CHANNEL_MESSAGE')
)
```

### 2.2 Personal Memory 校验

```sql
CONSTRAINT chk_owner_for_personal CHECK (
  (type = 'PERSONAL' AND proposed_by_user_id IS NOT NULL)
  OR (type <> 'PERSONAL')
)
```

Personal Memory 只能由 Human 申请（不能由 Agent 自动申请）。

## 3. 完整流程：Agent 申请 → 人类审批

### 3.1 流程图

```
T+0  Agent 在 channel 产生 MEMORY_REQUEST 消息
     (v0.4.1 projection: content.memory_proposal_ref=null，触发后写)

T+1  Agent 推 execution.event { event_type: 'MEMORY_REQUEST' }  (可选，记录在事件流)

T+2  Agent 调 E5 REST API: POST /memory-proposals
     Headers: X-Agent-Token
     Body:
       {
         "type": "PROJECT",                            // 或 DECISION/KNOWLEDGE
         "title": "重试策略约定",
         "content": "# 重试策略约定\n\n- max_attempts: 5\n- ...",
         "source_type": "CHANNEL_MESSAGE",
         "source_channel_id": "...",
         "source_message_seq": 42
       }

T+3  E5 Memory Module:
     a) 校验 Agent 身份（agent_token）
     b) 校验 type：Agent 只能 PROJECT/DECISION/KNOWLEDGE
     c) 校验 Source 三件套（DB CHECK）
     d) 校验 Content（防止 XSS：DOMPurify-like sanitize）
     e) INSERT memory_proposals (status=PROPOSED, proposed_by_agent_id=agent.id)

T+4  E5 写消息流 MEMORY_REQUEST 投影:
     INSERT messages (content_type='MEMORY_REQUEST', content={memory_proposal_ref: proposal.id})

T+5  E5 WS 广播:
     - 'memory.proposal_created' 给所有 project online member（per project owner 优先）
     - 'message.created' 给 channel online member

T+6  人类 owner 在 P6 审批中心看到待办（聚合）
     - 显示：title / type / content / Source 引用（点击跳到原消息）

T+7  owner 选「批准」:
     POST /memory-proposals/:id/approve
     Headers: { Authorization: Bearer ... }
     Body: {} (optional note)

T+8  E5 校验:
     a) 校验 owner 权限: approve_memory (E6)
     b) UPDATE memory_proposals SET status='APPROVED', approved_by=owner.id, approved_at=now()
     c) INSERT memory_items (proposal_id, type, title, content, source_*, approved_by, version=1)
     d) 入队 memory.index async（分块 + tsv 写 memory_chunks）
     e) WS 广播 'memory.proposal_approved'

T+9  索引 worker:
     - SELECT content FROM memory_items WHERE id=?
     - 分块（V1: 200 chars/chunk）
     - 对每块：to_tsvector + INSERT memory_chunks
     - UPDATE memory_items.search_text = to_tsvector('simple', content)

T+10  原 MEMORY_REQUEST 投影消息标记 done:
     - 看 P5 视觉：从"待批准" 变 "已写入项目记忆 · Jason 批准"
     - 实现：客户端重新拉取 proposal → status='APPROVED' → 显示 done 样式
```

### 3.2 Reject 路径

```
owner 选「驳回」:
  POST /memory-proposals/:id/reject
  Body: { reason: "内容不准确" }

E5:
  a) UPDATE memory_proposals SET status='REJECTED', rejected_by=owner.id, reject_reason=?
  b) WS 广播 'memory.proposal_rejected'
  c) 消息流：原 MEMORY_REQUEST 投影消息消失（或标 "rejected"）
  d) 写 memory_review_actions (action='REJECT', note=reason)
  e) REJECTED 记录保留 1 年（供 Agent 学习）
```

### 3.3 Edit-Approve 路径

```
owner 选「编辑后批准」:
  POST /memory-proposals/:id/edit-approve
  Body: { content: "<edited content>" }

E5:
  a) 用新 content 写入 memory_items（覆盖原 proposal.content）
  b) status='APPROVED'
  c) version=1（V1 简化：覆盖）
  d) V2 引入版本表：保留历史版本
```

### 3.4 Withdraw 路径

```
申请人主动撤回（仅 PROPOSED 状态）:
  POST /memory-proposals/:id/withdraw
  Headers: { Authorization: Agent token or user token }

E5:
  a) 校验申请人是当前调用方（proposed_by_agent_id=agent.id 或 proposed_by_user_id=user.id）
  b) UPDATE status='WITHDRAWN'
  c) 消息流投影消失
```

## 4. 数据模型（v0.4.1 拆表）

### 4.1 memory_proposals（v0.4.1 拆出：申请阶段事实源）

```sql
CREATE TABLE memory_proposals (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  type                TEXT NOT NULL CHECK (type IN ('PERSONAL','PROJECT','DECISION','KNOWLEDGE')),
  title               TEXT NOT NULL,
  content             TEXT NOT NULL,
  status              TEXT NOT NULL DEFAULT 'PROPOSED'
                      CHECK (status IN ('PROPOSED','APPROVED','REJECTED','WITHDRAWN')),
  source_type         TEXT NOT NULL,
  source_channel_id   UUID REFERENCES channels(id),
  source_message_seq  BIGINT,
  source_message_id   UUID,
  proposed_by_agent_id UUID REFERENCES agents(id),
  proposed_by_user_id  UUID REFERENCES users(id),
  approved_by         UUID REFERENCES users(id),
  rejected_by         UUID REFERENCES users(id),
  reject_reason       TEXT,
  created_at          TIMESTAMPTZ DEFAULT now(),
  approved_at         TIMESTAMPTZ,
  -- 强约束（DB CHECK）
  CONSTRAINT chk_source_chmsg CHECK (
    (source_type = 'CHANNEL_MESSAGE' AND source_channel_id IS NOT NULL AND source_message_seq IS NOT NULL)
    OR (source_type <> 'CHANNEL_MESSAGE')
  ),
  CONSTRAINT chk_owner_for_personal CHECK (
    (type = 'PERSONAL' AND proposed_by_user_id IS NOT NULL)
    OR (type <> 'PERSONAL')
  )
);
```

### 4.2 memory_items（v0.4.1 拆出：已批准事实源）

```sql
CREATE TABLE memory_items (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  proposal_id         UUID NOT NULL REFERENCES memory_proposals(id),  -- v0.4.1：必须从 proposal 来
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
```

### 4.3 memory_review_actions（v0.4.1 改：记 proposal）

```sql
CREATE TABLE memory_review_actions (
  id          UUID PRIMARY KEY,
  proposal_id UUID NOT NULL REFERENCES memory_proposals(id),  -- v0.4.1 改
  actor_id    UUID NOT NULL REFERENCES users(id),
  action      TEXT NOT NULL CHECK (action IN ('APPROVE','REJECT','EDIT_APPROVE','WITHDRAW')),
  note        TEXT,
  created_at  TIMESTAMPTZ DEFAULT now()
);
```

## 5. 消息流投影

### 5.1 MEMORY_REQUEST 形态（v0.4.1 projection）

```jsonc
// messages.content
{
  "memory_proposal_ref": "proposal-uuid",  // 唯一事实引用
  "summary": "申请写入项目记忆《重试策略约定》"  // 缓存，详情走 entity_ref
}
```

不复制 title/content；详情走 `GET /memory-proposals/:id`。

### 5.2 UI 渲染

```
P5 收到 message.created { content_type: 'MEMORY_REQUEST' }
  → 显示：标题 + 摘要 + 三按钮（批准/驳回/编辑后批准）
  → 点击 "查看详情" → GET /memory-proposals/:id
```

### 5.3 状态变化时的消息流

| 状态变化 | 消息流表现 |
| --- | --- |
| `PROPOSED` | MEMORY_REQUEST 卡片：pending 状态 |
| `APPROVED` | 卡片变 "已写入项目记忆 · Jason 批准"（done 样式） |
| `REJECTED` | 卡片消失 + SYSTEM 事件 "Jason 驳回了记忆申请《xxx》" |
| `WITHDRAWN` | 卡片消失 |

**实现**：客户端订阅 `memory.proposal_*` 事件，收到后刷新对应的 projection 卡片。

## 6. 检索（V1 简化）

### 6.1 端点

```http
GET /work-items/search?q=...&type=...&status=...&page=...&size=20
```

不对，是 memory search：

```http
GET /memory-items/search?q=...&type=PROJECT&page=1&size=20
Headers: { Authorization: Bearer ... }
```

### 6.2 查询

```sql
SELECT id, title, type, snippet, source_*
FROM memory_items m
WHERE m.project_id IN (  -- 强制 project 隔离
    SELECT id FROM projects WHERE team_id IN (
      SELECT team_id FROM team_members WHERE user_id = $current_user
    )
  )
  AND m.type = $type  -- 可选
  AND (m.owner_user_id = $current_user OR m.type != 'PERSONAL')  -- Personal 仅 owner
  AND m.search_text @@ websearch_to_tsquery('simple', $q)
ORDER BY ts_rank(m.search_text, websearch_to_tsquery($q)) DESC
LIMIT 20;
```

**强制 project_id 过滤**：从 session 用户的 team 推导，绝不返回跨 project 结果。

### 5.4 Personal 隔离

```sql
AND (m.owner_user_id = $current_user OR m.type != 'PERSONAL')
```

Personal Memory 只能被 owner 自己看到。

## 7. P6 审批中心

### 7.1 列表

```
GET /memory-proposals/pending
Query: ?project_id=&type=&since=
Response: [
  { id, title, type, proposed_by_agent_name, source: {...}, created_at }
]
```

聚合：人类 owner 登录后看到所有（其 owner 的）项目的待审批 proposals。

### 7.2 详情

```
GET /memory-proposals/:id
Response: {
  id, type, title, content, source: {channel, seq, message_preview},
  proposed_by_agent: {id, name, avatar},
  created_at
}
```

### 7.3 审批操作

```http
POST /memory-proposals/:id/approve
POST /memory-proposals/:id/reject   Body: { reason }
POST /memory-proposals/:id/edit-approve Body: { content }
POST /memory-proposals/:id/withdraw  (申请人)
```

## 8. 索引（V1 简化）

### 8.1 memory_chunks

```sql
CREATE TABLE memory_chunks (
  id          UUID PRIMARY KEY,
  memory_id   UUID NOT NULL REFERENCES memory_items(id) ON DELETE CASCADE,
  chunk_text  TEXT NOT NULL,
  tsv         tsvector
);
```

### 8.2 索引 worker

```python
async def index_memory(memory_id):
    memory = await get_memory_item(memory_id)
    chunks = chunk_text(memory.content, chunk_size=200)  # V1: 200 chars
    for i, chunk in enumerate(chunks):
        await db.execute("""
            INSERT INTO memory_chunks (memory_id, chunk_text, tsv)
            VALUES ($1, $2, to_tsvector('simple', $2))
        """, memory_id, chunk)
    await db.execute("""
        UPDATE memory_items SET search_text = to_tsvector('simple', content) WHERE id = $1
    """, memory_id)
```

V2：embedding + pgvector + rerank。

## 9. 与 Permission 的集成

- Agent 想写 memory：调 `checkPermission(agent, 'write_memory', project_scope)`
- 旧 v0.4.1 行为：Guard 收到 REQUIRE_APPROVAL → 放行 + 业务层"忘记"调用 → 漏洞
- **v0.4.2 改**：Guard 收到 REQUIRE_APPROVAL → 拒绝 403
- 业务层（E5）显式 `policy.evaluate('write_memory', {project_id, ...})` → 走 proposal 创建路径

详见 [07-permission-and-approval-orchestration.md](./07-permission-and-approval-orchestration.md)

## 10. E2E 验收点

```
e2e/05-memory-approval/
  test_001_agent_propose.json
    Given Agent with capabilities + permission
    When POST /memory-proposals
    Then status=PROPOSED, MEMORY_REQUEST 消息流, WS 通知 owner

  test_002_owner_approve.json
    Given PROPOSED proposal
    When POST /approve
    Then status=APPROVED, memory_items created, indexed

  test_003_owner_reject.json
    When POST /reject
    Then status=REJECTED, reason stored, message removed

  test_004_source_required.json
    When proposal without source_channel_id
    Then 400 (DB CHECK 拦截)

  test_005_personal_isolation.json
    Given user A has Personal memory
    When user B queries
    Then 0 results (Personal 隔离)

  test_006_cross_project_isolation.json
    Given memory in Project X
    When user from Project Y queries
    Then 0 results

  test_007_withdraw.json
    When applicant withdraws
    Then status=WITHDRAWN, message removed

  test_008_audit_trail.json
    Every action writes memory_review_actions
```

## 11. 与其他设计的关系

- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（消息流时序）
- 详见 [07-permission-and-approval-orchestration.md](./07-permission-and-approval-orchestration.md)（REQUIRE_APPROVAL 流程）
- 详见 [08-error-and-retry.md](./08-error-and-retry.md)（索引失败重试）
