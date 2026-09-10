using System.Text;
using Microsoft.EntityFrameworkCore;

namespace MateOS.Api.Persistence;

/// <summary>
/// EF Core 上下文。
/// </summary>
/// <remarks>
/// schema <b>不由 EF 创建</b>（D7/§9：显式 SQL 迁移），本上下文只负责读写与映射。
/// 因此这里只需要保证「表名 + 列名」与 <c>ops/postgres/migrations</c> 的 DDL 一致。
/// </remarks>
public sealed class MateOSDbContext(DbContextOptions<MateOSDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<SchemaMigration> SchemaMigrations => Set<SchemaMigration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── 表名（与 DDL 严格一致）──
        modelBuilder.Entity<User>().ToTable("users");
        modelBuilder.Entity<Organization>().ToTable("organizations");
        modelBuilder.Entity<OrganizationMember>().ToTable("organization_members");
        modelBuilder.Entity<Team>().ToTable("teams");
        modelBuilder.Entity<TeamMember>().ToTable("team_members");
        modelBuilder.Entity<Project>().ToTable("projects");
        modelBuilder.Entity<ProjectMember>().ToTable("project_members");
        modelBuilder.Entity<AuditLog>().ToTable("audit_logs");
        modelBuilder.Entity<OutboxEvent>().ToTable("outbox_events");
        modelBuilder.Entity<SchemaMigration>().ToTable("schema_migrations");

        // ── 主键 ──
        modelBuilder.Entity<User>().HasKey(x => x.Id);
        modelBuilder.Entity<Organization>().HasKey(x => x.Id);
        modelBuilder.Entity<Team>().HasKey(x => x.Id);
        modelBuilder.Entity<Project>().HasKey(x => x.Id);
        modelBuilder.Entity<AuditLog>().HasKey(x => x.Id);
        modelBuilder.Entity<OutboxEvent>().HasKey(x => x.Id);
        modelBuilder.Entity<SchemaMigration>().HasKey(x => x.Name);

        // 成员表是复合主键（同一用户在同一层级只有一条成员记录）
        modelBuilder.Entity<OrganizationMember>().HasKey(x => new { x.OrganizationId, x.UserId });
        modelBuilder.Entity<TeamMember>().HasKey(x => new { x.TeamId, x.UserId });
        modelBuilder.Entity<ProjectMember>().HasKey(x => new { x.ProjectId, x.UserId });

        // ── 关系 ──
        // 这些不是「有了导航属性才需要」的装饰：EF 靠它们决定同一次 SaveChanges 内的
        // INSERT 顺序。缺了关系，插入 organizations 与 organization_members 的先后就是
        // 未定义的，会随机撞 23503 外键冲突。
        modelBuilder.Entity<OrganizationMember>()
            .HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
        modelBuilder.Entity<OrganizationMember>()
            .HasOne<User>().WithMany().HasForeignKey(x => x.UserId);

        modelBuilder.Entity<Organization>()
            .HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy);

        modelBuilder.Entity<Team>()
            .HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId);

        modelBuilder.Entity<TeamMember>()
            .HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId);
        modelBuilder.Entity<TeamMember>()
            .HasOne<User>().WithMany().HasForeignKey(x => x.UserId);

        modelBuilder.Entity<Project>()
            .HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId);

        modelBuilder.Entity<ProjectMember>()
            .HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        modelBuilder.Entity<ProjectMember>()
            .HasOne<User>().WithMany().HasForeignKey(x => x.UserId);

        // ── 列类型（PostgreSQL 专有类型必须显式声明）──
        // 邮箱唯一且大小写不敏感（E1 F8）
        modelBuilder.Entity<User>().Property(x => x.Email).HasColumnType("citext");

        modelBuilder.Entity<AuditLog>().Property(x => x.Detail).HasColumnType("jsonb");
        modelBuilder.Entity<AuditLog>().Property(x => x.Ip).HasColumnType("inet");

        modelBuilder.Entity<OutboxEvent>().Property(x => x.Payload).HasColumnType("jsonb");

        // ── 全局列名 → snake_case ──
        // EF 默认用属性名原样，而 DDL 是 snake_case；漏掉任何一个都会在运行期报列不存在。
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    /// <summary>PascalCase → snake_case（<c>AvatarUrl</c> → <c>avatar_url</c>）。</summary>
    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var sb = new StringBuilder(name.Length + 8);

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];

            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
