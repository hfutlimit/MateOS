-- =============================================================================
-- MateOS · 008 · E8 Work Management Core（S3）
-- -----------------------------------------------------------------------------
-- 权威来源：
--   · E8 epic §3 DDL（work_items / work_item_bindings / work_management_connections
--     / work_comments / work_relations）
--   · detailed/06 §2.3 + §7.1（v0.4.3：work_items.binding_id 是路由唯一事实源）
--   · detailed/06 §6.2（v0.4.3：webhook 独立表，挂 binding 维度）
--   · detailed/09 §9（v0.4.3 必须落地的 schema migration）
--
-- 与 E8 §3 的收紧（不改语义）：
--   · search_text 从 tsvector 改为 TEXT + 表达式 GIN 索引
--     （与 007 memory_items 同口径：V1 简化，V2 换 pgvector embedding）
--   · work_management_connections 不再持有 webhook_* 字段
--     （v0.4.3 把 webhook 拆到 work_management_webhooks，connection 只表 tenant + OAuth）
--   · work_items 直接带 binding_id（NOT NULL），不再有 provider_key 维度的唯一索引
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Provider Connection（v0.4.2：Org/Owner 级，多 Project 复用同一 Jira Site）
-- -----------------------------------------------------------------------------
CREATE TABLE work_management_connections (
  id                        UUID PRIMARY KEY,
  provider_key              TEXT NOT NULL,
  org_id                    UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
  owner_user_id             UUID NOT NULL REFERENCES users(id),
  display_label             TEXT,
  access_token_encrypted    BYTEA,
  refresh_token_encrypted   BYTEA,
  expires_at                TIMESTAMPTZ,
  meta                      JSONB,
  created_at                TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_wmc_org ON work_management_connections(org_id);
CREATE INDEX idx_wmc_owner ON work_management_connections(owner_user_id);
CREATE INDEX idx_wmc_provider ON work_management_connections(provider_key);

-- -----------------------------------------------------------------------------
-- Provider Binding（Project × Provider；同一 project 同 provider_key 只一条）
-- -----------------------------------------------------------------------------
-- v0.4.1 不变量：同 project 只能有一个 is_active=true 的 binding（partial unique）。
CREATE TABLE work_item_bindings (
  id                   UUID PRIMARY KEY,
  project_id           UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  provider_key         TEXT NOT NULL,
  -- BuiltInProvider 不需要 connection
  connection_id        UUID REFERENCES work_management_connections(id),
  external_project_ref TEXT,
  settings             JSONB,
  is_active            BOOLEAN NOT NULL DEFAULT true,
  created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (project_id, provider_key)
);
CREATE UNIQUE INDEX uq_project_active_work_provider
  ON work_item_bindings(project_id) WHERE is_active = true;

-- -----------------------------------------------------------------------------
-- WorkItem（v0.4.1 单一表：Built-in 是 source of truth；Jira 时也是本地同步表示）
-- -----------------------------------------------------------------------------
-- binding_id 是路由的唯一事实源（detailed/06 §2.2）：
--   CREATE → project.active_binding
--   UPDATE → work_item.binding（不看 active binding，切 Provider 后旧 WorkItem 路由稳定）
CREATE TABLE work_items (
  id                        UUID PRIMARY KEY,
  project_id                UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  type                      TEXT NOT NULL CHECK (type IN ('TASK','STORY','BUG','EPIC')),
  title                     TEXT NOT NULL,
  description               TEXT,
  status                    TEXT NOT NULL DEFAULT 'OPEN'
                            CHECK (status IN ('OPEN','IN_PROGRESS','IN_REVIEW','DONE','CLOSED')),
  canonical_status_category TEXT NOT NULL DEFAULT 'TODO'
                            CHECK (canonical_status_category IN ('TODO','IN_PROGRESS','DONE')),
  assignee_type             TEXT CHECK (assignee_type IN ('HUMAN','AGENT')),
  assignee_id               UUID,
  due_at                    TIMESTAMPTZ,
  created_by_type           TEXT NOT NULL CHECK (created_by_type IN ('USER','AGENT','SYSTEM')),
  created_by_id             UUID NOT NULL,
  -- v0.4.3：binding_id 是路由事实源
  binding_id                UUID NOT NULL REFERENCES work_item_bindings(id),
  provider_key              TEXT NOT NULL DEFAULT 'builtin',  -- 冗余字段，便于查询
  external_ref              TEXT,
  external_url              TEXT,
  provider_status           TEXT,
  provider_updated_at       TIMESTAMPTZ,
  provider_meta             JSONB,
  search_text               TEXT NOT NULL DEFAULT '',
  created_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- assignee_type / assignee_id 必须成对
  CONSTRAINT chk_work_item_assignee CHECK (
    (assignee_type IS NULL AND assignee_id IS NULL)
    OR (assignee_type IS NOT NULL AND assignee_id IS NOT NULL)
  )
);
CREATE INDEX idx_work_items_project_status ON work_items(project_id, status);
CREATE INDEX idx_work_items_project_type ON work_items(project_id, type);
CREATE INDEX idx_work_items_assignee
  ON work_items(assignee_type, assignee_id) WHERE assignee_id IS NOT NULL;
CREATE INDEX idx_work_items_due ON work_items(due_at) WHERE due_at IS NOT NULL;
CREATE INDEX idx_work_items_binding ON work_items(binding_id);
-- V1 简化：表达式 GIN（与 007 memory_items 同口径）
CREATE INDEX idx_work_items_search
  ON work_items USING GIN (to_tsvector('simple', search_text));
-- v0.4.3 改：唯一约束以 binding 维度（同一 provider 跨 binding 时 identity 不撞）
CREATE UNIQUE INDEX uq_work_items_binding_external
  ON work_items(binding_id, external_ref) WHERE external_ref IS NOT NULL;

-- -----------------------------------------------------------------------------
-- WorkComment
-- -----------------------------------------------------------------------------
CREATE TABLE work_comments (
  id           UUID PRIMARY KEY,
  work_item_id UUID NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
  author_type  TEXT NOT NULL CHECK (author_type IN ('HUMAN','AGENT','SYSTEM')),
  author_id    UUID,
  body         TEXT NOT NULL,
  created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_work_comments_item ON work_comments(work_item_id, created_at);

-- -----------------------------------------------------------------------------
-- WorkRelation
-- -----------------------------------------------------------------------------
CREATE TABLE work_relations (
  id                UUID PRIMARY KEY,
  from_work_item_id UUID NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
  to_work_item_id   UUID NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
  relation_type     TEXT NOT NULL
                    CHECK (relation_type IN ('BLOCKS','BLOCKED_BY','RELATES_TO','PARENT_OF','CHILD_OF')),
  created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (from_work_item_id, to_work_item_id, relation_type),
  CHECK (from_work_item_id <> to_work_item_id)
);
CREATE INDEX idx_work_relations_from ON work_relations(from_work_item_id);
CREATE INDEX idx_work_relations_to ON work_relations(to_work_item_id);

-- -----------------------------------------------------------------------------
-- Webhook 注册（v0.4.3：独立表，挂 binding 维度；E9 Jira 消费，S3 只落 schema）
-- -----------------------------------------------------------------------------
CREATE TABLE work_management_webhooks (
  id                  UUID PRIMARY KEY,
  binding_id          UUID NOT NULL REFERENCES work_item_bindings(id) ON DELETE CASCADE,
  connection_id       UUID NOT NULL REFERENCES work_management_connections(id),
  external_webhook_id TEXT NOT NULL,
  filter_jql          TEXT,
  filter_events       JSONB,
  expires_at          TIMESTAMPTZ NOT NULL,
  last_refreshed_at   TIMESTAMPTZ,
  refresh_status      TEXT,
  last_error          TEXT,
  created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (external_webhook_id)
);
CREATE INDEX idx_webhook_external ON work_management_webhooks(external_webhook_id);
CREATE INDEX idx_webhook_expires ON work_management_webhooks(expires_at)
  WHERE refresh_status = 'OK';
