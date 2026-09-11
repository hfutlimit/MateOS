using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace MateOS.Api.Auth;

/// <summary>JWT 配置（E1 §2.1：access 15min + refresh 30d）。</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "mateos";
    public string Audience { get; set; } = "mateos-api";

    /// <summary>HMAC 签名密钥。生产必须由环境变量注入，不得使用仓库中的开发值。</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

/// <summary>自建 claim 名。</summary>
public static class MateOsClaims
{
    /// <summary>区分 access / refresh，防止 refresh token 被当作 access 使用。</summary>
    public const string TokenType = "token_type";

    public const string AccessTokenType = "access";
    public const string RefreshTokenType = "refresh";
    public const string AgentTokenType = "agent";
}

/// <summary>一对令牌。</summary>
public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt);

/// <summary>轮转结果：新令牌 + 令牌归属方，免去调用方再查一次用户。</summary>
public sealed record RotationResult(TokenPair Tokens, Guid UserId, string DisplayName);

/// <summary>
/// 令牌签发与轮转。
/// </summary>
/// <remarks>
/// <para>
/// 按 E1 §10.2 的口径，refresh 的吊销状态放 <b>Redis</b>（key <c>auth:rt:{jti}</c>）。
/// 这与 SYSTEM_DESIGN §10 的 G-1 纪律（Redis 只做可重建缓存）存在张力：
/// Redis 全量丢失会让所有 refresh token 失效，用户需重新登录。
/// 当前按 E1 文档实现；若后续要求「Redis 丢失可无损恢复」，应把 jti 落 PG 表再让 Redis 做缓存。
/// </para>
/// <para>
/// 轮转用 <c>KeyDelete</c> 的返回值做<b>一次性消费</b>：并发的两次 refresh 只有一个能拿到旧 jti，
/// 另一个必然失败 —— 这是 E1 F2「旧 jti 立即失效」的强形式。
/// </para>
/// </remarks>
public sealed class TokenService(
    IOptions<JwtOptions> options,
    IConnectionMultiplexer redis,
    TimeProvider clock)
{
    /// <summary>
    /// <c>MapInboundClaims = false</c> 必须显式设置：
    /// JwtSecurityTokenHandler 默认会把 <c>sub</c> 重写成 <c>ClaimTypes.NameIdentifier</c>，
    /// 那样这里的 <c>FindFirstValue("sub")</c> 永远取不到值，轮转会一直 401。
    /// 该行为独立于 <c>JwtBearerOptions.MapInboundClaims</c>，两处都要关。
    /// </summary>
    private static readonly JwtSecurityTokenHandler Handler = new() { MapInboundClaims = false };

    private readonly JwtOptions _options = options.Value;

    private static string RefreshKey(string jti) => $"auth:rt:{jti}";

    private static SecurityKey SigningKey(JwtOptions options) =>
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));

    /// <summary>签发一对新令牌，并把 refresh 的 jti 登记到 Redis。</summary>
    public async Task<TokenPair> IssueAsync(Guid userId, string displayName)
    {
        TimeSpan accessLifetime = TimeSpan.FromMinutes(_options.AccessTokenMinutes);
        TimeSpan refreshLifetime = TimeSpan.FromDays(_options.RefreshTokenDays);

        string access = CreateToken(userId, displayName, MateOsClaims.AccessTokenType, accessLifetime,
            out _, out DateTimeOffset accessExpiresAt);

        string refresh = CreateToken(userId, displayName, MateOsClaims.RefreshTokenType, refreshLifetime,
            out string refreshJti, out DateTimeOffset refreshExpiresAt);

        await redis.GetDatabase().StringSetAsync(
            RefreshKey(refreshJti),
            userId.ToString(),
            refreshLifetime);

        return new TokenPair(access, refresh, accessExpiresAt, refreshExpiresAt);
    }

    /// <summary>
    /// 轮转：校验 refresh → 消费旧 jti → 签发新的一对。任何一步不满足都返回 <c>null</c>。
    /// </summary>
    public async Task<RotationResult?> RotateAsync(string refreshToken)
    {
        ClaimsPrincipal principal;

        try
        {
            principal = Validate(refreshToken, MateOsClaims.RefreshTokenType);
        }
        catch (SecurityTokenException)
        {
            return null;
        }

        string? jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);

        if (string.IsNullOrEmpty(jti) || !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out Guid userId))
        {
            return null;
        }

        // 一次性消费：已轮转过 / 已登出的 jti 都不在 Redis 里
        bool consumed = await redis.GetDatabase().KeyDeleteAsync(RefreshKey(jti));

        if (!consumed)
        {
            return null;
        }

        string displayName = principal.FindFirstValue(JwtRegisteredClaimNames.Name) ?? string.Empty;

        return new RotationResult(await IssueAsync(userId, displayName), userId, displayName);
    }

    /// <summary>登出：吊销 refresh 的 jti。返回是否确实吊销了一条。</summary>
    public async Task<bool> RevokeAsync(string refreshToken)
    {
        try
        {
            ClaimsPrincipal principal = Validate(refreshToken, MateOsClaims.RefreshTokenType);
            string? jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);

            return !string.IsNullOrEmpty(jti)
                && await redis.GetDatabase().KeyDeleteAsync(RefreshKey(jti));
        }
        catch (SecurityTokenException)
        {
            return false;
        }
    }

    /// <summary>校验 access token 并返回主体（JwtBearer 中间件之外的手动校验入口）。</summary>
    public ClaimsPrincipal? ValidateAccessToken(string accessToken)
    {
        try
        {
            return Validate(accessToken, MateOsClaims.AccessTokenType);
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }

    private string CreateToken(
        Guid userId,
        string displayName,
        string tokenType,
        TimeSpan lifetime,
        out string jti,
        out DateTimeOffset expiresAt)
    {
        jti = Guid.NewGuid().ToString("N");

        DateTimeOffset now = clock.GetUtcNow();
        expiresAt = now + lifetime;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, jti),
            new(MateOsClaims.TokenType, tokenType),
        };

        // display_name 只放 access token：它服务于 UI，refresh token 不需要携带可变信息
        if (tokenType == MateOsClaims.AccessTokenType)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Name, displayName));
        }

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256));

        return Handler.WriteToken(token);
    }

    private ClaimsPrincipal Validate(string token, string expectedTokenType)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = SigningKey(_options),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Name,
        };

        ClaimsPrincipal principal = Handler.ValidateToken(token, parameters, out _);

        string? actualType = principal.FindFirstValue(MateOsClaims.TokenType);

        if (!string.Equals(actualType, expectedTokenType, StringComparison.Ordinal))
        {
            throw new SecurityTokenException($"令牌类型不匹配：期望 {expectedTokenType}，实际 {actualType ?? "null"}");
        }

        return principal;
    }
}
