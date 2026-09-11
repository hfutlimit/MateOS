-- =============================================================================
-- MateOS · 004 · E7 Agent Execution & Dispatch
-- -----------------------------------------------------------------------------
-- 权威来源：
--   · detailed/01（Single Agent Task Lifecycle）v0.4.3 修正：Execution 在
--     collaboration.decision=ACCEPT 之后由 E4 同步直调 E7 创建（outbox 兜底）
--   · detailed/03 §2 完整 WS 协议 + §3 resume + §4 Runtime restart 行为
--   · detailed/03 §5 Idempotency 三层（协议 envelope.id / 业务 provider_event_id+seq /
--     terminal_envelope_id via CAS）
--   · E2 epic §3.1 lifecycle × activity 6 态
--   · v0.4.3：contiguous cursor（last_persisted_seq 严格不跳号）+ dispatch_acked_at
--     区分 PENDING（重派） vs RUNNING（等 resume）
--   · v0.7：dispatch_ack 是 transport 收据，不表达业务接受（业务接受在
--     collaboration.decision=ACCEPT 阶段已完成；CR 是终态，UNIQUE(CR) 保证 1 个
--     CR 只创建 1 个 Execution）
--
-- 与 detailed/01+03 DDL 的收紧（不改语义）：
--   · agent_executions：加 `idempotency_key` 唯一索引（防 E4 → E7 重发同 CR）
--   · `collaboration_request_id` 唯一约束（一个 CR 只一个 Execution；v0.4.3 关键）
--     V1 简化：collaboration_request_id 暂可为 NULL（手动 dispatch 不走 CR 路径）
--   · execution_attempts：attempt_no 从 1 起；status 4 态（PENDING/RUNNING/COMPLETED/FAILED）
--   · execution_events：UNIQUE(attempt_id, provider_event_id) + UNIQUE(attempt_id, seq)
--     强制 contiguous cursor（v0.4.2 bug：MAX(seq) 跳号丢事件）
--   · agent_dispatch_inbox：idempotency_key 唯一（E4 → E7 重发去重；详设 03 §7.1）
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Agent Executions（E7 顶层实体）
-- -----------------------------------------------------------------------------
CREATE TABLE agent_executions (
  id                       UUID PRIMARY KEY,
  -- 一个 CR 只能创建一个 Execution（v0.4.3 关键）
  -- V1 简化：允许 NULL（手动 dispatch 路径）；V2 E4 Resolver 上线后改为 NOT NULL
  collaboration_request_id UUID,
  agent_id                 UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  work_item_ref            UUID,
  -- 4 状态：PENDING（已创建未 dispatch）→ RUNNING（已 dispatch + acked）→ 终态（SUCCEEDED / FAILED / CANCELLED）
  status                   TEXT NOT NULL DEFAULT 'PENDING'
                           CHECK (status IN ('PENDING','RUNNING','SUCCEEDED','FAILED','CANCELLED')),
  -- v0.4.3：dispatch 发送时间（runtime restart 判定 PENDING 重派用）
  dispatch_sent_at         TIMESTAMPTZ,
  -- v0.4.3：Agent 回 dispatch_ack 的时间（与 dispatch_sent_at 共同决定 PENDING vs RUNNING）
  dispatch_acked_at        TIMESTAMPTZ,
  -- v0.4.3 contiguous cursor：实际写入 events 的最末 seq（严格不跳号）
  last_persisted_seq       BIGINT NOT NULL DEFAULT 0,
  -- v0.5：终态用 envelope.id 做幂等（CAS 已经够，terminal_envelope_id 留作审计）
  terminal_envelope_id     TEXT,
  -- attempt 索引（v0.4.3：active_attempt_no NULL 时表示终态）
  active_attempt_no        INT,
  attempt_count            INT NOT NULL DEFAULT 0,
  -- input + context 都是结构化 JSON
  input                    JSONB NOT NULL,
  context_refs             JSONB,
  -- 终态结果
  result_output            JSONB,
  result_usage             JSONB,
  -- 时间戳
  created_at               TIMESTAMPTZ NOT NULL DEFAULT now(),
  started_at               TIMESTAMPTZ,
  completed_at             TIMESTAMPTZ,
  deadline_at              TIMESTAMPTZ
);

-- v0.4.3：UNIQUE(collaboration_request_id) —— 一个 CR 只一个 Execution
-- 部分唯一索引（V1 允许 collaboration_request_id 为 NULL）
CREATE UNIQUE INDEX uq_agent_executions_collab
  ON agent_executions(collaboration_request_id) WHERE collaboration_request_id IS NOT NULL;

CREATE INDEX idx_agent_executions_agent_active
  ON agent_executions(agent_id, status, created_at DESC)
  WHERE status IN ('PENDING','RUNNING');

CREATE INDEX idx_agent_executions_status
  ON agent_executions(status, created_at DESC);

-- -----------------------------------------------------------------------------
-- Execution Attempts（E7 一次执行可有多次 attempt；V1 简化为单 attempt）
-- -----------------------------------------------------------------------------
CREATE TABLE execution_attempts (
  id                  UUID PRIMARY KEY,
  execution_id        UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_no          INT NOT NULL CHECK (attempt_no > 0),
  -- 6 状态：PENDING（已创建未 dispatch） / DISPATCHED（已 dispatch 未 ack） /
  --         RUNNING（已 ack） / COMPLETED（终态 succeeded） / FAILED（终态 failed） / CANCELLED
  status              TEXT NOT NULL DEFAULT 'PENDING'
                      CHECK (status IN ('PENDING','DISPATCHED','RUNNING','COMPLETED','FAILED','CANCELLED')),
  -- v0.4.3：Runtime restart 时按 dispatch_acked_at 区分
  --   · NULL → Agent 没收到 → 可重派
  --   · 非 NULL → Agent 已收到但还没收尾 → 等 resume_request
  dispatch_sent_at    TIMESTAMPTZ,
  dispatch_acked_at   TIMESTAMPTZ,
  started_at          TIMESTAMPTZ,
  completed_at        TIMESTAMPTZ,
  -- v0.4.3 contiguous cursor：每 attempt 独立维护
  last_persisted_seq  BIGINT NOT NULL DEFAULT 0,
  -- runtime session id（runtime restart 时用于恢复）
  runtime_session_id  TEXT,
  -- 终态错误码（如 PROVIDER_5XX / SANDBOX_INIT_FAILED / DEADLINE_EXCEEDED）
  terminal_error_code TEXT,
  terminal_message    TEXT,
  created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (execution_id, attempt_no)
);

CREATE INDEX idx_execution_attempts_status
  ON execution_attempts(status, created_at DESC);

-- -----------------------------------------------------------------------------
-- Execution Events（agent 产出的事件流；详细设计 03 §3 cursor 强制 contiguous）
-- -----------------------------------------------------------------------------
CREATE TABLE execution_events (
  id                UUID PRIMARY KEY,
  attempt_id        UUID NOT NULL REFERENCES execution_attempts(id) ON DELETE CASCADE,
  event_type        TEXT NOT NULL CHECK (event_type IN
                      ('STDOUT','PROGRESS','TOOL_CALL','LLM_TICK','ARTIFACT','ERROR')),
  -- v0.4.2 dedup：UNIQUE(attempt_id, provider_event_id)
  provider_event_id TEXT NOT NULL,
  -- v0.4.3 contiguous cursor：UNIQUE(attempt_id, seq) —— 严格不跳号
  seq               BIGINT NOT NULL CHECK (seq > 0),
  payload           JSONB NOT NULL,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (attempt_id, provider_event_id),
  UNIQUE (attempt_id, seq)
);

CREATE INDEX idx_execution_events_attempt_seq
  ON execution_events(attempt_id, seq);

-- -----------------------------------------------------------------------------
-- Agent Dispatch Inbox（E4 → E7 重发去重；v0.5 §7.1）
-- -----------------------------------------------------------------------------
CREATE TABLE agent_dispatch_inbox (
  id                 UUID PRIMARY KEY,
  idempotency_key    TEXT NOT NULL,
  agent_id           UUID NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
  execution_id       UUID NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
  attempt_no         INT NOT NULL,
  dispatched_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- 同一 idempotency_key 是否已成功（v0.5 §7.1 兜底）
  acked_at           TIMESTAMPTZ,
  UNIQUE (idempotency_key)
);
CREATE INDEX idx_agent_dispatch_inbox_agent
  ON agent_dispatch_inbox(agent_id, dispatched_at DESC);
