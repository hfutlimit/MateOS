namespace MateOS.Domain.Routing;

/// <summary>
/// CR 状态机（E4 §2.1 v0.4.1）。
/// </summary>
/// <remarks>
/// <para>
/// 合法迁移：
/// <list type="bullet">
///   <item>PENDING → ACCEPTED（决策 = ACCEPT）</item>
///   <item>PENDING → REJECTED（决策 = REJECT）</item>
///   <item>PENDING → NEED_CONTEXT（决策 = NEED_CONTEXT）</item>
///   <item>PENDING → CANCELLED（决策 = CANCEL 或外部取消）</item>
///   <item>PENDING → UNRESOLVED（lease sweep 60s+）</item>
/// </list>
/// </para>
/// <para>
/// 终态（ACCEPTED / REJECTED / NEED_CONTEXT / UNRESOLVED / CANCELLED）之间不可迁移；
/// 终态 idempotency：重复写决策时，若 envelope_id 相同则静默忽略，不同则 409。
/// </para>
/// </remarks>
public static class CrStateMachine
{
    /// <summary>从 PENDING 状态到目标状态的迁移是否合法。</summary>
    public static bool CanTransition(CrStatus current, CrStatus target)
    {
        if (current != CrStatus.PENDING)
        {
            return false; // 终态不可迁移
        }

        return target is CrStatus.ACCEPTED
                      or CrStatus.REJECTED
                      or CrStatus.NEED_CONTEXT
                      or CrStatus.UNRESOLVED
                      or CrStatus.CANCELLED;
    }

    /// <summary>从 PENDING 状态迁移到目标状态（CAS 前的纯函数判定）。</summary>
    public static string? WhyCannotTransition(CrStatus current, CrStatus target)
    {
        if (current == CrStatus.PENDING && CanTransition(current, target))
        {
            return null;
        }

        if (current.IsTerminal())
        {
            return $"CR 已处于 {current.ToDbValue()} 终态；不接受新决策";
        }

        return $"CR 状态 {current.ToDbValue()} → {target.ToDbValue()} 非法迁移";
    }
}
