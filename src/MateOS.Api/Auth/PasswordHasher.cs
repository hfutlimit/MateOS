using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace MateOS.Api.Auth;

/// <summary>
/// argon2id 口令哈希（E1 §3：<c>password_hash TEXT -- argon2id</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 输出为 PHC 标准编码串：<c>$argon2id$v=19$m=65536,t=3,p=4$&lt;salt&gt;$&lt;hash&gt;</c>。
/// 参数写进哈希串本身，因此日后调参（提高内存或轮数）不会让存量哈希失效 ——
/// 校验时按每条记录自带的参数重算即可，无需迁移数据。
/// </para>
/// <para>校验用 <see cref="CryptographicOperations.FixedTimeEquals"/>，避免时序侧信道。</para>
/// </remarks>
public sealed class PasswordHasher
{
    public const int SaltSizeBytes = 16;
    public const int HashSizeBytes = 32;

    // 参数选择：64 MiB / 3 轮 / 4 并行度，属 OWASP 推荐区间。
    private const int DefaultMemoryKib = 65536;
    private const int DefaultIterations = 3;
    private const int DefaultParallelism = 4;

    /// <summary>生成 PHC 格式的哈希串。</summary>
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] hash = Compute(password, salt, DefaultMemoryKib, DefaultIterations, DefaultParallelism);

        return string.Create(
            null,
            $"$argon2id$v=19$m={DefaultMemoryKib},t={DefaultIterations},p={DefaultParallelism}$" +
            $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>校验口令。格式非法或参数不可解析时返回 <c>false</c>，不抛异常。</summary>
    public bool Verify(string password, string encodedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encodedHash))
        {
            return false;
        }

        try
        {
            // 期望形状：["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
            string[] segments = encodedHash.Split('$', StringSplitOptions.None);

            if (segments.Length != 6 || segments[1] != "argon2id")
            {
                return false;
            }

            if (!TryParseParameters(segments[3], out int memoryKib, out int iterations, out int parallelism))
            {
                return false;
            }

            byte[] salt = Convert.FromBase64String(segments[4]);
            byte[] expected = Convert.FromBase64String(segments[5]);

            byte[] actual = Compute(password, salt, memoryKib, iterations, parallelism);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseParameters(string segment, out int memoryKib, out int iterations, out int parallelism)
    {
        memoryKib = 0;
        iterations = 0;
        parallelism = 0;

        foreach (string pair in segment.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);

            if (kv.Length != 2 || !int.TryParse(kv[1], out int value) || value <= 0)
            {
                return false;
            }

            switch (kv[0])
            {
                case "m": memoryKib = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
            }
        }

        return memoryKib > 0 && iterations > 0 && parallelism > 0;
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKib, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };

        return argon2.GetBytes(HashSizeBytes);
    }
}
