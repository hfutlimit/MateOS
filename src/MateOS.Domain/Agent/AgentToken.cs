using System.Security.Cryptography;
using System.Text;

namespace MateOS.Domain.Agent;

/// <summary>
/// Agent token 哈希与生命周期（E2 §3 / §5 / F11）。
/// </summary>
/// <remarks>
/// <para>
/// 明文 token 形如 <c>matk_&lt;64 hex&gt;</c>，前端只在签发响应里拿到一次。
/// DB 只存 SHA-256 hex（64 字符），撤销时翻 <c>revoked = true</c>。
/// </para>
/// <para>
/// V1 token 实际是 JWT（sub=agent_id, token_type=agent）；hash 用来做撤销列表
/// 唯一索引（即便 JWT 内容相同，新签发实例也是新的 jti + hash）。
/// </para>
/// </remarks>
public static class AgentToken
{
    /// <summary>明文 token 前缀（用于 UI 标识）。</summary>
    public const string TokenPrefix = "matk_";

    /// <summary>SHA-256 hex 字符数。</summary>
    public const int HashHexLength = 64;

    /// <summary>明文 token 长度（5 + 64 = 69 字符）。</summary>
    public const int PlaintextLength = 5 + HashHexLength;

    /// <summary>
    /// 计算 token 明文 SHA-256 hex。
    /// </summary>
    public static string ComputeHashHex(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            throw new ArgumentException("token plaintext 不能为空", nameof(plaintext));
        }

        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>生成新 token 明文 + 配套 jti。</summary>
    /// <returns>(plaintext, jti, hashHex)。plaintext 一次性下发，hashHex 落库。</returns>
    public static (string Plaintext, Guid Jti, string HashHex) Generate()
    {
        // 64 字节随机 → 128 hex → 取前 64 hex 字符作 token 后缀
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        string suffix = Convert.ToHexString(random).ToLowerInvariant()[..HashHexLength];
        string plaintext = TokenPrefix + suffix;
        Guid jti = Guid.NewGuid();
        string hashHex = ComputeHashHex(plaintext);

        return (plaintext, jti, hashHex);
    }

    /// <summary>校验明文 token 格式（matk_ 前缀 + 64 hex）。</summary>
    public static string? ValidatePlaintextFormat(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "token 不能为空";
        }

        if (token.Length != PlaintextLength)
        {
            return $"token 长度必须为 {PlaintextLength} 字符（matk_ 前缀 + {HashHexLength} hex）";
        }

        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return $"token 必须以 {TokenPrefix} 开头";
        }

        for (int i = TokenPrefix.Length; i < token.Length; i++)
        {
            char c = token[i];

            if (!IsHexChar(c))
            {
                return $"token 第 {i + 1} 字符 '{c}' 不是 hex";
            }
        }

        return null;
    }

    private static bool IsHexChar(char c) =>
        (c is >= '0' and <= '9') || (c is >= 'a' and <= 'f') || (c is >= 'A' and <= 'F');
}
