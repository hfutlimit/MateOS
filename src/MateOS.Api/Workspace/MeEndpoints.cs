using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MateOS.Api.Workspace;

public sealed record UpdateProfileRequest(string? DisplayName, string? AvatarUrl);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>E1 §4 的 <c>/me</c> 端点。</summary>
public static class MeEndpoints
{
    public static void MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/me").WithTags("Me").RequireAuthorization();

        group.MapGet("", GetAsync);
        group.MapPatch("", PatchAsync);
        group.MapPost("/password", ChangePasswordAsync);
    }

    private static async Task<IResult> GetAsync(
        MateOSDbContext db, HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        UserSummary? me = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new UserSummary(u.Id, u.Email, u.DisplayName, u.AvatarUrl))
            .FirstOrDefaultAsync(ct);

        return me is null
            ? ApiErrors.Unauthorized(ApiErrors.InvalidToken, "令牌所属用户已不存在")
            : Results.Ok(me);
    }

    private static async Task<IResult> PatchAsync(
        UpdateProfileRequest request,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
        {
            return ApiErrors.Unauthorized(ApiErrors.InvalidToken, "令牌所属用户已不存在");
        }

        string? displayName = request.DisplayName?.Trim();

        if (displayName is not null && displayName.Length == 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "display_name 不能为空字符串");
        }

        if (displayName is not null)
        {
            user.DisplayName = displayName;
        }

        if (request.AvatarUrl is not null)
        {
            user.AvatarUrl = string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl.Trim();
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, user.Id, AuditActions.ProfileUpdated,
            TargetType: "user", TargetId: user.Id));

        await db.SaveChangesAsync(ct);

        return Results.Ok(new UserSummary(user.Id, user.Email, user.DisplayName, user.AvatarUrl));
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        MateOSDbContext db,
        PasswordHasher hasher,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (request.NewPassword is null || request.NewPassword.Length < AuthEndpoints.MinPasswordLength)
        {
            return ApiErrors.BadRequest(
                ApiErrors.ValidationFailed, $"新口令至少 {AuthEndpoints.MinPasswordLength} 位");
        }

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
        {
            return ApiErrors.Unauthorized(ApiErrors.InvalidToken, "令牌所属用户已不存在");
        }

        if (!hasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
            return ApiErrors.Unauthorized(ApiErrors.InvalidCredentials, "当前口令不正确");
        }

        user.PasswordHash = hasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, user.Id, AuditActions.PasswordChanged,
            TargetType: "user", TargetId: user.Id));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}
