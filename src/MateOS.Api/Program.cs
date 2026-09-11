using System.Security.Claims;
using System.Text;
using MateOS.Api.Auth;
using MateOS.Api.Channels;
using MateOS.Api.Observability;
using MateOS.Api.Persistence;
using MateOS.Api.Workspace;
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
            // refresh token 与 access token 用同一把密钥签名，若不在这一层拦住，
            // 一个 30 天有效的 refresh token 就能当 access token 直接访问业务端点。
            OnTokenValidated = context =>
            {
                string? tokenType = context.Principal?.FindFirstValue(MateOsClaims.TokenType);

                if (!string.Equals(tokenType, MateOsClaims.AccessTokenType, StringComparison.Ordinal))
                {
                    context.Fail("令牌类型不是 access");
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

// ───────────────────────────── 端点 ─────────────────────────────

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "mateos-api" })).AllowAnonymous();

app.MapAuthEndpoints();
app.MapMeEndpoints();
app.MapWorkspaceEndpoints();
app.MapChannelEndpoints();

app.Run();

/// <summary>
/// 供集成测试用 <c>WebApplicationFactory&lt;Program&gt;</c> 挂载宿主。
/// </summary>
public partial class Program;
