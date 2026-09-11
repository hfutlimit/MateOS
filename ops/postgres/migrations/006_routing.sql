-- =============================================================================
-- MateOS · 006 · E4 Collaboration & Routing
-- -----------------------------------------------------------------------------
-- 权威来源：
--   · E4 epic §3 DDL（triggers / collaboration_requests / decision_records）
--   · E4 epic §2.1 status 6 态（PENDING/ACCEPTED/REJECTED/NEED_CONTEXT/UNRESOLVED/CANCELLED）
--   · E4 epic §2.2 协议边界：collaboration.*（E4）vs execution.*（E7）
--   · detailed/01 §1 v0.4.3 关键修正：Execution 在 collaboration.decision=ACCEPT 之后
--     由 E4 同步直调 E7 创建（outbox 兜底）；CR 已是 ACCEPTED 终态，UNIQUE(CR) 1 个
--     CR 只 1 个 Execution
--   · detailed/04 §3 Resolver 选 Agent → tryAcquireSlot → send CR
--
-- 与 E4 DDL 的收紧（不改语义）：
--   · collaboration_requests 6 态（v0.4.1 收敛：PENDING/ACCEPTED/REJECTED/NEED_CONTEXT/
--     UNRESOLVED/CANCELLED）
--   · decision_records UNIQUE(collaboration_request_id) 一个 CR 一次决策
--   · triggers.trigger_type 4 态（MENTION/WORK_ITEM/API/AUTOMATION）
--   · 删 agents.active_slots（E4 v0.4.2；detail 已在 003_agent.sql 改）
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Triggers（E4 §3）
-- -----------------------------------------------------------------------------
-- trigger_type: MENTION（@） / WORK_ITEM（task） / API（外部调用） / AUTOMATION（schedule）
-- trigger_ref: JSONB 描述引用（channel_id+message_seq / work_item_ref / ...）
CREATE TABLE triggers (
  id              UUID PRIMARY KEY,
  trigger_type    TEXT NOT NULL CHECK (trigger_type IN ('MENTION','WORK_ITEM','API','AUTOMATION')),
  trigger_ref     JSONB NOT NULL,
  from_actor_type TEXT NOT NULL CHECK (from_actor_type IN ('USER','AGENT','SYSTEM')),
  from_actor_id   UUID NOT NULL,
  captured_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_triggers_captured ON triggers(captured_at DESC);
CREATE INDEX idx_triggers_type ON triggers(trigger_type, captured_at DESC);

-- -----------------------------------------------------------------------------
-- Collaboration Requests（E4 §3 + D9 口径）
-- -----------------------------------------------------------------------------
-- request_kind 4 态：MESSAGE_RESPONSE（@mention 回复）/ WORK_ITEM_EXECUTION /
--                    API_CALL（外部调用）/ AUTOMATION_RUN（定时）
-- status 6 态：PENDING / ACCEPTED / REJECTED / NEED_CONTEXT / UNRESOLVED / CANCELLED
-- slot_lease_id：v0.4.2 Redis Lua reservation id（用于 release 反查；V1 占位 NULL）
-- target_execution_id：ACCEPT 时由 E4 同步调 E7 创建 Execution 后回填
-- idempotency_key UNIQUE：避免重复创建
CREATE TABLE collaboration_requests (
  id                    UUID PRIMARY KEY,
  trigger_id            UUID NOT NULL REFERENCES triggers(id) ON DELETE CASCADE,
  trigger_type          TEXT NOT NULL,
  trigger_ref           JSONB NOT NULL,
  request_kind          TEXT NOT NULL
                        CHECK (request_kind IN ('MESSAGE_RESPONSE','WORK_ITEM_EXECUTION','API_CALL','AUTOMATION_RUN')),
  from_actor_type       TEXT NOT NULL,
  from_actor_id         UUID NOT NULL,
  target_agent_id       UUID REFERENCES agents(id),
  required_capabilities JSONB NOT NULL DEFAULT '[]'::jsonb,
  context_refs          JSONB NOT NULL DEFAULT '{}'::jsonb,
  status                TEXT NOT NULL DEFAULT 'PENDING'
                        CHECK (status IN ('PENDING','ACCEPTED','REJECTED','NEED_CONTEXT','UNRESOLVED','CANCELLED')),
  slot_lease_id         TEXT,
  target_execution_id   UUID,
  deadline_s            INT NOT NULL DEFAULT 600,
  idempotency_key       TEXT NOT NULL,
  created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
  resolved_at           TIMESTAMPTZ,
  UNIQUE (idempotency_key)
);
CREATE INDEX idx_cr_status ON collaboration_requests(status, created_at DESC);
CREATE INDEX idx_cr_target_agent ON collaboration_requests(target_agent_id, status, created_at DESC);
CREATE INDEX idx_cr_trigger ON collaboration_requests(trigger_id);

-- -----------------------------------------------------------------------------
-- Decision Records（E4 §3：ACCEPT/REJECT/NEED_CONTEXT/CANCEL 写一行）
-- -----------------------------------------------------------------------------
-- 一个 CR 一次决策（UNIQUE）；事实源（E4 写）
-- 4 决策：ACCEPT / REJECT / NEED_CONTEXT / CANCELLED（与 CR 状态机对齐）
CREATE TABLE decision_records (
  id                      UUID PRIMARY KEY,
  collaboration_request_id UUID NOT NULL REFERENCES collaboration_requests(id) ON DELETE CASCADE,
  decision                TEXT NOT NULL
                          CHECK (decision IN ('ACCEPT','REJECT','NEED_CONTEXT','CANCEL')),
  reason                  TEXT,
  needs                   JSONB,                   -- NEED_CONTEXT 时携带需求细节
  analysis_capability     BOOLEAN,                  -- 三件套 (detailed/01 §1 T+11)
  analysis_context_score  INT,                      -- 0-100
  analysis_permission     BOOLEAN,
  -- v0.4.3 关键：ACCEPT 时回填 execution_id（E4 同步调 E7 创建后回填）
  accepted_execution_id   UUID,
  actor_type              TEXT NOT NULL CHECK (actor_type IN ('AGENT','SYSTEM')),
  actor_id                UUID NOT NULL,
  decided_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (collaboration_request_id)
);
CREATE INDEX idx_decision_actor ON decision_records(actor_type, actor_id, decided_at DESC);
