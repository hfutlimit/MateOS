# E3 · Channel & Timeline

| 字段 | 值 |
| --- | --- |
| Epic ID | E3 |
| 标题 | Channel & Timeline |
| 阶段 | MVP（M2） |
| 上游 | PRD v0.4 §5 FR-8 / SYSTEM_DESIGN v0.3 §3 channel / §5 messages / §5.2 |
| 下游 | E4（Trigger 来源）/ E5（记忆申请投影）/ E7（execution 输出投影）/ E10（audit 写消息） |
| 状态 | Draft（v0.4 架构修订版） |

## 1. 背景与动机

Channel 是通信边界。**v0.4 关键变化**：消息流从"事实源"改为"projection 投影"——5 形态 UI 保留（人类 / 决策投影 / 输出投影 / 系统 / 记忆申请投影），但 `messages.content` **不再复制** `decision_records` / `memory_proposals` / `agent_executions` 的完整字段，只引 `entity_ref`。

**为什么改**：
- 决策 / 记忆 / 执行有自己的 lifecycle，消息流只是时间线
- 复制字段 = 双写不一致风险
- UI 跳转（"查看决策详情"）必须去事实源，不能在消息流上 edit

## 2. 范围

### 2.1 In Scope

- Channel CRUD（name / description / archived）
- Channel 成员邀请（Human / Agent）
- 5 形态消息流（**projection**）：
  - HUMAN
  - DECISION（引 `decision_ref`）
  - AGENT_OUTPUT（引 `execution_ref`）
  - SYSTEM
  - MEMORY_REQUEST（引 `memory_proposal_ref`）
- seq 单调递增 + 断线续传
- 幂等去重（client_msg_id）
- 附件 S3 预签名直传
- 已读位点
- 软删
- P5 Channel 主界面

### 2.2 Out of Scope

- 消息编辑（V2 message_edits 版本表）
- Thread 模型（V1 用 parent_seq 简化，V2 独立表）
- 表情 / 私聊 / 已读
- 端到端加密

## 3. 数据模型

```sql
CREATE TABLE channels (
  id          UUID PRIMARY KEY,
  project_id  UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  name        TEXT NOT NULL,
  description TEXT,
  archived_at TIMESTAMPTZ,
  last_seq    BIGINT NOT NULL DEFAULT 0,
  created_at  TIMESTAMPTZ DEFAULT now()
);
CREATE UNIQUE INDEX idx_channels_project_name ON channels(project_id, name);

CREATE TABLE channel_members (
  channel_id   UUID NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
  member_type  TEXT NOT NULL CHECK (member_type IN ('HUMAN','AGENT')),
  member_id    UUID NOT NULL,
  joined_at    TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (channel_id, member_type, member_id)
);

CREATE TABLE channel_seq_counters (
  channel_id  UUID PRIMARY KEY REFERENCES channels(id) ON DELETE CASCADE,
  next_seq    BIGINT NOT NULL DEFAULT 1
);

-- messages（v0.4：5 形态仅保留 projection + entity_ref）
CREATE TABLE messages (
  id             UUID NOT NULL,
  channel_id     UUID NOT NULL,
  seq            BIGINT NOT NULL,
  sender_type    TEXT NOT NULL CHECK (sender_type IN ('HUMAN','AGENT','SYSTEM')),
  sender_id      UUID,
  content_type   TEXT NOT NULL CHECK (content_type IN ('HUMAN','DECISION','AGENT_OUTPUT','SYSTEM','MEMORY_REQUEST')),
  -- projection：DECISION 形态只存 entity_ref，不复制 decision_records 字段
  content        JSONB NOT NULL,
  -- mentions 提取（E4 解析后回填）
  mentions       JSONB,
  parent_seq     BIGINT,
  client_msg_id  UUID,
  trace_id       TEXT,
  created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
  deleted_at     TIMESTAMPTZ,
  PRIMARY KEY (channel_id, seq)
);
-- v0.4.5：删除 PARTITION BY RANGE(created_at)
--   （1）PG 16 声明式分区要求唯一约束必须包含分区键，PK (channel_id, seq) 建不出来
--   （2）Prisma migrate 管理不了声明式分区（架构评审「Prisma vs PG 分区」）
-- 替代：保留 (channel_id, seq) 主键 + 按月归档 job（V1+ 再评估分区）
CREATE INDEX idx_messages_channel_time ON messages(channel_id, created_at DESC);
```

### 3.1 content 五形态 schema（v0.4 projection）

```jsonc
// HUMAN
{ "text": "...", "mentions": [...], "attachment_ids": [...] }

// DECISION（v0.4 简化：只引 entity_ref）
{
  "decision_ref": "decision-uuid",       // 必填，事实源在 decision_records
  "summary": "ACCEPT"                    // 仅展示用的缓存，详情走 entity_ref
}

// AGENT_OUTPUT（v0.4：引 execution_ref）
{
  "execution_ref": "execution-uuid",
  "artifact_id": "artifact-uuid"          // 可选，附产物
}

// SYSTEM
{ "text": "...", "actions": [{ "label": "批准", "kind": "approve_memory", "target_ref": "memory_proposal-uuid" }] }

// MEMORY_REQUEST（v0.4：引 memory_proposal_ref）
{
  "memory_proposal_ref": "proposal-uuid",  // 事实源在 memory_proposals
  "summary": "申请写入项目记忆《重试策略约定》"
}
```

> **关键**：UI 渲染时，DECISION / AGENT_OUTPUT / MEMORY_REQUEST 形态需 fetch 对应实体填充（`decision_records.analysis` / `agent_executions.context_refs` / `memory_proposals.title`）。客户端通过 entity_ref 查询，避免双写。

## 4. API

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| POST / GET | `/projects/:pid/channels` | CRUD | project member |
| GET / PATCH | `/channels/:id` | 详情 / 修改 | channel member / owner |
| POST / DELETE | `/channels/:id/members` | 邀请 / 退出 | owner 或自身 |
| POST | `/channels/:id/messages` | 发送 | channel member |
| GET | `/channels/:id/messages` | 历史分页 | channel member |
| GET | `/channels/:id/messages?since_seq=N` | 增量续传 | 同上 |
| POST | `/attachments/presign` | 申请 S3 URL | channel member |
| POST | `/attachments/:id/confirm` | 上传完成 | channel member |
| POST | `/channels/:id/read` | 更新已读位点 | channel member |
| **GET** | **`/decisions/:id`** | **v0.4 新增** 事实源查询（投影补全用） | 关联成员 |
| **GET** | **`/executions/:id`** | **v0.4 新增** | owner / 相关成员 |
| **GET** | **`/memory-proposals/:id`** | **v0.4 新增** | project member |

### 4.2 WebSocket

- `message.created` / `message.seq_sync`
- `channel.member_joined` / `channel.member_left`
- 连接级 `resume(last_seq)`

## 5. 关键流程

### 5.1 发送 DECISION 消息（v0.4 修订）

```
1. E4 写 decision_records（事实源）
2. E3 写 messages：
   { content_type: 'DECISION', content: { decision_ref: '...', summary: 'ACCEPT' }, ... }
3. seq 分配事务
4. WS 广播 message.created
5. 客户端渲染时根据 decision_ref GET /decisions/:id 拉取完整字段
```

### 5.2 客户端渲染流程（v0.4 修订）

```
收到 message.created { content_type: 'DECISION', decision_ref: 'xxx' }
  → 立即显示 summary（消息体里的小摘要）
  → 异步 GET /decisions/:id 拉取 analysis / reason / missing
  → 填充决策卡片
  → 缓存 decision_records（短期内存缓存即可）
```

### 5.3 seq 分配、附件直传、resume 续传

同 v0.3 行为。

## 6. UI

### 6.1 页面

- **P5 Channel**（v0.4 → v0.5 回修）：5 形态 projection 渲染；决策卡 / 输出 / 记忆卡点击跳事实源详情

### 6.2 关键组件

- `<MessageBubble>`：5 形态
- `<DecisionCard>`：**从 decision_records 投影**（v0.4 改）—— 包含分析三格
- `<AgentOutputCard>`：从 agent_executions 投影
- `<MemoryRequestCard>`：从 memory_proposals 投影
- `<Composer>`：@ 弹层 + 附件
- `<ThreadToggle>`：v0.4 简化

## 7. 验收标准

### 7.1 功能

- **F1** 发消息后 channel 在线 member 收到 `message.created`
- **F2** 同 client_msg_id 重发 5 次：服务端只产生 1 条
- **F3** seq 单调，last_seq 准确
- **F4** resume(last_seq=500) 拿回 seq 501+
- **F5** 附件直传走 S3 预签名
- **F6** 非 channel member 返 403
- **F7** 软删后 GET 不返回
- **F8** **v0.4 新增** DECISION 消息的 `content` 不含 `analysis` / `missing` 字段（schema 校验）
- **F9** **v0.4 新增** GET /decisions/:id 返回完整字段（含 analysis 三件套）
- **F10** **v0.4 新增** 客户端拿到 decision_ref 后能正确渲染分析三格

### 7.2 E2E

- `e2e/E3-001-send-receive`
- `e2e/E3-002-idempotency`
- `e2e/E3-003-resume`
- `e2e/E3-004-attachment`
- `e2e/E3-005-permission-isolation`
- `e2e/E3-006-soft-delete`
- `e2e/E3-007-decision-projection`（v0.4 新）—— 发 DECISION 消息，验证消息体不含 analysis 字段，GET /decisions/:id 返完整

### 7.3 非功能

- 发送 P95 < 100ms
- WS 广播 P95 < 300ms

## 8. 与其他 Epic 的关系

- **被依赖**：E4（消息载体 + Trigger 提取）/ E5（记忆申请投影）/ E7（execution 输出投影）/ E10（audit）
- **依赖**：E1（Project + Member）
- **冲突裁决**：5 形态只投影不复制（PRD v0.4 §5 FR-8 / SD v0.3 §5.2）

## 9. 风险与开放问题

- **R1**：projection 拉取的 entity_ref 查询延迟 → 客户端短期内存缓存
- **R2**：消息引用 entity_ref 后删除（软删）怎么办？→ 软删保留 audit 完整性，UI 标注「事实源已归档」
- **R3**：decision / memory_proposal / execution 跨项目可见性 → E6 permission 控制

## 10. 实施顺序（M2）

1. PG schema（v0.4：content 改 projection）
2. seq 分配事务 + POST /messages
3. WS 网关 + `message.created` 广播
4. **v0.4 新增** GET /decisions/:id /executions/:id /memory-proposals/:id
5. resume 协议
6. 附件预签名
7. P5 Channel 前端（v0.5 回修）
8. E2E 套件
