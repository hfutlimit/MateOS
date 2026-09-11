namespace MateOS.Domain.Channel;

/// <summary>
/// WS 连接的<b>主体类型</b>（hello 帧用哪种令牌换来的会话）。
/// </summary>
/// <remarks>
/// <para>
/// 必须显式区分而不是「按 id 猜」：人 id 与 agent id 都是 UUID，同一个 GUID
/// 语义上可以同时存在一个 user 和一个 agent。若只按 id 索引会话，
/// <c>execution.dispatch</c> 有概率被推到人的浏览器上——那是把执行指令泄露给错误主体。
/// </para>
/// <para>
/// 因此注册表按 (ActorType, ActorId) 两个维度分别建索引，出站推送也只认 AGENT 索引。
/// </para>
/// </remarks>
public enum WsActorType
{
    /// <summary>人类成员（access token / token_type=access）。</summary>
    HUMAN,

    /// <summary>Agent（agent_token / token_type=agent，sub = agent_id）。</summary>
    AGENT,
}

public static class WsActorTypeMap
{
    public const string HumanValue = "HUMAN";
    public const string AgentValue = "AGENT";

    public static string ToDbValue(this WsActorType type) => type switch
    {
        WsActorType.HUMAN => HumanValue,
        WsActorType.AGENT => AgentValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 WsActorType"),
    };

    public static bool TryParse(string? value, out WsActorType type)
    {
        switch (value)
        {
            case HumanValue: type = WsActorType.HUMAN; return true;
            case AgentValue: type = WsActorType.AGENT; return true;
            default:
                type = (WsActorType)(-1);
                return false;
        }
    }
}
