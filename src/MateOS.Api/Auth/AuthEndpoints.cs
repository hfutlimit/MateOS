using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MateOS.Api.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

/// <summary>返回给客户端的用户摘要。<b>绝不含 password_hash</b>（README 工作约定：凭据绝不回显）。</summary>
public sealed record UserSummary(Guid Id, string Email, string DisplayName, string? AvatarUrl);

/// <summary>
/// 令牌响应。时间戳用 unix 毫秒，与 Connector 协议（detailed/03 §2）保持一致的时间表达。
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    long AccessExpiresAtMs,
    long RefreshExpiresAtMs,
    UserSummary User);

/// <summary>
/// E1 §4 的 <c>/auth/*</c> 端点。
/// </summary>
public static class AuthEndpoints
{
    /// <summary>口令最小长度。E1 未规定，取 8 位作为基线。</summary>
    public const int MinPasswordLength = 8;

    /// <summary>
    /// 用户不存在时用来消耗**等量**时间的占位哈希，防止通过响应时间枚举账号。
    /// 惰性求值：避免类首次加载时白白付一次 argon2 的代价。
    /// </summary>
    private static readonly Lazy<string> TimingPlaceholderHash =
        new(() => new PasswordHasher().Hash("mateos::timing-placeholder::never-matches"));

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", RegisterAsync).AllowAnonymous();
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/refresh", RefreshAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        MateOSDbContext db,
        PasswordHasher hasher,
        TokenService tokens,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        string email = request.Email?.Trim() ?? string.Empty;
        string displayName = request.DisplayName?.Trim() ?? string.Empty;

        if (!IsValidEmail(email))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "邮箱格式无效");
        }

        if (string.IsNullOrEmpty(displayName))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "display_name 不能为空");
        }

        if (request.Password is null || request.Password.Length < MinPasswordLength)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, $"口令至少 {MinPasswordLength} 位");
        }

        // email 是 citext 列，比较天然大小写不敏感（E1 F8）
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return ApiErrors.ConflictResult(ApiErrors.EmailAlreadyRegistered, "该邮箱已注册");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = hasher.Hash(request.Password),
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Users.Add(user);

        // 审计与业务写在同一个 SaveChanges（同一个事务）
        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, user.Id, AuditActions.UserRegistered,
            TargetType: "user", TargetId: user.Id, Detail: new { email = user.Email }));

        await db.SaveChangesAsync(ct);

        TokenPair pair = await tokens.IssueAsync(user.Id, user.DisplayName);

        return Results.Created("/me", ToResponse(pair, user));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        MateOSDbContext db,
        PasswordHasher hasher,
        TokenService tokens,
        LoginRateLimiter limiter,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        string email = request.Email?.Trim() ?? string.Empty;

        // E1 §5.4：以 email 为限流主体；email 为空时退化为按 IP
        string subject = string.IsNullOrEmpty(email)
            ? http.Connection.RemoteIpAddress?.ToString() ?? "unknown"
            : email;

        RateLimitDecision decision = await limiter.AcquireAsync(subject);

        if (!decision.Allowed)
        {
            return ApiErrors.TooManyRequests(
                ApiErrors.RateLimited, "登录尝试过于频繁，请稍后再试", decision.RetryAfter);
        }

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // 无论用户是否存在都做一次 argon2 校验，抹平响应时间差异
        bool passwordMatches = hasher.Verify(
            request.Password ?? string.Empty,
            user?.PasswordHash ?? TimingPlaceholderHash.Value);

        if (user is null || !passwordMatches)
        {
            return ApiErrors.Unauthorized(ApiErrors.InvalidCredentials, "邮箱或口令不正确");
        }

        TokenPair pair = await tokens.IssueAsync(user.Id, user.DisplayName);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, user.Id, AuditActions.UserLoggedIn,
            TargetType: "user", TargetId: user.Id));

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(pair, user));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        TokenService tokens,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "refresh_token 不能为空");
        }

        RotationResult? rotation = await tokens.RotateAsync(request.RefreshToken);

        if (rotation is null)
        {
            return ApiErrors.Unauthorized(ApiErrors.InvalidToken, "refresh token 无效、已过期或已被轮转");
        }

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == rotation.UserId, ct);

        if (user is null)
        {
            return ApiErrors.Unauthorized(ApiErrors.InvalidToken, "令牌所属用户已不存在");
        }

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, user.Id, AuditActions.TokenRefreshed,
            TargetType: "user", TargetId: user.Id));

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(rotation.Tokens, user));
    }

    private static async Task<IResult> LogoutAsync(
        RefreshRequest request,
        TokenService tokens,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        bool revoked = !string.IsNullOrWhiteSpace(request.RefreshToken)
            && await tokens.RevokeAsync(request.RefreshToken);

        Guid? actorId = http.GetUserId();

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, actorId, AuditActions.UserLoggedOut,
            TargetType: "user", TargetId: actorId, Detail: new { revoked }));

        await db.SaveChangesAsync(ct);

        // 幂等：重复登出同样返回 204，不暴露该 token 是否曾经有效
        return Results.NoContent();
    }

    private static TokenResponse ToResponse(TokenPair pair, User user) => new(
        pair.AccessToken,
        pair.RefreshToken,
        pair.AccessExpiresAt.ToUnixTimeMilliseconds(),
        pair.RefreshExpiresAt.ToUnixTimeMilliseconds(),
        new UserSummary(user.Id, user.Email, user.DisplayName, user.AvatarUrl));

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || email.Contains(' '))
        {
            return false;
        }

        int at = email.IndexOf('@');

        return at > 0 && at < email.Length - 1 && email.IndexOf('@', at + 1) < 0;
    }
}
