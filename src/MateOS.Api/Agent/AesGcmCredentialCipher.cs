using System.Security.Cryptography;
using System.Text;
using MateOS.Domain.Agent;

namespace MateOS.Api.Agents;

/// <summary>
/// AES-256-GCM 凭据加密器（E2 §3.3）。
/// </summary>
/// <remarks>
/// <para>
/// 格式：<c>nonce(12) || ciphertext || tag(16)</c>。密钥由配置 <c>Agent:CryptoKey</c> 注入。
/// V1 用本地静态主密钥；V2 升 Argo KMS 信封（E2 §3.3 生产路径）。
/// </para>
/// <para>
/// 主密钥要求：<c>byte[32]</c>，base64 配置。dev 环境用 <c>dotnet user-secrets</c> 或 env
/// <c>MateOS__Agent__CryptoKey</c> 注入；缺失即拒启动（与 <c>Jwt:SigningKey</c> 同等待遇）。
/// </para>
/// </remarks>
public sealed class AesGcmCredentialCipher
{
    private const int MaxCredentialLength = 4 * 1024; // 4KB 上限（API key / short secret）

    private readonly byte[] _masterKey;

    public AesGcmCredentialCipher(byte[] masterKey)
    {
        if (masterKey is null)
        {
            throw new ArgumentNullException(nameof(masterKey));
        }

        if (masterKey.Length != 32)
        {
            throw new ArgumentException(
                $"AES-256-GCM 要求 32 字节主密钥，当前 {masterKey.Length} 字节", nameof(masterKey));
        }

        _masterKey = masterKey;
    }

    public byte[] Encrypt(string plaintext)
    {
        if (plaintext is null)
        {
            throw new ArgumentNullException(nameof(plaintext));
        }

        byte[] cipher = Encoding.UTF8.GetBytes(plaintext);

        if (cipher.Length > MaxCredentialLength)
        {
            throw new InvalidOperationException(
                $"明文 {cipher.Length} 字节超过 {MaxCredentialLength} 上限");
        }

        byte[] nonce = new byte[CredentialCrypto.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        byte[] ciphertext = new byte[cipher.Length];
        byte[] tag = new byte[CredentialCrypto.TagSize];

        using AesGcm aes = new(_masterKey, tagSizeInBytes: CredentialCrypto.TagSize);
        aes.Encrypt(nonce, cipher, ciphertext, tag);

        byte[] envelope = new byte[CredentialCrypto.NonceSize + cipher.Length + CredentialCrypto.TagSize];
        Buffer.BlockCopy(nonce, 0, envelope, 0, CredentialCrypto.NonceSize);
        Buffer.BlockCopy(ciphertext, 0, envelope, CredentialCrypto.NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, envelope, CredentialCrypto.NonceSize + cipher.Length, CredentialCrypto.TagSize);

        return envelope;
    }

    public string Decrypt(byte[] envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        string? lengthError = CredentialCrypto.ValidateEnvelopeLength(
            envelope.Length, MaxCredentialLength + CredentialCrypto.EnvelopeHeaderAndTag);

        if (lengthError is not null)
        {
            throw new InvalidOperationException(lengthError);
        }

        int cipherLen = envelope.Length - CredentialCrypto.EnvelopeHeaderAndTag;
        byte[] nonce = new byte[CredentialCrypto.NonceSize];
        byte[] ciphertext = new byte[cipherLen];
        byte[] tag = new byte[CredentialCrypto.TagSize];

        Buffer.BlockCopy(envelope, 0, nonce, 0, CredentialCrypto.NonceSize);
        Buffer.BlockCopy(envelope, CredentialCrypto.NonceSize, ciphertext, 0, cipherLen);
        Buffer.BlockCopy(envelope, CredentialCrypto.NonceSize + cipherLen, tag, 0, CredentialCrypto.TagSize);

        byte[] plaintext = new byte[cipherLen];

        using AesGcm aes = new(_masterKey, tagSizeInBytes: CredentialCrypto.TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}
