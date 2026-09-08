# E3 · Channel & Messaging

| 字段 | 值 |
| --- | --- |
| Epic ID | E3 |
| 标题 | Channel & Messaging |
| 阶段 | MVP（M2） |
| 上游 | PRD v0.3 §3.1/§5 FR（基本 Chat）/ §5 FR-7 记忆申请卡 / SYSTEM_DESIGN v0.2 §3 channel 模块 / §5 messages 表 |
| 下游 | E4（消息载体）/ E5（记忆申请消息流）/ E6（scope=channel 权限）/ E8（audit 写消息） |
| 状态 | Draft |

## 1. 背景与动机

Channel 是通信边界（PRD §3.2）。所有人类消息、Agent 决策卡、Agent 输出、系统事件、记忆申请，最终都落到某个 channel 的消息流里。本 epic 解决：

- Channel 怎么建、谁能进、邀请谁
- 消息怎么存、怎么读、怎么不丢（seq + idempotency）
- 5 形态消息流的存储结构
- 附件直传链路（S3 预签名）

## 2. 范围

### 2.1 In Scope

- Channel CRUD（name / description / archived）
- Channel 成员邀请（Human + Agent，必须是该 Project 成员）
- 5 形态消息存储：HUMAN / DECISION / AGENT_OUTPUT / SYSTEM / MEMORY_REQUEST
- seq 单调递增（channel 内全局）+ 断线续传 `resume(last_seq)`
- 幂等去重（client_msg_id 唯一索引）
- 附件 S3 预签名直传
- 已读位点（last_read_seq，per member per channel）
- 软删（`deleted_at`，v0.2 起按 PRD 落实）
- P5 Channel 主界面（已有 v0.4 原型）

### 2.2 Out of Scope

- 编辑（V2 message_edits 版本表）
- 回复 / thread 模型落库（V1 用 message.parent_seq 简化，V2 独立表）
- 表情 / @ 提及点击（V2 增强）
- 私聊（V1 Non Goal，PRD §8）
- 端到端加密（V3+）

## 3. 数据模型

```sql
-- channels
CREATE TABLE channels (
  id          UUID PRIMARY KEY,
  project_id  UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  name        TEXT NOT NULL,           -- 频道名（小写、kebab-case）
  description TEXT,
  archived_at TIMESTAMPTZ,
  last_seq    BIGINT NOT NULL DEFAULT 0,
  created_at  TIMESTAMPTZ DEFAULT now()
);
CREATE UNIQUE INDEX idx_channels_project_name ON channels(project_id, name);

-- channel_members
CREATE TABLE channel_members (
  channel_id   UUID NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
  member_type  TEXT NOT NULL CHECK (member_type IN ('HUMAN','AGENT')),
  member_id    UUID NOT NULL,
  joined_at    TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (channel_id, member_type, member_id)
);
CREATE INDEX idx_channel_members_lookup ON channel_members(member_type, member_id);

-- channel_seq_counters
CREATE TABLE channel_seq_counters (
  channel_id  UUID PRIMARY KEY REFERENCES channels(id) ON DELETE CASCADE,
  next_seq    BIGINT NOT NULL DEFAULT 1
);

-- messages (按月分区)
CREATE TABLE messages (
  id             UUID NOT NULL,
  channel_id     UUID NOT NULL,
  seq            BIGINT NOT NULL,         -- channel 内单调递增
  sender_type    TEXT NOT NULL CHECK (sender_type IN ('HUMAN','AGENT','SYSTEM')),
  sender_id      UUID,                    -- SYSTEM 时 NULL
  content        JSONB NOT NULL,          -- 见 §3.1 五形态 schema
  content_type   TEXT NOT NULL,           -- 'HUMAN' | 'DECISION' | 'AGENT_OUTPUT' | 'SYSTEM' | 'MEMORY_REQUEST'
  mentions       JSONB,                   -- 解析后的 mentions 引用
  parent_seq     BIGINT,                  -- 父消息 seq（thread root）
  client_msg_id  UUID,                    -- 客户端生成的幂等键
  trace_id       TEXT,                    -- OTel trace
  created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
  deleted_at     TIMESTAMPTZ,
  PRIMARY KEY (channel_id, seq)
) PARTITION BY RANGE (created_at);

-- 按月分区
CREATE TABLE messages_2026_09 PARTITION OF messages
  FOR VALUES FROM ('2026-09-01') TO ('2026-10-01');
-- pg_cron 自动预建下 3 个月

CREATE UNIQUE INDEX idx_messages_id ON messages(channel_id, id);
CREATE UNIQUE INDEX idx_messages_client_msg_id ON messages(channel_id, client_msg_id) WHERE client_msg_id IS NOT NULL;
CREATE INDEX idx_messages_sender ON messages(channel_id, sender_type, sender_id);
CREATE INDEX idx_messages_parent ON messages(channel_id, parent_seq) WHERE parent_seq IS NOT NULL;
CREATE INDEX idx_messages_tsv ON messages USING gin(to_tsvector('simple', content::text));

-- attachments
CREATE TABLE attachments (
  id          UUID PRIMARY KEY,
  message_id  UUID NOT NULL,
  channel_id  UUID NOT NULL,
  s3_key      TEXT NOT NULL,
  size        BIGINT NOT NULL,
  mime        TEXT NOT NULL,
  created_at  TIMESTAMPTZ DEFAULT now()
);

-- read_cursors (per member per channel)
CREATE TABLE read_cursors (
  channel_id   UUID NOT NULL,
  member_type  TEXT NOT NULL,
  member_id    UUID NOT NULL,
  last_read_seq BIGINT NOT NULL DEFAULT 0,
  updated_at   TIMESTAMPTZ DEFAULT now(),
  PRIMARY KEY (channel_id, member_type, member_id)
);
```

### 3.1 content 五形态 schema

```jsonc
// HUMAN
{ "text": "...", "mentions": [...], "attachment_ids": ["..."] }

// DECISION
{
  "agent_id": "...",
  "result": "ACCEPT" | "REJECT" | "NEED_CONTEXT" | "DELEGATE",
  "analysis": { "capability": bool, "context_score": 0-100, "permission": bool },
  "reason": "...",
  "missing": ["..."],      // NEED_CONTEXT 时
  "delegate_to": "..."     // DELEGATE 时
}

// AGENT_OUTPUT
{ "agent_id": "...", "markdown": "...", "code": "...",
  "model": "...", "tokens_in": 0, "tokens_out": 0, "latency_ms": 0,
  "memory_refs": ["mem:..."]  // 使用的记忆引用
}

// SYSTEM
{ "text": "...", "actions": [{ "label": "批准", "kind": "approve_invite", "target": "..." }] }

// MEMORY_REQUEST
{ "agent_id": "...", "memory_title": "...", "memory_body": "...",
  "source_channel_id": "...", "source_seq": 42, "source_message_id": "...",
  "tags": [{"type": "PROJECT", "status": "PENDING_APPROVAL"}]
}
```

### 3.2 seq 分配（事务）

```sql
BEGIN;
UPDATE channel_seq_counters SET next_seq = next_seq + 1 WHERE channel_id = $1 RETURNING next_seq;
INSERT INTO messages (channel_id, seq, ...) VALUES ($1, $returned_seq, ...);
UPDATE channels SET last_seq = $returned_seq WHERE id = $1;
COMMIT;
```

行级锁保证 channel 内严格有序。

## 4. API

### 4.1 REST

| Method | Path | 描述 | 权限 |
| --- | --- | --- | --- |
| POST | `/projects/:pid/channels` | 建频道 | project member |
| GET | `/projects/:pid/channels` | 列表 | project member |
| GET | `/channels/:id` | 详情 | channel member |
| PATCH | `/channels/:id` | 改名/描述/归档 | channel owner |
| POST | `/channels/:id/members` | 邀请 Human / Agent | channel owner |
| DELETE | `/channels/:id/members` | 退出 / 移除 | owner 或自身 |
| POST | `/channels/:id/messages` | 发送消息 | channel member（write_message） |
| GET | `/channels/:id/messages` | 历史分页 | channel member（read_message） |
| GET | `/channels/:id/messages?since_seq=N` | 增量（断线续传） | 同上 |
| POST | `/attachments/presign` | 申请 S3 预签名 | channel member |
| POST | `/attachments/:id/confirm` | 上传完成后通知后端 | channel member |
| POST | `/channels/:id/read` | 更新已读位点 | channel member |

### 4.2 WebSocket

- `message.created` — 新消息事件（推给所有 channel member）
- `message.seq_sync` — ack 后服务端广播
- `channel.member_joined` / `channel.member_left`
- 连接级 `resume(last_seq)` 协议：客户端 reconnect 时带 last_seq，服务端补发差量

### 4.3 错误码

| HTTP | 含义 |
| --- | --- |
| 400 | content_type 不在 5 形态枚举 |
| 403 | 非 channel member |
| 409 | client_msg_id 重复（同 channel 内） |
| 413 | 附件 > 50MB |
| 422 | 消息体长度超限（HUMAN 50k 字符 / AGENT_OUTPUT 200k 字符） |

## 5. 关键流程

### 5.1 发送消息

```
1. POST /channels/:id/messages
   { client_msg_id, sender_type, sender_id, content, content_type, parent_seq?, mentions? }
2. 服务端：
   a) 校验 sender_type/sender_id 匹配当前用户
   b) 校验 mentions 中的 @ 对象都是 channel member（E4 解析）
   c) BEGIN 事务：
      - 分配 seq（行级锁）
      - INSERT messages
      - UPDATE channels.last_seq
   d) COMMIT
3. 返回 { id, seq, created_at }
4. WS 广播 message.created 给 channel 所有 online member（除发送者）
```

### 5.2 断线续传

```
客户端 reconnect：
  ws.send({ type: 'resume', last_seq: N })
服务端：
  SELECT * FROM messages WHERE channel_id = $1 AND seq > N ORDER BY seq LIMIT 500
  推送 message.created 给客户端直到 last_seq 追平
  之后正常订阅新消息
```

### 5.3 附件直传

```
1. 客户端 POST /attachments/presign
   { channel_id, filename, mime, size }
   → 校验 mime / size ≤ 50MB
   → 预签名 S3 PUT URL，过期 15min
   → 预创建 attachments 行（status=PENDING，s3_key = "tmp/{uuid}"）
2. 客户端 PUT 到 S3 URL
3. 客户端 POST /attachments/:id/confirm
   → 服务端 HEAD 检查 S3 存在 + size 一致
   → UPDATE attachments status=READY
   → 客户端后续在 messages.content.attachment_ids 引用
4. 用户读消息时后端签发 GET URL（15min）内联返回
```

### 5.4 已读位点

```
POST /channels/:id/read { last_read_seq: N }
  → UPSERT read_cursors
  → 计算 unread_count = N - last_read_seq（Redis 缓存：unread:{member_id}:{channel_id}）
  → WS 推 unread.updated 给前端（顶栏聚合）
```

## 6. UI

### 6.1 页面

- **P5 Channel 主界面**（v0.4 原型）：四栏（导航 / 频道 / 消息流 / 上下文），5 形态消息流

### 6.2 关键组件

- `<MessageBubble>`：按 content_type 渲染 5 种形态
- `<Composer>`：输入器 + @ 弹层（E4） + 附件按钮
- `<ThreadToggle>`：「展开 N 条回复」（V1 用 parent_seq 简化实现）
- `<UnreadBadge>`：侧栏频道列表上聚合

### 6.3 状态

- 消息发送中：客户端乐观显示「⌛ 发送中」，失败重试
- 附件上传：进度条
- 断线：顶栏显示「重连中…」+ 抑制消息流刷新

## 7. 验收标准

### 7.1 功能

- **F1** 发消息后 channel 所有 online member 收到 `message.created`（按 last_seq 顺序）
- **F2** 同 client_msg_id 重发 5 次：服务端只产生 1 条消息，后 4 次返 409
- **F3** seq 单调：发 1000 条后 last_seq = 1000；中间无空洞
- **F4** resume(last_seq=500) 拿回 seq 501~1000 共 500 条
- **F5** 附件 50MB 直传走 S3 预签名，服务端不代理字节
- **F6** 非 channel member 调用 POST /channels/:id/messages 返 403
- **F7** 软删后内容从 GET /messages 返回中过滤（除非带 include_deleted=true）
- **F8** 已读位点正确聚合到顶栏未读徽标

### 7.2 E2E

- `e2e/E3-001-send-receive`：A 发送 → B 收到，seq 正确
- `e2e/E3-002-idempotency`：同 client_msg_id 重复发送只入库一次
- `e2e/E3-003-resume`：客户端断线 → resume → 补发差量
- `e2e/E3-004-attachment`：上传 50MB 文件 → 服务端确认 → 客户端能下载
- `e2e/E3-005-permission-isolation`：非 member 调用返 403
- `e2e/E3-006-soft-delete`：删消息 → GET 默认不返回

### 7.3 非功能

- 发送 P95 < 100ms（不含广播）
- WS `message.created` 广播 P95 < 300ms（1k 并发）
- 1000 消息/秒单 channel 下 seq 锁竞争 < 10ms 等待

## 8. 与其他 Epic 的关系

- **被依赖**：
  - E4 Decision 消息落 DECISION 形态
  - E5 Memory 申请消息落 MEMORY_REQUEST 形态
  - E8 写消息即写 audit_logs
- **依赖**：E1（Project + Member）
- **冲突裁决**：5 形态 message.content schema 与 UI DS §5.1 / PRD FR-7 强约束

## 9. 风险与开放问题

- **R1**：seq 用 BIGINT 单调递增不会溢出（2^63/1k msg/s ≈ 292 万亿年），但分区表按月切换需要 seq 跨分区全局递增——`channel_seq_counters` 必须在所有分区可见
- **R2**：客户端时区与 created_at 显示 → MVP 用 UTC，服务端格式化 + 客户端 i18n
- **R3**：@ 提及在消息里是 raw text 还是结构化？→ 落 `mentions` JSONB 字段 + raw text 留原样（E4 解析）
- **R4**：memory_items 引用消息时，消息软删怎么办？→ 软删不影响引用（audit 完整性优先）

## 10. 实施顺序（M2）

1. PG schema（channels / members / counters / messages / attachments / cursors）
2. `messages` 分区表 + pg_cron 自动预建
3. seq 分配事务 + POST /messages
4. WS 网关 + `message.created` 广播
5. resume(last_seq) 协议
6. 附件预签名链路
7. P5 Channel 主界面前端
8. E2E 套件
