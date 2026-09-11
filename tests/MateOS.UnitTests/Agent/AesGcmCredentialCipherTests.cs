using System.Security.Cryptography;
using MateOS.Api.Agents;
using MateOS.Domain.Agent;

namespace MateOS.UnitTests.Agent;

/// <summary>
/// 覆盖 E2 §3.3 AES-256-GCM 信封：加密 → 解密 round-trip + 篡改检测。
/// </summary>
public sealed class AesGcmCredentialCipherTests
{
    private static byte[] MakeKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void 加密应返nonce_12密文tag_16格式()
    {
        AesGcmCredentialCipher cipher = new(MakeKey());
        byte[] envelope = cipher.Encrypt("sk-12345");

        // 最短：12 (nonce) + 0 (密文) + 16 (tag) = 28
        Assert.True(envelope.Length >= CredentialCrypto.EnvelopeHeaderAndTag,
            $"envelope 太短：{envelope.Length}");

        // 密文部分 = envelope - nonce - tag
        int cipherPart = envelope.Length - CredentialCrypto.EnvelopeHeaderAndTag;
        Assert.Equal(8, cipherPart); // "sk-12345" 是 8 字节 UTF-8
    }

    [Fact]
    public void 加密再解密应还原明文()
    {
        AesGcmCredentialCipher cipher = new(MakeKey());
        string plaintext = "sk-secret-ABCDEF";
        byte[] envelope = cipher.Encrypt(plaintext);
        string decrypted = cipher.Decrypt(envelope);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void 同样明文两次加密应产生不同密文_nonce唯一()
    {
        AesGcmCredentialCipher cipher = new(MakeKey());
        byte[] env1 = cipher.Encrypt("hello");
        byte[] env2 = cipher.Encrypt("hello");

        Assert.NotEqual(env1, env2);
    }

    [Fact]
    public void 篡改密文应抛AesGcmException()
    {
        AesGcmCredentialCipher cipher = new(MakeKey());
        byte[] envelope = cipher.Encrypt("hello");

        // 翻转 nonce 第一个字节
        envelope[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(envelope));
    }

    [Fact]
    public void 篡改tag应抛AesGcmException()
    {
        AesGcmCredentialCipher cipher = new(MakeKey());
        byte[] envelope = cipher.Encrypt("hello");

        // 翻转 tag 最后一个字节
        envelope[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(envelope));
    }

    [Fact]
    public void 不同主密钥解同密文应抛异常()
    {
        AesGcmCipher1 = new AesGcmCredentialCipher(MakeKey());
        AesGcmCredentialCipher cipher2 = new(MakeKey());

        byte[] envelope = AesGcmCipher1.Encrypt("hello");
        Assert.ThrowsAny<CryptographicException>(() => cipher2.Decrypt(envelope));
    }

    [Fact]
    public void 构造时应拒绝非32字节主密钥()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmCredentialCipher(new byte[16]));
        Assert.Throws<ArgumentException>(() => new AesGcmCredentialCipher(new byte[64]));
    }

    // 兼容 CS0136：测试类内部不同名 cipher 变量
    private AesGcmCredentialCipher AesGcmCipher1 = null!;
}
