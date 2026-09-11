using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace MateOS.Api.Auth;

/// <summary>从已认证主体读取当前用户。</summary>
/// <remarks>
/// claim 名用 JOSE 标准 <c>sub</c>（<see cref="JwtRegisteredClaimNames.Sub"/>）的原始形态：
/// Program.cs 里关闭了 <c>MapInboundClaims</c>，因此不会出现 <c>ClaimTypes.NameIdentifier</c>
/// 与 <c>sub</c> 两套名字并存的情况。取用户 id 一律走这里，不要在端点里散写 claim 名。
/// </remarks>
public static class CurrentUserExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal? principal)
    {
        string? subject = principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);

        return Guid.TryParse(subject, out Guid userId) ? userId : null;
    }

    /// <summary>取用户 id；缺失即视为编程错误（该端点必须挂在 RequireAuthorization 之后）。</summary>
    public static Guid RequireUserId(this ClaimsPrincipal? principal) =>
        principal.GetUserId() ?? throw new InvalidOperationException("已认证请求缺少 sub claim");

    public static Guid? GetUserId(this HttpContext context) => context.User.GetUserId();

    public static Guid RequireUserId(this HttpContext context) => context.User.RequireUserId();

    /// <summary>
    /// 取 agent id（agent_token 鉴权时）。与 <see cref="GetUserId"/> 共用 sub 字段，
    /// 由 <c>token_type</c> claim 区分上下文。
    /// </summary>
    public static Guid? GetAgentId(this ClaimsPrincipal? principal)
    {
        string? tokenType = principal?.FindFirstValue(MateOsClaims.TokenType);

        if (!string.Equals(tokenType, MateOsClaims.AgentTokenType, StringComparison.Ordinal))
        {
            return null;
        }

        return principal.GetUserId();
    }

    public static Guid RequireAgentId(this ClaimsPrincipal? principal) =>
        principal.GetAgentId() ?? throw new InvalidOperationException(
            "已认证请求不是 agent_token 上下文，缺少 token_type=agent claim");

    public static Guid? GetAgentId(this HttpContext context) => context.User.GetAgentId();

    public static Guid RequireAgentId(this HttpContext context) => context.User.RequireAgentId();
}
