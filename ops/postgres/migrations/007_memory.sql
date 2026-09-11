-- =============================================================================
-- MateOS · 007 · E5 Shared Memory
-- -----------------------------------------------------------------------------
-- 权威来源：
--   · E5 epic §3 DDL（memory_proposals + memory_items + 4 类型 + Source 三件套）
--   · E5 §2.1 v0.4 拆表（proposals 申请 / items 已批准）
--   · E5 §3 v0.5 scope_type 生成列（DM-I4：scope 不应与 type 漂移）
--   · E5 §3 Source CHECK（CHANNEL_MESSAGE 必填 channel_id + seq）
--   · E5 §3 scope_target CHECK（PERSONAL 不挂 project + 必须 owner_user_id）
--
-- 与 E5 DDL 的收紧（不改语义）：
--   · search_text 列：V1 简化用 text[] to_tsvector；V2 加 pgvector embedding
--   · memory_items.proposal_id UNIQUE（v0.4.3 加，v0.4 起不可绕过）
--   · memory_items.version 从 1 起（V2 自动修订时自增）
--   · proposals source_type 6 态：CHANNEL_MESSAGE / EXECUTION_RESULT /
--     DECISION / WORK_ITEM / API / MANUAL
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Memory Proposals（v0.4 拆出：申请阶段的事实源）
-- -----------------------------------------------------------------------------
-- type 4 态：PERSONAL（仅 user owner）/ PROJECT（挂在 project）/ DECISION（已批准后归档）/ KNOWLEDGE
-- scope_type 由 type 派生：PERSONAL → 'PERSONAL'，其他 → 'PROJECT'
CREATE TABLE memory_proposals (
  id                   UUID PRIMARY KEY,
  project_id           UUID REFERENCES projects(id) ON DELETE CASCADE,
  type                 TEXT NOT NULL CHECK (type IN ('PERSONAL','PROJECT','DECISION','KNOWLEDGE')),
  scope_type           TEXT GENERATED ALWAYS AS
                       (CASE WHEN type = 'PERSONAL' THEN 'PERSONAL' ELSE 'PROJECT' END) STORED,
  title                TEXT NOT NULL,
  content              TEXT NOT NULL,
  status               TEXT NOT NULL DEFAULT 'PROPOSED'
                       CHECK (status IN ('PROPOSED','APPROVED','REJECTED','WITHDRAWN')),
  -- Source 三件套（强约束）
  source_type          TEXT NOT NULL CHECK (source_type IN
                         ('CHANNEL_MESSAGE','EXECUTION_RESULT','DECISION','WORK_ITEM','API','MANUAL')),
  source_channel_id    UUID REFERENCES channels(id),
  source_message_seq   BIGINT,
  source_message_id    UUID,
  -- 申请人（PERSONAL → user；PROJECT/DECISION/KNOWLEDGE → user 或 agent）
  proposed_by_agent_id UUID REFERENCES agents(id),
  proposed_by_user_id  UUID REFERENCES users(id),
  -- 审批
  approved_by          UUID REFERENCES users(id),
  rejected_by          UUID REFERENCES users(id),
  reject_reason        TEXT,
  -- 幂等（同 idempotency_key 重发去重）
  idempotency_key      TEXT NOT NULL,
  created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
  approved_at          TIMESTAMPTZ,
  -- CHECK 约束
  CONSTRAINT chk_memory_proposal_source CHECK (
    (source_type = 'CHANNEL_MESSAGE' AND source_channel_id IS NOT NULL AND source_message_seq IS NOT NULL)
    OR (source_type <> 'CHANNEL_MESSAGE')
  ),
  CONSTRAINT chk_memory_proposal_scope_target CHECK (
    (type = 'PERSONAL' AND project_id IS NULL AND proposed_by_user_id IS NOT NULL AND proposed_by_agent_id IS NULL)
    OR (type <> 'PERSONAL' AND project_id IS NOT NULL AND (proposed_by_user_id IS NOT NULL OR proposed_by_agent_id IS NOT NULL))
  ),
  UNIQUE (idempotency_key)
);
CREATE INDEX idx_memory_proposal_project_status
  ON memory_proposals(project_id, status, created_at DESC);
CREATE INDEX idx_memory_proposal_proposer
  ON memory_proposals(proposed_by_user_id, created_at DESC) WHERE proposed_by_user_id IS NOT NULL;

-- -----------------------------------------------------------------------------
-- Memory Items（v0.4 拆出：已批准的事实源）
-- -----------------------------------------------------------------------------
-- memory_items.proposal_id UNIQUE：v0.4.3 加，v0.4 起不可绕过（直接写 item 不可能）
CREATE TABLE memory_items (
  id                 UUID PRIMARY KEY,
  proposal_id        UUID NOT NULL UNIQUE REFERENCES memory_proposals(id),
  project_id         UUID REFERENCES projects(id) ON DELETE CASCADE,
  owner_user_id      UUID REFERENCES users(id),
  type               TEXT NOT NULL CHECK (type IN ('PERSONAL','PROJECT','DECISION','KNOWLEDGE')),
  scope_type         TEXT GENERATED ALWAYS AS
                     (CASE WHEN type = 'PERSONAL' THEN 'PERSONAL' ELSE 'PROJECT' END) STORED,
  title              TEXT NOT NULL,
  content            TEXT NOT NULL,
  -- V1 简化：tsvector 全文检索（V2 升 pgvector embedding）
  search_text        TEXT NOT NULL DEFAULT '',
  source_type        TEXT NOT NULL,
  source_channel_id  UUID REFERENCES channels(id),
  source_message_seq BIGINT,
  approved_by        UUID NOT NULL REFERENCES users(id),
  version            INT NOT NULL DEFAULT 1,
  created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT chk_memory_item_scope_owner CHECK (
    (type = 'PERSONAL' AND project_id IS NULL AND owner_user_id IS NOT NULL)
    OR (type <> 'PERSONAL' AND project_id IS NOT NULL)
  )
);
CREATE INDEX idx_memory_item_project
  ON memory_items(project_id, created_at DESC);
CREATE INDEX idx_memory_item_owner
  ON memory_items(owner_user_id, created_at DESC) WHERE owner_user_id IS NOT NULL;
-- V1 简化：LIKE 检索（小数据量 OK；V2 升 tsvector GIN）
CREATE INDEX idx_memory_item_search
  ON memory_items USING GIN (to_tsvector('simple', search_text));
