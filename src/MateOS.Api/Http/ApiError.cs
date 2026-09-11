namespace MateOS.Api.Http;

/// <summary>API 错误的统一形状（所有非 2xx 响应都用它）。</summary>
/// <param name="Code">稳定的机器可读错误码，客户端据此分支，不解析 Message。</param>
/// <param name="Message">给人看的说明。</param>
public sealed record ApiError(string Code, string Message);

/// <summary>错误响应构造器。集中在此，避免各端点各写一套状态码。</summary>
public static class ApiErrors
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string EmailAlreadyRegistered = "EMAIL_ALREADY_REGISTERED";
    public const string InvalidToken = "INVALID_TOKEN";
    public const string RateLimited = "RATE_LIMITED";
    public const string NotMember = "NOT_MEMBER";
    public const string NotOwner = "NOT_OWNER";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string WouldOrphanOrganization = "WOULD_ORPHAN_ORGANIZATION";

    /// <summary>E8 §5.1：project 没有 active Work Management Provider（422，不是 500）。</summary>
    public const string NoActiveWorkProvider = "NO_ACTIVE_WORK_PROVIDER";

    /// <summary>E8 §5.1：binding 引用了未注册的 provider_key（422）。</summary>
    public const string UnknownWorkProvider = "UNKNOWN_WORK_PROVIDER";

    public static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new ApiError(code, message));

    public static IResult Unauthorized(string code, string message) =>
        Results.Json(new ApiError(code, message), statusCode: StatusCodes.Status401Unauthorized);

    public static IResult Forbidden(string code, string message) =>
        Results.Json(new ApiError(code, message), statusCode: StatusCodes.Status403Forbidden);

    public static IResult NotFoundResult(string code, string message) =>
        Results.Json(new ApiError(code, message), statusCode: StatusCodes.Status404NotFound);

    public static IResult ConflictResult(string code, string message) =>
        Results.Conflict(new ApiError(code, message));

    public static IResult TooManyRequests(string code, string message, TimeSpan retryAfter)
    {
        return new RateLimitedResult(new ApiError(code, message), retryAfter);
    }

    /// <summary>带 <c>Retry-After</c> 头的 429。</summary>
    private sealed class RateLimitedResult(ApiError error, TimeSpan retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

            return httpContext.Response.WriteAsJsonAsync(error);
        }
    }
}
