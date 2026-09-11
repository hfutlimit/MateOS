using MateOS.Domain.Agent;

namespace MateOS.UnitTests.Agent;

/// <summary>
/// 覆盖 E2 §3.1 lifecycle × activity 6 态 + 正交不变量。
/// </summary>
public sealed class AgentLifecycleTests
{
    [Theory]
    [InlineData("ACTIVE", AgentLifecycle.Active)]
    [InlineData("PAUSED", AgentLifecycle.Paused)]
    [InlineData("DISABLED", AgentLifecycle.Disabled)]
    [InlineData("invalid", null)]
    public void AgentLifecycleMap应正确解析(string raw, AgentLifecycle? expected)
    {
        bool ok = AgentLifecycleMap.TryParse(raw, out AgentLifecycle lifecycle);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, lifecycle);
        }
    }

    [Theory]
    [InlineData(AgentLifecycle.Active, "ACTIVE")]
    [InlineData(AgentLifecycle.Paused, "PAUSED")]
    [InlineData(AgentLifecycle.Disabled, "DISABLED")]
    public void AgentLifecycleMap应正确序列化(AgentLifecycle lifecycle, string expected)
    {
        Assert.Equal(expected, lifecycle.ToDbValue());
    }

    [Theory]
    [InlineData("OFFLINE", AgentActivity.Offline)]
    [InlineData("AVAILABLE", AgentActivity.Available)]
    [InlineData("THINKING", AgentActivity.Thinking)]
    [InlineData("WORKING", AgentActivity.Working)]
    [InlineData("WAITING_CONTEXT", AgentActivity.WaitingContext)]
    [InlineData("ERROR", AgentActivity.Error)]
    [InlineData("invalid", null)]
    public void AgentActivityMap应正确解析(string raw, AgentActivity? expected)
    {
        bool ok = AgentActivityMap.TryParse(raw, out AgentActivity activity);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, activity);
        }
    }

    [Fact]
    public void ResolveActivity应保持Active_lifecycle下的activity()
    {
        Assert.Equal(AgentActivity.Available,
            AgentLifecycleActivity.ResolveActivity(AgentLifecycle.Active, AgentActivity.Available));
    }

    [Theory]
    [InlineData(AgentLifecycle.Paused)]
    [InlineData(AgentLifecycle.Disabled)]
    public void ResolveActivity应将非Active_lifecycle下的activity归一为Offline(AgentLifecycle lifecycle)
    {
        foreach (AgentActivity activity in Enum.GetValues<AgentActivity>())
        {
            Assert.Equal(AgentActivity.Offline, AgentLifecycleActivity.ResolveActivity(lifecycle, activity));
        }
    }

    [Fact]
    public void CanTransitionTo应允许Active_lifecycle下的activity变更()
    {
        Assert.Null(AgentLifecycleActivity.CanTransitionTo(AgentLifecycle.Active, AgentActivity.Working));
    }

    [Theory]
    [InlineData(AgentLifecycle.Paused)]
    [InlineData(AgentLifecycle.Disabled)]
    public void CanTransitionTo应拒绝非Active_lifecycle下的activity变更(AgentLifecycle lifecycle)
    {
        string? error = AgentLifecycleActivity.CanTransitionTo(lifecycle, AgentActivity.Working);
        Assert.NotNull(error);
        Assert.Contains(lifecycle.ToDbValue(), error);
    }
}

/// <summary>
/// 覆盖 E2 §3.2 capability 5 canonical key 校验。
/// </summary>
public sealed class AgentCapabilityTests
{
    [Fact]
    public void Validate应接受canonical集合()
    {
        Assert.Null(AgentCapability.Validate(new[] { "coding", "review" }));
    }

    [Fact]
    public void Validate应接受完整5键()
    {
        Assert.Null(AgentCapability.Validate(new[]
        {
            AgentCapability.Coding,
            AgentCapability.Debugging,
            AgentCapability.Review,
            AgentCapability.Testing,
            AgentCapability.Architecture,
        }));
    }

    [Fact]
    public void Validate应拒绝非法key()
    {
        string? error = AgentCapability.Validate(new[] { "magic" });
        Assert.NotNull(error);
        Assert.Contains("magic", error);
    }

    [Fact]
    public void Validate应拒绝空字符串()
    {
        string? error = AgentCapability.Validate(new[] { "" });
        Assert.NotNull(error);
        Assert.Contains("空字符串", error);
    }

    [Fact]
    public void Validate应拒绝重复key()
    {
        string? error = AgentCapability.Validate(new[] { "coding", "coding" });
        Assert.NotNull(error);
        Assert.Contains("重复", error);
    }

    [Fact]
    public void Validate应接受空集合()
    {
        Assert.Null(AgentCapability.Validate(Array.Empty<string>()));
    }

    [Fact]
    public void Covers应判定子集()
    {
        IReadOnlyCollection<string> agent = new[] { "coding", "review" };
        Assert.True(AgentCapability.Covers(agent, "coding"));
        Assert.True(AgentCapability.Covers(agent, "review"));
        Assert.False(AgentCapability.Covers(agent, "debugging"));
    }
}

/// <summary>
/// 覆盖 E2 §3.3 credential provider 校验 + 信封长度规则。
/// </summary>
public sealed class CredentialProviderAndCryptoTests
{
    [Theory]
    [InlineData("openai", null)]
    [InlineData("anthropic", null)]
    [InlineData("google", null)]
    [InlineData("azure", null)]
    [InlineData("bedrock", null)]
    [InlineData("magic", "未知 provider")]
    [InlineData("", "不能为空")]
    [InlineData(null, "不能为空")]
    public void CredentialProvider应校验合法与非法(string? provider, string? expectedErrorFragment)
    {
        string? error = CredentialProvider.Validate(provider);
        if (expectedErrorFragment is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.NotNull(error);
            Assert.Contains(expectedErrorFragment, error);
        }
    }

    [Fact]
    public void ValidateEnvelopeLength应接受合法长度()
    {
        // nonce + 0 字节密文 + tag = 28 字节（最小）
        Assert.Null(CredentialCrypto.ValidateEnvelopeLength(28, 1024));
    }

    [Fact]
    public void ValidateEnvelopeLength应拒绝过短()
    {
        string? error = CredentialCrypto.ValidateEnvelopeLength(20, 1024);
        Assert.NotNull(error);
        Assert.Contains("最小值", error);
    }

    [Fact]
    public void ValidateEnvelopeLength应拒绝过长()
    {
        string? error = CredentialCrypto.ValidateEnvelopeLength(2000, 1024);
        Assert.NotNull(error);
        Assert.Contains("上限", error);
    }
}

/// <summary>
/// 覆盖 matk_ token 哈希与格式校验（E2 §3 token 表）。
/// </summary>
public sealed class AgentTokenTests
{
    [Fact]
    public void Generate应返回matk_前缀和64hex后缀()
    {
        var (plaintext, jti, hashHex) = AgentToken.Generate();

        Assert.StartsWith(AgentToken.TokenPrefix, plaintext);
        Assert.Equal(AgentToken.PlaintextLength, plaintext.Length);
        Assert.Equal(AgentToken.HashHexLength, hashHex.Length);
        Assert.NotEqual(Guid.Empty, jti);
    }

    [Fact]
    public void 两次Generate应产生不同plaintext()
    {
        var (p1, _, _) = AgentToken.Generate();
        var (p2, _, _) = AgentToken.Generate();
        Assert.NotEqual(p1, p2);
    }

    [Fact]
    public void ComputeHashHex应确定性()
    {
        string h1 = AgentToken.ComputeHashHex("matk_abc");
        string h2 = AgentToken.ComputeHashHex("matk_abc");
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void ComputeHashHex应区分大小写()
    {
        string h1 = AgentToken.ComputeHashHex("matk_abc");
        string h2 = AgentToken.ComputeHashHex("MATK_ABC");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void ValidatePlaintextFormat应接受合法token()
    {
        var (plaintext, _, _) = AgentToken.Generate();
        Assert.Null(AgentToken.ValidatePlaintextFormat(plaintext));
    }

    [Theory]
    [InlineData("")]
    [InlineData("matk_")]
    [InlineData("matk_xxx")]
    [InlineData("matk_ZZZ_def456")]
    [InlineData("prefix_abc")]
    [InlineData(null)]
    public void ValidatePlaintextFormat应拒绝非法token(string? token)
    {
        string? error = AgentToken.ValidatePlaintextFormat(token);
        Assert.NotNull(error);
    }
}
