-- =============================================================================
-- MateOS · 003 · E2 Agent Registry
-- -----------------------------------------------------------------------------
-- 权威来源：
--   · E2 epic §3 DDL（credentials / agents / agent_project_membership / agent_tokens）
--   · E2 epic §3.1 lifecycle × activity 6 态
--   · E2 epic §3.2 5 个 canonical capability key
--   · E2 epic §3.3 AES-256-GCM 信封（nonce(12) || ciphertext || tag(16) = 28 字节 + 实际密文）
--   · SYSTEM_DESIGN v0.9 §3 agent-registry
--
-- 与 E2 DDL 的收紧（不改语义）：
--   · agents 删 active_slots 字段（归 Redis 持有，E2 §3 v0.4.2）
--   · credentials meta 与 agents capabilities 显式 jsonb 列
--   · agent_tokens.token_hash 显式 NOT NULL（API 返回永不返明文）
--   · agent_project_membership 复合 PK + ON DELETE CASCADE 双向
--   · 限额字段 NUMERIC(10,2) 强类型（避免浮点误差）
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Credentials（E2 §3.3 AES-256-GCM 信封）
-- -----------------------------------------------------------------------------
CREATE TABLE credentials (
  id               UUID PRIMARY KEY,
  user_id          UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  provider         TEXT NOT NULL,                  -- 'openai' | 'anthropic' | 'google' | ...
  label            TEXT NOT NULL,
  -- 密文 = nonce(12) || ciphertext || tag(16)；应用层解密
  secret_encrypted BYTEA NOT NULL,
  meta             JSONB,
  last_used_at     TIMESTAMPTZ,
  created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_credentials_user ON credentials(user_id, created_at DESC);

-- -----------------------------------------------------------------------------
-- Agents（E2 §3 lifecycle × activity 正交）
-- -----------------------------------------------------------------------------
CREATE TABLE agents (
  id                  UUID PRIMARY KEY,
  owner_user_id       UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  credential_id       UUID NOT NULL REFERENCES credentials(id),
  name                TEXT NOT NULL,
  -- role 自由文本（'coder' / 'reviewer' / 'planner' / 自定义）
  role                TEXT NOT NULL,
  -- canonical key 集合：coding / debugging / review / testing / architecture（E2 §3.2）
  capabilities        JSONB NOT NULL DEFAULT '[]'::jsonb,
  -- v0.4 拆维度：lifecycle 由 owner 控制；activity 仅做 UI derived
  lifecycle           TEXT NOT NULL DEFAULT 'ACTIVE'
                      CHECK (lifecycle IN ('ACTIVE','PAUSED','DISABLED')),
  activity            TEXT NOT NULL DEFAULT 'OFFLINE'
                      CHECK (activity IN ('OFFLINE','AVAILABLE','THINKING','WORKING','WAITING_CONTEXT','ERROR')),
  activity_reason     TEXT,
  activity_updated_at TIMESTAMPTZ,
  -- v0.4.2 改：只存 durable config；active slot 归 Redis
  max_concurrency     INT NOT NULL DEFAULT 1 CHECK (max_concurrency > 0),
  daily_limit_usd     NUMERIC(10,2) NOT NULL DEFAULT 5.00,
  monthly_budget_usd  NUMERIC(10,2) NOT NULL DEFAULT 50.00,
  -- 单台 stub 进程最近一次心跳时间（WS /api 维度使用）
  last_heartbeat_at   TIMESTAMPTZ,
  created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_agents_owner ON agents(owner_user_id, created_at DESC);
CREATE INDEX idx_agents_lifecycle_activity ON agents(lifecycle, activity);
-- 唯一：同 owner 下 agent 名称不能重
CREATE UNIQUE INDEX uq_agents_owner_name ON agents(owner_user_id, name) WHERE lifecycle <> 'DISABLED';

-- -----------------------------------------------------------------------------
-- Agent Project Membership（E2 §2.1 in scope：Agent 在 Project 内可见范围）
-- -----------------------------------------------------------------------------
-- V1 简化：单批准（project owner 邀请即可），双批准留 V2
-- can_read_history：Agent 是否可读取 project 历史（用于让 Agent 在 @mention 之前就有上下文）
CREATE TABLE agent_project_membership (
  agent_id         UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  project_id       UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  invited_by       UUID NOT NULL REFERENCES users(id),
  can_read_history BOOLEAN NOT NULL DEFAULT false,
  joined_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (agent_id, project_id)
);
CREATE INDEX idx_agent_project_membership_project
  ON agent_project_membership(project_id, joined_at);

-- -----------------------------------------------------------------------------
-- Agent Tokens（E2 §3；明文只下发一次，存 hash）
-- -----------------------------------------------------------------------------
-- token_hash = SHA-256(token_plaintext)，hex 64 字符
-- expires_at 可空：null 表示永不过期（不推荐，但留口子给 dev stub）
CREATE TABLE agent_tokens (
  id          UUID PRIMARY KEY,
  agent_id    UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  -- SHA-256 hex；明文仅在创建响应里返回一次
  token_hash  TEXT NOT NULL,
  label       TEXT,
  last_seen_at TIMESTAMPTZ,
  expires_at  TIMESTAMPTZ,
  revoked     BOOLEAN NOT NULL DEFAULT false,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX uq_agent_tokens_hash ON agent_tokens(token_hash);
CREATE INDEX idx_agent_tokens_agent_active
  ON agent_tokens(agent_id) WHERE revoked = false;
