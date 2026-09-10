-- =============================================================================
-- MateOS · 001 · E1 Identity & Workspace
-- -----------------------------------------------------------------------------
-- 权威来源：
--   · E1 epic §3 数据模型（users / organizations / organization_members /
--     teams / team_members / projects / project_members）
--   · E10 epic §3.1 audit_logs（I10：所有写操作留审计）
--   · SYSTEM_DESIGN §5.2 outbox_events（D7：PostgreSQL transactional outbox）
--
-- 与文档的差异（均为收紧，不改语义）：
--   · created_at / joined_at 补 NOT NULL + DEFAULT now()，避免出现时间戳为空的成员行
--   · organizations 不含 owner_id（v0.4.2 删除双事实源）；owner 的唯一事实源是
--     organization_members.role='owner'
-- =============================================================================

-- 邮箱大小写不敏感唯一（E1 F8）
CREATE EXTENSION IF NOT EXISTS citext;

-- pgvector 供 E5 memory_chunks 使用；此处建好避免后续迁移要求 superuser
CREATE EXTENSION IF NOT EXISTS vector;

-- -----------------------------------------------------------------------------
-- Identity
-- -----------------------------------------------------------------------------
CREATE TABLE users (
  id              UUID PRIMARY KEY,
  email           CITEXT UNIQUE NOT NULL,
  password_hash   TEXT NOT NULL,                  -- argon2id
  display_name    TEXT NOT NULL,
  avatar_url      TEXT,
  created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- -----------------------------------------------------------------------------
-- Organization（顶层租户；不是权限作用域，权限最小到 Project）
-- -----------------------------------------------------------------------------
CREATE TABLE organizations (
  id          UUID PRIMARY KEY,
  name        TEXT NOT NULL,
  created_by  UUID REFERENCES users(id),          -- v0.4.2：仅信息性，不参与权限判定
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- v0.4：Org 成员是 owner 角色的唯一事实源
CREATE TABLE organization_members (
  organization_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
  user_id         UUID NOT NULL REFERENCES users(id),
  role            TEXT NOT NULL CHECK (role IN ('owner','member')),
  joined_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (organization_id, user_id)
);
CREATE INDEX idx_org_members_user ON organization_members(user_id);

-- -----------------------------------------------------------------------------
-- Team
-- -----------------------------------------------------------------------------
CREATE TABLE teams (
  id          UUID PRIMARY KEY,
  org_id      UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
  name        TEXT NOT NULL,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_teams_org ON teams(org_id);

CREATE TABLE team_members (
  team_id     UUID NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
  user_id     UUID NOT NULL REFERENCES users(id),
  role        TEXT NOT NULL CHECK (role IN ('owner','member')),
  joined_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (team_id, user_id)
);
CREATE INDEX idx_team_members_user ON team_members(user_id);

-- -----------------------------------------------------------------------------
-- Project（Context Boundary：成员 / 记忆 / 工作 / Provider 绑定的作用域）
-- v0.4 简化：不含 integration_backend / issue_tracker（Provider 字段归 E8 的 binding 表）
-- -----------------------------------------------------------------------------
CREATE TABLE projects (
  id          UUID PRIMARY KEY,
  team_id     UUID NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
  name        TEXT NOT NULL,
  description TEXT,
  repo_url    TEXT,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_projects_team ON projects(team_id);

CREATE TABLE project_members (
  project_id  UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  user_id     UUID NOT NULL REFERENCES users(id),
  role        TEXT NOT NULL CHECK (role IN ('owner','member')),
  joined_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (project_id, user_id)
);
CREATE INDEX idx_project_members_user ON project_members(user_id);

-- -----------------------------------------------------------------------------
-- E10 基础：审计（I10 —— 从 M1 起每个写操作都写 audit，框架统一）
-- -----------------------------------------------------------------------------
CREATE TABLE audit_logs (
  id          BIGSERIAL PRIMARY KEY,
  actor_type  TEXT NOT NULL CHECK (actor_type IN ('USER','AGENT','SYSTEM','RUNTIME','PROVIDER')),
  actor_id    UUID,
  action      TEXT NOT NULL,
  target_type TEXT,
  target_id   UUID,
  detail      JSONB,
  trace_id    TEXT,
  ip          INET,
  user_agent  TEXT,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_audit_actor_time ON audit_logs(actor_type, actor_id, created_at DESC);
CREATE INDEX idx_audit_target ON audit_logs(target_type, target_id);
CREATE INDEX idx_audit_action_time ON audit_logs(action, created_at DESC);
CREATE INDEX idx_audit_trace ON audit_logs(trace_id);

-- -----------------------------------------------------------------------------
-- Transactional outbox（D7：无独立 MQ，relay 用 SKIP LOCKED 领取）
-- event 必须与业务写在**同一事务**内，relay 只做 transport，绝不是事实源（I6）
-- -----------------------------------------------------------------------------
CREATE TABLE outbox_events (
  id              UUID PRIMARY KEY,
  aggregate_type  TEXT NOT NULL,          -- 'collaboration' | 'execution' | 'work_item' | ...
  aggregate_id    UUID NOT NULL,
  event_type      TEXT NOT NULL,          -- 'execution.completed' | 'collab.timeout' | ...
  payload         JSONB NOT NULL,
  idempotency_key TEXT UNIQUE,            -- 消费者侧去重
  status          TEXT NOT NULL DEFAULT 'PENDING'
                  CHECK (status IN ('PENDING','PUBLISHED','DEAD')),
  attempt_count   INT NOT NULL DEFAULT 0,
  next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT now(),   -- 延迟重试 / 超时的承载
  last_error      TEXT,
  published_at    TIMESTAMPTZ,
  created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_outbox_ready ON outbox_events(next_attempt_at) WHERE status = 'PENDING';
CREATE INDEX idx_outbox_aggregate ON outbox_events(aggregate_type, aggregate_id);
CREATE INDEX idx_outbox_dead ON outbox_events(created_at DESC) WHERE status = 'DEAD';
