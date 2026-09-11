namespace MateOS.Domain.Agent;

/// <summary>
/// Agent Capability 5 canonical key（E2 §3.2）。
/// </summary>
/// <remarks>
/// <para>
/// "能不能做"是事实源在 Agent 表 capabilities JSONB 中（canonical key 集合）。
/// "允不允许"是 E6 Permission 管的另一维度（execution_code / create_pr / approve_memory）。
/// </para>
/// <para>
/// canonical key 收敛：v0.4 起硬编码 5 个，未来升为 DB 表（E2 R2）。
/// </para>
/// </remarks>
public static class AgentCapability
{
    public const string Coding = "coding";
    public const string Debugging = "debugging";
    public const string Review = "review";
    public const string Testing = "testing";
    public const string Architecture = "architecture";

    public static readonly IReadOnlySet<string> Canonical = new HashSet<string>(StringComparer.Ordinal)
    {
        Coding,
        Debugging,
        Review,
        Testing,
        Architecture,
    };

    /// <summary>校验输入 key 集合是否全部为 canonical（v0.4 强约束；V2 升 DB 后放宽）。</summary>
    public static string? Validate(IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> invalid = new();

        foreach (string key in capabilities)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return "capability 不能为空字符串";
            }

            if (!Canonical.Contains(key))
            {
                invalid.Add(key);
            }

            if (!seen.Add(key))
            {
                return $"capability 重复：{key}";
            }
        }

        if (invalid.Count > 0)
        {
            return $"未知 capability key：{string.Join(", ", invalid)}（canonical: {string.Join(", ", Canonical)}）";
        }

        return null;
    }

    /// <summary>判断是否覆盖某 capability（用于 E4 Resolver 过滤：required_capabilities ⊆ agent.capabilities）。</summary>
    public static bool Covers(IReadOnlyCollection<string> agentCapabilities, string required)
        => agentCapabilities.Contains(required, StringComparer.Ordinal);
}
