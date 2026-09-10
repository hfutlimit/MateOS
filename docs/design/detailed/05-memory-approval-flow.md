# Detailed Design · 05 · Memory Approval Flow

> **v0.4.3 修正**：
> 1. **P1-8**：`/memory-proposals` 端点不挂只决 ALLOW 的 Guard；业务层显式 `policy.evaluate('propose_memory')`（拆权限键）
> 2. **P1-9**：Memory approval CAS + `UNIQUE(proposal_id)` 防重复
> 3. **P1-10**：Memory Search 用 `accessible_project_ids()` 不手写 team→project
> 前置：[00-overview.md](./00-overview.md) / [07-permission-and-approval-orchestration.md](./07-permission-and-approval-orchestration.md)

## 0. 范围

- Memory 4 类（Personal / Project / Decision / Knowledge）
- Source 三件套强约束
- Agent 申请 → 人类审批 → 写入
- P6 审批中心
- 消息流 MEMORY_REQUEST 投影
- 检索（V1 简化：tsv + accessible_project_ids）

## 1. 4 类 Memory

（同 v0.4.2）

| 类型 | 归属 | 谁能读 | 谁能写（v0.4.3 改） |
| --- | --- | --- | --- |
| `PERSONAL` | owner_user_id | 仅 owner | `propose_memory` permission |
| `PROJECT` | project_id | accessible_project_ids(current_user) | 同上 |
| `DECISION` | project_id | 同上 | 同上 |
| `KNOWLEDGE` | project_id | 同上 | 同上 |

**v0.4.3 关键拆分**：
- 旧：`write_memory` 一个键，Agent 写=REQUIRE_APPROVAL，Guard 永远 403
- 新：**`propose_memory`**（申请 proposal）+ **`write_memory`**（直接写已批准 memory）两键

## 2. Source 三件套强约束

（同 v0.4.2）

## 3. 完整流程：Agent 申请 → 人类审批（v0.4.3 修正 P1-8）

### 3.1 v0.4.2 的问题

```ts
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('write_memory')  // 默认 REQUIRE_APPROVAL
@Post('/memory-proposals')
async proposeMemory(...) { ... }
```

**bug**：Guard 收到 REQUIRE_APPROVAL → 拒绝 → 所有人调 `/memory-proposals` 都 403 → 端点形同虚设

### 3.2 v0.4.3 修复

**permission 拆键**：

```python
# 7 键 + 2 个新键
PERMISSION_KEYS = [
    'read_message', 'write_message', 'write_memory', 'execute_code',
    'create_pr', 'approve_memory', 'manage_channel',
    # v0.4.3 新增
    'propose_memory'   # 申请（Agent + Human 都需；只 ALLOW）
]
```

**默认矩阵（v0.4.3 改）**：

| 键 | Human owner | Human member | Agent (lifecycle=ACTIVE) |
| --- | --- | --- | --- |
| `propose_memory` | ALLOW | ALLOW | ALLOW |
| `write_memory` | DENY | DENY | DENY |
| `approve_memory` | ALLOW | DENY | DENY |

**`write_memory` 真正只能用于：**
- 内部 service（E5 自己批准后写）
- 写已通过审批的 memory_items（v0.4.3：proposal 批准后由 E5 service-to-service 调，不再经 Guard）

**`/memory-proposals` 端点**：

```ts
@UseGuards(JwtGuard, PermissionGuard)
@RequirePermission('propose_memory')  // ALLOW 通过
@Post('/memory-proposals')
async proposeMemory(@Body() body, @Req() req) {
  // 1. Guard 已确保 propose_memory = ALLOW
  // 2. INSERT memory_proposals (status=PROPOSED)
  const proposal = await db.insert_memory_proposal({...body, status: 'PROPOSED'});
  return proposal;
}
```

**结果**：Agent/Human 都能申请。审批在 P6 独立走。

### 3.3 流程图

```
T+0  Agent POST /memory-proposals { type, title, content, source_* }
T+1  Guard: checkPermission(agent, 'propose_memory', project)
T+2  Guard: ALLOW → next
T+3  E5: 校验 type / Source / sanitize content
T+4  E5: INSERT memory_proposals (status=PROPOSED, proposed_by_agent_id)
T+5  E5: 写消息流 MEMORY_REQUEST projection (content.memory_proposal_ref)
T+6  E5: WS 推 memory.proposal_created 给 project owner
T+7  Owner 在 P6 选「批准」
T+8  POST /memory-proposals/:id/approve
T+9  E5: v0.4.3 CAS + UNIQUE(proposal_id) 防重（详见 §6）
T+10 E5: BEGIN transaction:
       - CAS proposal: PROPOSED → APPROVED
       - INSERT memory_items
       - INSERT memory_review_actions
       - INSERT outbox('memory.approved')
       COMMIT
T+11 outbox worker 拾取 → 写 tsvector 索引
T+12 原 MEMORY_REQUEST projection 变 "已写入项目记忆 · Jason 批准"
```

### 3.4 Reject / Edit-Approve / Withdraw

（同 v0.4.2，CAS 都用上）

## 4. 数据模型（v0.4.3 加 UNIQUE）

### 4.1 memory_proposals（v0.4.3 改：proposal_id 唯一）

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
  -- 强约束
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

### 4.2 memory_items（v0.4.3 加 UNIQUE(proposal_id)）

```sql
CREATE TABLE memory_items (
  id                  UUID PRIMARY KEY,
  project_id          UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  proposal_id         UUID NOT NULL UNIQUE REFERENCES memory_proposals(id),  -- v0.4.3: UNIQUE
  owner_user_id       UUID REFERENCES users(id),
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

### 4.3 memory_review_actions

```sql
CREATE TABLE memory_review_actions (
  id          UUID PRIMARY KEY,
  proposal_id UUID NOT NULL REFERENCES memory_proposals(id),
  actor_id    UUID NOT NULL REFERENCES users(id),
  action      TEXT NOT NULL CHECK (action IN ('APPROVE','REJECT','EDIT_APPROVE','WITHDRAW')),
  note        TEXT,
  created_at  TIMESTAMPTZ DEFAULT now()
);
```

## 5. Approve 完整流程（v0.4.3 修复 P1-9 race）

### 5.1 Race 场景

```
Owner A 和 Owner B 同时点击「批准」:

T+0  A 读 proposal.status='PROPOSED'
T+1  B 读 proposal.status='PROPOSED'
T+2  A INSERT memory_items
T+3  B INSERT memory_items
T+4  结果: 同一 proposal_id 两条 memory_items
```

### 5.2 v0.4.3 修复：CAS + UNIQUE

```python
async def approve_memory(proposal_id, owner):
    async with db.transaction() as tx:
        # 1. CAS proposal (事实源)
        affected = await tx.execute("""
            UPDATE memory_proposals
            SET status='APPROVED', approved_by=$2, approved_at=NOW()
            WHERE id=$1 AND status='PROPOSED'
            RETURNING id, type, content, ...
        """, proposal_id, owner.id)

        if not affected:
            # 已 approved/rejected/withdrawn
            # 或不存在
            return await get_proposal(proposal_id)  # 返当前状态

        proposal = affected[0]

        # 2. INSERT memory_items
        #    UNIQUE(proposal_id) 双保险
        await tx.execute("""
            INSERT INTO memory_items
            (project_id, proposal_id, owner_user_id, type, title, content, source_*, approved_by)
            VALUES (...)
        """, ...)

        # 3. 写 review_action
        await tx.execute("""
            INSERT INTO memory_review_actions (proposal_id, actor_id, action)
            VALUES ($1, $2, 'APPROVE')
        """, proposal_id, owner.id)

        # 4. 写 outbox
        await tx.execute("""
            INSERT INTO outbox_events (event_type, payload, idempotency_key)
            VALUES ('memory.approved', $1, $2)
        """, {...}, f'memory-approved-{proposal_id}')

    # 5. outbox worker → 索引
```

**v0.4.3 双保险**：
- CAS `WHERE status='PROPOSED'`：仅一人 CAS 成功
- `UNIQUE(proposal_id)`：DB 层兜底（即使 CAS 失败，DB 也拒绝重复 INSERT）

## 6. 检索（V1 简化 + v0.4.3 修复 P1-10）

### 6.1 v0.4.2 的问题

```sql
-- 手写 team → project
WHERE m.project_id IN (
  SELECT id FROM projects WHERE team_id IN (
    SELECT team_id FROM team_members WHERE user_id = $1
  )
)
```

**问题**：
- 没考虑 project_members（team member 不一定是 project member）
- 没考虑 Personal Memory
- 每个模块都自己写一遍 → 旁路 authorization 风险

### 6.2 v0.4.3 修复：accessible_project_ids()

```python
# packages/contracts
async def accessible_project_ids(principal: MemberRef) -> list[UUID]:
    """统一权限查询：返回 principal 能访问的所有 project_id"""
    if principal.type == 'USER':
        # v0.4.4 修正：只按显式 Project membership 授权
        return await db.query("""
            SELECT project_id FROM project_members
            WHERE user_id = $1
        """, principal.id)
    elif principal.type == 'AGENT':
        return await db.query("""
            SELECT project_id FROM agent_project_membership
            WHERE agent_id = $1 AND can_read_history = true
        """, principal.id)
```

**v0.4.4 越权修复要点（替换 §6.1 指出的问题）**：

- 删掉 `WHERE team_id IN (SELECT team_id FROM team_members ...)` 分支。**Team 成员 ≠ Project 成员**：用 Team 反查会把该 Team 下用户**并未加入**的 Project 全部放行；用户被移出某 Project 后，只要仍在 Team 内就仍能读到该 Project 的共享 Memory。原 v0.4.3 写法只是把这条越权分支"统一"到了一个函数里，问题本身没消除。
- 唯一授权来源：User → `project_members`；Agent → `agent_project_membership(can_read_history)`。所有跨模块可见性查询（Memory 检索、Inbox 聚合、WorkItem 列表）都必须走 `accessible_project_ids()`，禁止各模块手写 team→project 反查。
- 回归用例（必须进 e2e）：**同 Team、不同 Project** —— 用户 A 是 Team T 成员但只加入 P1，检索 `scope_type='PROJECT'` 的 Memory 时不得返回 P2 的任何条目；随后把 A 从 P1 的 `project_members` 移除，同一查询立即返回空集。

### 6.3 搜索查询（v0.4.3 修正）

```sql
SELECT id, title, type, snippet, source_*
FROM memory_items m
WHERE m.project_id = ANY($1::uuid[])  -- accessible_project_ids
  AND (m.owner_user_id = $2 OR m.type != 'PERSONAL')  -- Personal 仅 owner
  AND (m.type = $3 OR $3 IS NULL)
  AND m.search_text @@ websearch_to_tsquery('simple', $4)
ORDER BY ts_rank(m.search_text, websearch_to_tsquery($4)) DESC
LIMIT 20;
```

**v0.4.3 关键**：
- `project_id = ANY($accessible_project_ids)` —— 统一权限
- `owner_user_id = $current_user` for Personal
- 任何模块搜 memory 都必须走 `accessible_project_ids()`

## 7. P6 审批中心

（同 v0.4.2，但权限改用 `accessible_project_ids`）

## 8. 索引（V1 简化）

（同 v0.4.2）

## 9. 与 Permission 的集成（v0.4.3 修正 P1-8）

- Agent 想申请 memory：调 `checkPermission(agent, 'propose_memory', project_scope)`
- 默认 propose_memory = ALLOW（owner/member/Agent 都能申请）
- Agent 想直接写 memory（绕过 proposal）：无端点（write_memory 只能 internal 调用）

**关键**：`/memory-proposals` 端点不再挂 Guard 防绕过——直接靠 Guard `propose_memory=ALLOW` 通过即可，审批在 P6 独立。

## 10. E2E 验收点

```
e2e/05-memory-approval/
  test_001_agent_propose.json
    Given Agent with propose_memory=ALLOW
    When POST /memory-proposals
    Then status=PROPOSED, MEMORY_REQUEST 投影, WS 通知 owner

  test_002_owner_approve.json
    Given PROPOSED proposal
    When POST /approve
    Then status=APPROVED, memory_items created, indexed

  test_003_owner_reject.json
    When POST /reject
    Then status=REJECTED, reason stored, message removed

  test_004_source_required.json
    When proposal without source_channel_id
    Then 400

  test_005_personal_isolation.json
    Given user A has Personal memory
    When user B queries
    Then 0 results (Personal 隔离 + accessible_project_ids)

  test_006_cross_project_isolation.json           # v0.4.3 改
    Given memory in Project X
    When user from Project Y queries
    Then 0 results (accessible_project_ids 不包含 X)

  test_007_approve_idempotent_race.json           # v0.4.3 修复 P1-9
    Given 2 owners click approve at same time
    When POST /approve (concurrent)
    Then only 1 succeeds, 1 memory_items row exists (UNIQUE constraint)

  test_008_withdraw.json
    When applicant withdraws
    Then status=WITHDRAWN, message removed

  test_009_audit_trail.json
    Every action writes memory_review_actions
```

## 11. 与其他设计的关系

- 详见 [07-permission-and-approval-orchestration.md](./07-permission-and-approval-orchestration.md)（P1-8 permission 拆分）
- 详见 [01-single-agent-task-lifecycle.md](./01-single-agent-task-lifecycle.md)（消息流时序）
