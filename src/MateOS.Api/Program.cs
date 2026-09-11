using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Channels;
using MateOS.Api.Memory;
using MateOS.Api.NeedsYou;
using MateOS.Api.Observability;
using MateOS.Api.Outbox;
using MateOS.Api.Permissions;
using MateOS.Api.Persistence;
using MateOS.Api.Routing;
using MateOS.Api.Work;
using MateOS.Api.Workspace;
using MateOS.Domain.Work;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ───────────────────────────── 配置 ─────────────────────────────

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

JwtOptions jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

// 签名密钥是认证的根，缺了或太短就直接拒绝启动，不要带着弱配置跑起来
if (string.IsNullOrWhiteSpace(jwt.SigningKey) || Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey 缺失或不足 32 字节。开发环境见 appsettings.Development.json，生产用环境变量 Jwt__SigningKey。");
}

string postgresConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("缺少 ConnectionStrings:Postgres");

string redisConnection = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("缺少 ConnectionStrings:Redis");

// E2：Agent credential 主密钥（dev 用 env / user-secrets 注入；V2 升 Argo KMS）
string credentialKeyBase64 = builder.Configuration["Agent:CryptoKey"]
    ?? throw new InvalidOperationException(
        "缺少 Agent:CryptoKey。dev 环境用 dotnet user-secrets 或 env MateOS__Agent__CryptoKey 注入（base64 of 32 bytes）。");
byte[] credentialKey;
try
{
    credentialKey = Convert.FromBase64String(credentialKeyBase64);
}
catch (FormatException ex)
{
    throw new InvalidOperationException("Agent:CryptoKey 不是合法的 base64 字符串", ex);
}

if (credentialKey.Length != 32)
{
    throw new InvalidOperationException(
        $"Agent:CryptoKey 解码后必须为 32 字节（AES-256），当前 {credentialKey.Length} 字节");
}

// ─────────────────────────── 基础设施 ───────────────────────────

builder.Services.AddDbContext<MateOSDbContext>(options => options.UseNpgsql(postgresConnection));

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));

builder.Services.AddSingleton(TimeProvider.System);

// ─────────────────────────── 领域服务 ───────────────────────────

builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<LoginRateLimiter>();
builder.Services.AddScoped<AuditWriter>();
builder.Services.AddScoped<SqlMigrationRunner>();
builder.Services.AddScoped<WorkspaceAuthorizer>();

// ── E8 Work Management Provider 注册（bootstrap 注入，E8 §4）──
// Built-in 在启动时注册；E9 Jira 在绑连接时再 Register('jira')，
// Work Management Core 代码零改动（F4/F5）。
builder.Services.AddSingleton<WorkManagementProviderRegistry>(_ =>
{
    var registry = new WorkManagementProviderRegistry();
    registry.Register(new BuiltInWorkManagementProvider());
    return registry;
});

// ── E3 Channel & Messaging WS 网关 ──
builder.Services.AddSingleton<WsConnectionRegistry>();
builder.Services.AddSingleton<WsSender>();
builder.Services.AddHostedService<WsHeartbeatWatchdog>();

// ── M7 Outbox 主干 ──
builder.Services.AddSingleton<OutboxWriter>();
builder.Services.AddHostedService<OutboxRelayWorker>();

// ── E2 Agent Registry ──
builder.Services.AddSingleton(_ => new AesGcmCredentialCipher(credentialKey));

// ─────────────────────────── HTTP JSON 契约 ───────────────────────────

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // wire 形态统一 snake_case：WS envelope、outbox payload、messages.content(JSONB)
    // 三处都已经是 snake_case，REST 若保持 minimal API 默认的 camelCase，
    // 同一份数据在两个通道上形态不同，前端要写两套解析（契约 SSOT 也失去意义）。
    //
    // 刻意**不**设 DefaultIgnoreCondition.WhenWritingNull：可空字段必须显式出现为 null，
    // 否则「没有值」与「字段不存在」在客户端无法区分（例如 CR 未产生 execution 时
    // target_execution_id 应为 null 而不是缺字段）。
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

// ───────────────────────────── 认证 ─────────────────────────────

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 保留 sub / jti 的原始名字，避免与 ClaimTypes.* 两套名字并存
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };

        options.Events = new JwtBearerEvents
        {
            // 同时接受 access（user token）与 agent（agent_token）。
            // refresh token 仍被这一层拦住：一个 30 天有效的 refresh token 不能当 access 用。
            // agent_token 与 access 共用密钥，但 token_type claim 区分上下文。
            OnTokenValidated = context =>
            {
                string? tokenType = context.Principal?.FindFirstValue(MateOsClaims.TokenType);

                if (!string.Equals(tokenType, MateOsClaims.AccessTokenType, StringComparison.Ordinal)
                    && !string.Equals(tokenType, MateOsClaims.AgentTokenType, StringComparison.Ordinal))
                {
                    context.Fail($"令牌类型不被接受：{tokenType ?? "null"}");
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

WebApplication app = builder.Build();

// ───────────────────────────── 迁移 ─────────────────────────────

if (app.Configuration.GetValue("Database:AutoMigrate", true))
{
    using IServiceScope scope = app.Services.CreateScope();

    var runner = scope.ServiceProvider.GetRequiredService<SqlMigrationRunner>();
    IReadOnlyList<string> applied = await runner.RunAsync();

    app.Logger.LogInformation("数据库迁移完成，本次新应用 {Count} 个：{Migrations}",
        applied.Count, applied.Count == 0 ? "（无）" : string.Join(", ", applied));
}

// ───────────────────────────── 管道 ─────────────────────────────

app.UseMiddleware<TraceMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// WS 必须在 auth 之后 + endpoint 之前
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});

// ───────────────────────────── 端点 ─────────────────────────────

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "mateos-api" })).AllowAnonymous();

app.MapAuthEndpoints();
app.MapMeEndpoints();
app.MapWorkspaceEndpoints();
app.MapChannelEndpoints();
app.MapAgentEndpoints();
app.MapExecutionEndpoints();
app.MapPermissionEndpoints();
app.MapRoutingEndpoints();
app.MapMemoryEndpoints();
app.MapNeedsYouEndpoints();
app.MapWorkItemEndpoints();
app.MapWs();

app.Run();

/// <summary>
/// 供集成测试用 <c>WebApplicationFactory&lt;Program&gt;</c> 挂载宿主。
/// </summary>
public partial class Program;
