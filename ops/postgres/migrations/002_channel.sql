-- =============================================================================
-- MateOS · 002 · E3 Channel & Messaging
-- -----------------------------------------------------------------------------
-- 权威来源：
--   · E3 epic §3 数据模型（channels / channel_members / messages /
--     channel_seq_counters / attachments）
--   · E3 epic §3.1 5 形态 content schema（HUMAN / DECISION / AGENT_OUTPUT /
--     SYSTEM / MEMORY_REQUEST —— projection 模式：只引 entity_ref，不复制字段）
--   · SYSTEM_DESIGN v0.9 §5.2 messages 投影约束 + v0.4.5 删分区决定
--   · 09 §9 客户端幂等：client_msg_id 部分唯一索引（F2「同 client_msg_id 重发
--     5 次只产生 1 条」靠的就是它）
--
-- 与 E3 DDL 的收紧（不改语义）：
--   · content / mentions / provider_meta 等 JSONB 显式声明 jsonb 列类型
--   · channels.last_seq DEFAULT 0；counter 表 next_seq DEFAULT 1
--   · 复合 PK (channel_id, seq) 严格遵守 —— 与 SD §5.2 一致
--   · NOT NULL + DEFAULT now() 收紧时间戳（避免出现空时间戳）
--   · channel_seq_counters 与 channels 1:1，channel 创建时同事务初始化
--   · attachments 走独立表（不内嵌 messages.content）：跨消息复用 + 软删独立
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Channel（通信边界，PRD §1.1 / E3 §1）
-- -----------------------------------------------------------------------------
CREATE TABLE channels (
  id          UUID PRIMARY KEY,
  project_id  UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  name        TEXT NOT NULL,
  description TEXT,
  archived_at TIMESTAMPTZ,
  -- 当前最大 seq（消息流断点续传用，详情拉取时同步返回让客户端可一次性对齐）
  last_seq    BIGINT NOT NULL DEFAULT 0,
  -- 软删（audit 完整性优先；V1+ 实际很少用，先把字段留出来）
  deleted_at  TIMESTAMPTZ,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 同 project 下 channel 名称唯一（v0.4.5：与 SD §5.2 一致）
CREATE UNIQUE INDEX idx_channels_project_name
  ON channels(project_id, name) WHERE deleted_at IS NULL;

CREATE INDEX idx_channels_project_active
  ON channels(project_id, created_at DESC) WHERE deleted_at IS NULL;

-- -----------------------------------------------------------------------------
-- Channel Member（HUMAN 或 AGENT 都可加入，E3 §2.1）
-- -----------------------------------------------------------------------------
CREATE TABLE channel_members (
  channel_id   UUID NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
  member_type  TEXT NOT NULL CHECK (member_type IN ('HUMAN','AGENT')),
  member_id    UUID NOT NULL,
  -- owner 可管理 channel（踢人 / 归档 / 改 info），member 只能发消息与读
  role         TEXT NOT NULL DEFAULT 'member'
               CHECK (role IN ('owner','member')),
  joined_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (channel_id, member_type, member_id)
);

CREATE INDEX idx_channel_members_human
  ON channel_members(member_id) WHERE member_type = 'HUMAN';
CREATE INDEX idx_channel_members_agent
  ON channel_members(member_id) WHERE member_type = 'AGENT';

-- -----------------------------------------------------------------------------
-- Channel Seq Counter（每 channel 一行；事务内 UPDATE ... RETURNING 保证单调递增）
-- -----------------------------------------------------------------------------
CREATE TABLE channel_seq_counters (
  channel_id  UUID PRIMARY KEY REFERENCES channels(id) ON DELETE CASCADE,
  next_seq    BIGINT NOT NULL DEFAULT 1
);

-- -----------------------------------------------------------------------------
-- Messages（v0.4：5 形态 projection；DECISION / AGENT_OUTPUT / MEMORY_REQUEST
-- 只引 entity_ref，事实源在各自表中 —— 见 E3 §3.1）
-- -----------------------------------------------------------------------------
CREATE TABLE messages (
  id             UUID NOT NULL,
  channel_id     UUID NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
  -- 同一 channel 内单调递增；PK (channel_id, seq) 保证唯一
  seq            BIGINT NOT NULL,
  sender_type    TEXT NOT NULL CHECK (sender_type IN ('HUMAN','AGENT','SYSTEM')),
  sender_id      UUID,
  -- 5 形态投影：内容只引 entity_ref（事实源在 decision_records /
  -- agent_executions / memory_proposals）
  content_type   TEXT NOT NULL CHECK (content_type IN
                    ('HUMAN','DECISION','AGENT_OUTPUT','SYSTEM','MEMORY_REQUEST')),
  -- projection 内容：HUMAN/SYSTEM 形态可含 text / mentions / actions；
  -- 其余 3 形态只存 entity_ref + summary（详情走事实源 endpoint）
  content        JSONB NOT NULL,
  -- 预提取 mentions（E4 mention 解析回填；E3 仅写，E4 读后填）
  mentions       JSONB,
  -- v0.4 简化：thread 用 parent_seq，V2 再独立成表
  parent_seq     BIGINT,
  -- v0.5 客户端幂等键：可空 + 部分唯一索引允许历史/系统消息无 id
  client_msg_id  UUID,
  trace_id       TEXT,
  created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
  deleted_at     TIMESTAMPTZ,
  PRIMARY KEY (channel_id, seq)
);

-- v0.4.5：删除 PARTITION BY RANGE(created_at)
--   (1) PG 16 声明式分区唯一约束必须包含分区键，PK (channel_id, seq) 建不出来
--   (2) EF Core migrations 管不了声明式分区，反复想删
-- 替代：保留 PK (channel_id, seq) + 按月归档 job（V1+ 评估）
CREATE INDEX idx_messages_channel_time
  ON messages(channel_id, created_at DESC) WHERE deleted_at IS NULL;

-- 客户端幂等（F2）：同 client_msg_id 重发只产生 1 条
-- 可空列 + 部分唯一索引：未带 client_msg_id 的历史/系统消息不受约束
CREATE UNIQUE INDEX uq_messages_client_msg
  ON messages(channel_id, client_msg_id)
  WHERE client_msg_id IS NOT NULL AND deleted_at IS NULL;

-- -----------------------------------------------------------------------------
-- Attachments（独立表，跨消息可复用：同一 S3 key 可被多条消息引用）
-- -----------------------------------------------------------------------------
CREATE TABLE attachments (
  id            UUID PRIMARY KEY,
  -- 谁上传的（HUMAN 或 AGENT）
  uploader_type TEXT NOT NULL CHECK (uploader_type IN ('HUMAN','AGENT')),
  uploader_id   UUID NOT NULL,
  -- S3 / MinIO key（presign 时签发）
  s3_key        TEXT NOT NULL,
  -- presign → 上传 → confirm 状态机
  status        TEXT NOT NULL DEFAULT 'PRESIGNED'
                CHECK (status IN ('PRESIGNED','UPLOADED','FAILED')),
  file_name     TEXT NOT NULL,
  mime_type     TEXT,
  size_bytes    BIGINT,
  -- 消息引用计数（清理孤儿 attachment 用，非事实源）
  ref_count     INT NOT NULL DEFAULT 0,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  confirmed_at  TIMESTAMPTZ,
  deleted_at    TIMESTAMPTZ
);
CREATE INDEX idx_attachments_uploader
  ON attachments(uploader_type, uploader_id, created_at DESC);
-- presigned 超过 24h 未 confirm 的清理
CREATE INDEX idx_attachments_orphaned
  ON attachments(created_at) WHERE status = 'PRESIGNED';
