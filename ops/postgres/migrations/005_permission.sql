-- =============================================================================
-- MateOS · 005 · E6 Authorization & Approval
-- -----------------------------------------------------------------------------
-- 权威来源：
--   · E6 epic §3 DDL（permissions scope_type × subject_type × perm_key × effect）
--   · E6 epic §3.1 默认权限矩阵（v0.4 改：write_memory / create_pr = REQUIRE_APPROVAL）
--   · E6 epic §3.2 REQUIRE_APPROVAL 路由（Permission 层不实现审批，业务层调）
--   · E6 epic §2.1 effect 三态：ALLOW / DENY / REQUIRE_APPROVAL（v0.4 改）
--   · detailed/07 v0.4.3：8 个 canonical perm_key（含 propose_memory，从
--     write_memory 拆出）
--
-- 与 E6 DDL 的收紧（不改语义）：
--   · 8 个 perm_key CHECK 列表（v0.4.5 = 7 个 + v0.4.5 拆出 propose_memory = 8 个）
--   · effect 三态：ALLOW / DENY / REQUIRE_APPROVAL
--   · (scope_type, scope_id, subject_type, subject_id, perm_key) 5 元唯一
--   · scope 双向 (PROJECT, CHANNEL) + subject 双向 (USER, AGENT)
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Permissions（E6 §3）
-- -----------------------------------------------------------------------------
CREATE TABLE permissions (
  id           UUID PRIMARY KEY,
  scope_type   TEXT NOT NULL CHECK (scope_type IN ('PROJECT','CHANNEL')),
  scope_id     UUID NOT NULL,
  subject_type TEXT NOT NULL CHECK (subject_type IN ('USER','AGENT')),
  subject_id   UUID NOT NULL,
  -- v0.4.5 8 键：read_message / write_message / propose_memory / write_memory /
  --              execute_code / create_pr / approve_memory / manage_channel
  perm_key     TEXT NOT NULL CHECK (perm_key IN (
                  'read_message','write_message','propose_memory','write_memory',
                  'execute_code','create_pr','approve_memory','manage_channel')),
  -- v0.4 effect 三态（替换 v0.3 REQUEST）
  effect       TEXT NOT NULL CHECK (effect IN ('ALLOW','DENY','REQUIRE_APPROVAL')),
  created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- 5 元唯一：同一 (scope, subject, perm) 只一条 override
  UNIQUE (scope_type, scope_id, subject_type, subject_id, perm_key)
);

CREATE INDEX idx_perm_scope ON permissions(scope_type, scope_id);
CREATE INDEX idx_perm_subject ON permissions(subject_type, subject_id);
-- 加速单 key 查询（E6 §5 check() 热路径：scope 拉一次，subject 也拉一次）
CREATE INDEX idx_perm_scope_subject ON permissions(scope_type, scope_id, subject_type, subject_id);
