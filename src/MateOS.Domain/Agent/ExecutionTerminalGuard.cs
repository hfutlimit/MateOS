namespace MateOS.Domain.Agent;

/// <summary>
/// Execution / Attempt 终态 CAS 守卫（detailed/01 §1 T+20 / detailed/03 §5）。
/// </summary>
/// <remarks>
/// <para>
/// 终态守卫的核心：<c>UPDATE ... WHERE status IN ('PENDING','RUNNING')</c>。
/// 若 rows_affected = 0，说明已终态（重复 result / 迟到 result），按幂等策略：
/// <list type="bullet">
///   <item>同一 envelope.id 重复推 → 忽略（v0.5 §5.3 协议级去重）</item>
///   <item>新 envelope.id 但 status 已终态 → 记 metric + 忽略（v0.5 §5.3 业务级去重）</item>
/// </list>
/// </para>
/// <para>
/// 本类只放「终态是否可写入」的纯函数判定（与 <c>UPDATE</c> 同语义）；DB 写入由 Api 层
/// 用 <c>ExecuteUpdate</c> / <c>SaveChanges</c> 实现 + 校验 <c>rows_affected</c>。
/// </para>
/// </remarks>
public static class ExecutionTerminalGuard
{
    /// <summary>判定当前状态 → 目标终态的迁移是否合法。</summary>
    public static bool CanTransitionToTerminal(ExecutionStatus current)
    {
        return current is ExecutionStatus.PENDING or ExecutionStatus.RUNNING;
    }

    /// <summary>Attempt 状态迁移判定。</summary>
    public static bool CanAttemptTransitionToTerminal(AttemptStatus current)
    {
        return current is AttemptStatus.PENDING
              or AttemptStatus.DISPATCHED
              or AttemptStatus.RUNNING;
    }

    /// <summary>字符串错误信息：行级幂等的「为什么不抛错」说明。</summary>
    public static string? WhyIgnoringIdempotentResult(ExecutionStatus current, string envelopeId)
    {
        if (!current.IsTerminal())
        {
            return null; // 非终态 → 正常路径
        }

        // 已终态 + 重复 envelope.id → 静默忽略（v0.5 §5.3）
        return $"execution 已处于 {current.ToDbValue()} 终态；envelope.id={envelopeId} 视为重复投递，已忽略";
    }
}
