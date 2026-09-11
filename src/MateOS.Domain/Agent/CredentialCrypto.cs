namespace MateOS.Domain.Agent;

/// <summary>
/// Credential 信封规则（E2 §3.3 AES-256-GCM）。
/// </summary>
/// <remarks>
/// <para>
/// 密文格式：<c>nonce(12) || ciphertext || tag(16)</c>（E2 §3.3 明文要求）。
/// </para>
/// <para>
/// V1 简化：本地 AES-256-GCM with env-supplied master key。
/// V2：接入 Argo KMS 信封（E2 §3.3 生产路径）。
/// </para>
/// <para>
/// 本类只放「信封格式 + 长度校验」的纯函数不变量；实际加解密由 Api 层 Crypto 实现
/// （依赖 System.Security.Cryptography.AesGcm，Domain 不应引用）。
/// </para>
/// </remarks>
public static class CredentialCrypto
{
    /// <summary>nonce 长度（byte）。</summary>
    public const int NonceSize = 12;

    /// <summary>GCM tag 长度（byte）。</summary>
    public const int TagSize = 16;

    /// <summary>头部 + 尾部最小长度（密文 = Nonce + ciphertext + Tag）。</summary>
    public const int EnvelopeHeaderAndTag = NonceSize + TagSize;

    /// <summary>
    /// 校验密文长度合法性：最短 = Nonce + 0 字节密文 + Tag = 28；最长由调用方限（如 1MB）。
    /// </summary>
    public static string? ValidateEnvelopeLength(int envelopeLength, int maxLength)
    {
        if (envelopeLength < EnvelopeHeaderAndTag)
        {
            return $"密文长度 {envelopeLength} < 最小值 {EnvelopeHeaderAndTag}（nonce + tag）";
        }

        if (envelopeLength > maxLength)
        {
            return $"密文长度 {envelopeLength} 超过上限 {maxLength}";
        }

        return null;
    }
}

/// <summary>
/// Credential provider 自由文本白名单（V1 限制，避免乱填）。
/// </summary>
public static class CredentialProvider
{
    public const string OpenAI = "openai";
    public const string Anthropic = "anthropic";
    public const string Google = "google";
    public const string Azure = "azure";
    public const string Bedrock = "bedrock";

    public static readonly IReadOnlySet<string> Canonical = new HashSet<string>(StringComparer.Ordinal)
    {
        OpenAI,
        Anthropic,
        Google,
        Azure,
        Bedrock,
    };

    public static string? Validate(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "provider 不能为空";
        }

        if (!Canonical.Contains(provider))
        {
            return $"未知 provider：{provider}（canonical: {string.Join(", ", Canonical)}）";
        }

        return null;
    }
}
