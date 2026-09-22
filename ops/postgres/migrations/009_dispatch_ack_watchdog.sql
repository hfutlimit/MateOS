-- =============================================================================
-- MateOS · 009 · E7 dispatch_ack watchdog 字段
-- -----------------------------------------------------------------------------
-- 依据：detailed/03 §7.1 + detailed/10 §2 B2 + SYSTEM_DESIGN §5.2
--
-- watchdog worker 需要追踪：
--   · 同一 attempt 的 redispatch 次数（决定 GiveUpAndFail 阈值）
--   · 上一次 resume_request 探测时刻（决定是否进入探测→重派→放弃三段）
--   · last_redispatched_at 是审计用：worker 重派那一刻的事实（与 Runtime
--     restart planner 中的 DispatchSentAt 区分——后者只在 Runtime 进程重启时写）
--
-- V1 简化：worker 不引入新 WS 消息类型（不主动推 execution.resume_request），
-- 只做"重派同一 attempt / 超阈值放弃"两个动作；resume 探测由 Agent 在收到
-- 重复 dispatch 时按 (execution_id, attempt_no) 幂等账本自然处理
-- （detailed/10 §4：重复 dispatch 只重绑 + 再 ACK，不重新执行）。
-- =============================================================================

ALTER TABLE agent_executions
  ADD COLUMN dispatch_redispatch_count INT     NOT NULL DEFAULT 0,
  ADD COLUMN last_resume_probe_at     TIMESTAMPTZ,
  ADD COLUMN last_redispatched_at     TIMESTAMPTZ;

COMMENT ON COLUMN agent_executions.dispatch_redispatch_count IS
  'B2 watchdog worker 已重派同一 attempt 的次数。阈值（默认 3）耗尽 → Execution FAILED。';
COMMENT ON COLUMN agent_executions.last_resume_probe_at IS
  '上一次 watchdog 主动推 resume_request 探测的时刻（V1 简化版暂未使用，留作 V2 字段）。';
COMMENT ON COLUMN agent_executions.last_redispatched_at IS
  '上一次 watchdog 重派 dispatch 的时刻（审计用；不是 Runtime restart planner 用的 dispatch_sent_at）。';

-- worker 扫表索引：PENDING/RUNNING + 未 ACK + 已超 ack_wait
CREATE INDEX idx_agent_executions_ack_watchdog
  ON agent_executions(status, dispatch_sent_at)
  WHERE status IN ('PENDING','RUNNING')
    AND dispatch_acked_at IS NULL;
