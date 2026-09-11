using System.Text;
using MateOS.Domain.Agent;
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
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelMember> ChannelMembers => Set<ChannelMember>();
    public DbSet<ChannelSeqCounter> ChannelSeqCounters => Set<ChannelSeqCounter>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentProjectMember> AgentProjectMembers => Set<AgentProjectMember>();
    public DbSet<AgentToken> AgentTokens => Set<AgentToken>();
    public DbSet<AgentExecution> AgentExecutions => Set<AgentExecution>();
    public DbSet<ExecutionAttempt> ExecutionAttempts => Set<ExecutionAttempt>();
    public DbSet<ExecutionEvent> ExecutionEvents => Set<ExecutionEvent>();
    public DbSet<AgentDispatchInbox> AgentDispatchInbox => Set<AgentDispatchInbox>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<Trigger> Triggers => Set<Trigger>();
    public DbSet<CollaborationRequest> CollaborationRequests => Set<CollaborationRequest>();
    public DbSet<DecisionRecord> DecisionRecords => Set<DecisionRecord>();

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
        modelBuilder.Entity<Channel>().ToTable("channels");
        modelBuilder.Entity<ChannelMember>().ToTable("channel_members");
        modelBuilder.Entity<ChannelSeqCounter>().ToTable("channel_seq_counters");
        modelBuilder.Entity<Message>().ToTable("messages");
        modelBuilder.Entity<Attachment>().ToTable("attachments");
        modelBuilder.Entity<Credential>().ToTable("credentials");
        modelBuilder.Entity<Agent>().ToTable("agents");
        modelBuilder.Entity<AgentProjectMember>().ToTable("agent_project_membership");
        modelBuilder.Entity<AgentToken>().ToTable("agent_tokens");
        modelBuilder.Entity<AgentExecution>().ToTable("agent_executions");
        modelBuilder.Entity<ExecutionAttempt>().ToTable("execution_attempts");
        modelBuilder.Entity<ExecutionEvent>().ToTable("execution_events");
        modelBuilder.Entity<AgentDispatchInbox>().ToTable("agent_dispatch_inbox");
        modelBuilder.Entity<Permission>().ToTable("permissions");
        modelBuilder.Entity<Trigger>().ToTable("triggers");
        modelBuilder.Entity<CollaborationRequest>().ToTable("collaboration_requests");
        modelBuilder.Entity<DecisionRecord>().ToTable("decision_records");

        // ── 主键 ──
        modelBuilder.Entity<User>().HasKey(x => x.Id);
        modelBuilder.Entity<Organization>().HasKey(x => x.Id);
        modelBuilder.Entity<Team>().HasKey(x => x.Id);
        modelBuilder.Entity<Project>().HasKey(x => x.Id);
        modelBuilder.Entity<AuditLog>().HasKey(x => x.Id);
        modelBuilder.Entity<OutboxEvent>().HasKey(x => x.Id);
        modelBuilder.Entity<SchemaMigration>().HasKey(x => x.Name);
        modelBuilder.Entity<Channel>().HasKey(x => x.Id);
        modelBuilder.Entity<ChannelMember>().HasKey(x => new { x.ChannelId, x.MemberType, x.MemberId });
        modelBuilder.Entity<ChannelSeqCounter>().HasKey(x => x.ChannelId);
        modelBuilder.Entity<Message>().HasKey(x => new { x.ChannelId, x.Seq });
        modelBuilder.Entity<Attachment>().HasKey(x => x.Id);
        modelBuilder.Entity<Credential>().HasKey(x => x.Id);
        modelBuilder.Entity<Agent>().HasKey(x => x.Id);
        modelBuilder.Entity<AgentProjectMember>().HasKey(x => new { x.AgentId, x.ProjectId });
        modelBuilder.Entity<AgentToken>().HasKey(x => x.Id);
        modelBuilder.Entity<AgentExecution>().HasKey(x => x.Id);
        modelBuilder.Entity<ExecutionAttempt>().HasKey(x => x.Id);
        modelBuilder.Entity<ExecutionEvent>().HasKey(x => x.Id);
        modelBuilder.Entity<AgentDispatchInbox>().HasKey(x => x.Id);
        modelBuilder.Entity<Permission>().HasKey(x => x.Id);
        modelBuilder.Entity<Trigger>().HasKey(x => x.Id);
        modelBuilder.Entity<CollaborationRequest>().HasKey(x => x.Id);
        modelBuilder.Entity<DecisionRecord>().HasKey(x => x.Id);

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

        // E3 Channel：channel_members + messages 都依赖 channels
        modelBuilder.Entity<Channel>()
            .HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
        modelBuilder.Entity<ChannelMember>()
            .HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId);
        modelBuilder.Entity<ChannelSeqCounter>()
            .HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId);
        modelBuilder.Entity<Message>()
            .HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId);

        // ── 列类型（PostgreSQL 专有类型必须显式声明）──
        // 邮箱唯一且大小写不敏感（E1 F8）
        modelBuilder.Entity<User>().Property(x => x.Email).HasColumnType("citext");

        modelBuilder.Entity<AuditLog>().Property(x => x.Detail).HasColumnType("jsonb");
        modelBuilder.Entity<AuditLog>().Property(x => x.Ip).HasColumnType("inet");

        modelBuilder.Entity<OutboxEvent>().Property(x => x.Payload).HasColumnType("jsonb");

        // E3：messages / channel.content 等 JSONB 列
        modelBuilder.Entity<Message>().Property(x => x.Content).HasColumnType("jsonb");
        modelBuilder.Entity<Message>().Property(x => x.Mentions).HasColumnType("jsonb");

        // E2：credentials / agents 关系 + JSONB 列
        modelBuilder.Entity<Credential>()
            .HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        modelBuilder.Entity<Agent>()
            .HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId);
        modelBuilder.Entity<Agent>()
            .HasOne<Credential>().WithMany().HasForeignKey(x => x.CredentialId);
        modelBuilder.Entity<AgentProjectMember>()
            .HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId);
        modelBuilder.Entity<AgentProjectMember>()
            .HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);

        modelBuilder.Entity<Credential>().Property(x => x.Meta).HasColumnType("jsonb");
        modelBuilder.Entity<Agent>().Property(x => x.Capabilities).HasColumnType("jsonb");

        // E7：agent_executions / events / dispatch inbox 关系 + JSONB
        modelBuilder.Entity<AgentExecution>()
            .HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId);
        modelBuilder.Entity<ExecutionAttempt>()
            .HasOne<AgentExecution>().WithMany().HasForeignKey(x => x.ExecutionId);
        modelBuilder.Entity<ExecutionEvent>()
            .HasOne<ExecutionAttempt>().WithMany().HasForeignKey(x => x.AttemptId);

        modelBuilder.Entity<AgentExecution>().Property(x => x.Input).HasColumnType("jsonb");
        modelBuilder.Entity<AgentExecution>().Property(x => x.ContextRefs).HasColumnType("jsonb");
        modelBuilder.Entity<AgentExecution>().Property(x => x.ResultOutput).HasColumnType("jsonb");
        modelBuilder.Entity<AgentExecution>().Property(x => x.ResultUsage).HasColumnType("jsonb");
        modelBuilder.Entity<ExecutionEvent>().Property(x => x.Payload).HasColumnType("jsonb");

        // E4：triggers / collaboration_requests / decision_records 关系 + JSONB
        modelBuilder.Entity<CollaborationRequest>()
            .HasOne<Trigger>().WithMany().HasForeignKey(x => x.TriggerId);
        modelBuilder.Entity<DecisionRecord>()
            .HasOne<CollaborationRequest>().WithMany().HasForeignKey(x => x.CollaborationRequestId);

        modelBuilder.Entity<Trigger>().Property(x => x.TriggerRef).HasColumnType("jsonb");
        modelBuilder.Entity<CollaborationRequest>().Property(x => x.TriggerRef).HasColumnType("jsonb");
        modelBuilder.Entity<CollaborationRequest>().Property(x => x.RequiredCapabilities).HasColumnType("jsonb");
        modelBuilder.Entity<CollaborationRequest>().Property(x => x.ContextRefs).HasColumnType("jsonb");
        modelBuilder.Entity<DecisionRecord>().Property(x => x.Needs).HasColumnType("jsonb");

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
