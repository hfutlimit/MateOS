using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Outbox;
using MateOS.Api.Persistence;
using MateOS.Api.Workspace;
using MateOS.Domain.Agent;
using MateOS.Domain.Identity;
using MateOS.Domain.Work;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DbAgent = MateOS.Api.Persistence.Agent;
using DbAgentExecution = MateOS.Api.Persistence.AgentExecution;
using DbWorkComment = MateOS.Api.Persistence.WorkComment;
using DbWorkItem = MateOS.Api.Persistence.WorkItem;
using DbWorkItemBinding = MateOS.Api.Persistence.WorkItemBinding;
using DbWorkManagementConnection = MateOS.Api.Persistence.WorkManagementConnection;
using DbWorkRelation = MateOS.Api.Persistence.WorkRelation;
using Project = MateOS.Api.Persistence.Project;

namespace MateOS.Api.Work;

// ── 请求 DTO ──

public sealed record CreateWorkItemRequest(
    string Type,
    string Title,
    string? Description,
    string? AssigneeType,
    Guid? AssigneeId,
    DateTimeOffset? DueAt);

public sealed record UpdateWorkItemRequest(
    string? Title,
    string? Description,
    string? Status,
    string? AssigneeType,
    Guid? AssigneeId,
    bool? ClearAssignee,
    DateTimeOffset? DueAt,
    bool? ClearDueAt);

public sealed record TransitionWorkItemRequest(string Status);

public sealed record AddWorkCommentRequest(string Body);

public sealed record CreateWorkRelationRequest(Guid ToWorkItemId, string RelationType);

public sealed record ChangeWorkProviderBindingRequest(
    string ProviderKey,
    Guid? ConnectionId,
    string? ExternalProjectRef,
    JsonElement? Settings);

public sealed record AssignWorkItemRequest(Guid AgentId, int? DeadlineS);

// ── 响应 DTO（wire 形态由全局 snake_case 策略决定）──

public sealed record WorkItemSummary(
    Guid Id,
    Guid ProjectId,
    string Type,
    string Title,
    string? Description,
    string Status,
    string CanonicalStatusCategory,
    string? AssigneeType,
    Guid? AssigneeId,
    long? DueAtMs,
    string CreatedByType,
    Guid CreatedById,
    Guid BindingId,
    string ProviderKey,
    string? ExternalRef,
    string? ExternalUrl,
    string? ProviderStatus,
    long? ProviderUpdatedAtMs,
    long CreatedAtMs,
    long UpdatedAtMs,
    IReadOnlyList<string> AllowedTransitions);

public sealed record WorkItemBindingSummary(
    Guid Id,
    Guid ProjectId,
    string ProviderKey,
    Guid? ConnectionId,
    string? ExternalProjectRef,
    bool IsActive,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record WorkCommentSummary(
    Guid Id,
    Guid WorkItemId,
    string AuthorType,
    Guid? AuthorId,
    string Body,
    long CreatedAtMs);

public sealed record WorkRelationSummary(
    Guid Id,
    Guid FromWorkItemId,
    Guid ToWorkItemId,
    string RelationType,
    long CreatedAtMs);

public sealed record WorkItemExecutionSummary(
    Guid Id,
    Guid AgentId,
    string? AgentName,
    string Status,
    int? ActiveAttemptNo,
    int AttemptCount,
    long LastPersistedSeq,
    long CreatedAtMs,
    long? StartedAtMs,
    long? CompletedAtMs);

/// <summary>
/// E8 Work Management Core 端点（S3）。
/// </summary>
/// <remarks>
/// <para>
/// 路由纪律（E8 §5.1，<b>本文件的每条写路径都必须遵守</b>）：
/// <list type="bullet">
///   <item>CREATE → <c>project.active_binding</c>（没有 active binding 时按 F3 建 builtin）</item>
///   <item>UPDATE → <c>work_item.binding</c>（永不看 active binding）</item>
/// </list>
/// 切 Provider 后旧 WorkItem 仍走旧 Provider，因此历史数据不会因为切换而「路由到不认识的系统」。
/// </para>
/// <para>
/// 代码里<b>不出现</b> <c>provider_key == "jira"</c> 这类分支（F4）：
/// 所有差异都在 <see cref="IWorkManagementProvider"/> 实现里。
/// </para>
/// </remarks>
public static class WorkItemEndpoints
{
    private const int MaxPageSize = 100;
    private const int DefaultListSize = 50;
    private const int DefaultDeadlineSeconds = 600;

    public static void MapWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        // Provider 目录：已注册 Provider 的自描述（UI 用它渲染「切换 Provider」下拉）
        app.MapGet("/work-management/providers", ListProvidersAsync)
            .WithTags("Work.Providers")
            .RequireAuthorization();

        // Project 维度的 binding
        RouteGroupBuilder bindingGroup = app.MapGroup("/projects/{projectId:guid}/work-management/bindings")
            .WithTags("Work.Bindings")
            .RequireAuthorization();

        bindingGroup.MapGet("", ListBindingsAsync);
        bindingGroup.MapPost("", ChangeProviderBindingAsync);

        // Project 维度的 WorkItem
        RouteGroupBuilder projectItemGroup = app.MapGroup("/projects/{projectId:guid}/work-items")
            .WithTags("Work.Items")
            .RequireAuthorization();

        projectItemGroup.MapGet("", ListWorkItemsAsync);
        projectItemGroup.MapPost("", CreateWorkItemAsync);

        // 搜索（放在 /{id:guid} 之前；{id:guid} 约束本身也不会吃掉 "search"）
        app.MapGet("/work-items/search", SearchWorkItemsAsync)
            .WithTags("Work.Items")
            .RequireAuthorization();

        // 单个 WorkItem
        RouteGroupBuilder itemGroup = app.MapGroup("/work-items/{id:guid}")
            .WithTags("Work.Items")
            .RequireAuthorization();

        itemGroup.MapGet("", GetWorkItemAsync);
        itemGroup.MapPatch("", UpdateWorkItemAsync);
        itemGroup.MapPost("/transition", TransitionWorkItemAsync);
        itemGroup.MapGet("/comments", ListCommentsAsync);
        itemGroup.MapPost("/comments", AddCommentAsync);
        itemGroup.MapPost("/relations", CreateRelationAsync);
        itemGroup.MapGet("/executions", ListExecutionsAsync);
        itemGroup.MapPost("/assign", AssignAsync);
    }

    // ════════════════════════ Provider 目录 ════════════════════════

    private static IResult ListProvidersAsync(WorkManagementProviderRegistry registry)
    {
        return Results.Ok(registry.List());
    }

    // ════════════════════════ Binding ════════════════════════

    private static async Task<IResult> ListBindingsAsync(
        Guid projectId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        Access access = await RequireProjectMemberAsync(projectId, db, authorizer, http, ct);

        if (access.Error is not null)
        {
            return access.Error;
        }

        List<DbWorkItemBinding> rows = await db.WorkItemBindings
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.IsActive)
            .ThenBy(b => b.ProviderKey)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(ToBindingSummary).ToList());
    }

    /// <summary>
    /// E8 §5.2：切换 Provider（老 binding 置 inactive，新 binding 置 active）。
    /// </summary>
    /// <remarks>
    /// 关键：<c>uq_project_active_work_provider</c> 是<b>立即生效</b>的唯一索引，
    /// 因此必须先把老 active 置 false 再置新 active，且两步在同一个事务里。
    /// 用子查询一次 UPDATE 而不是「EF 加载-改-保存」，是为了避免 EF 的 UPDATE
    /// 顺序不确定导致瞬时出现两条 active 而被索引拒绝。
    /// </remarks>
    private static async Task<IResult> ChangeProviderBindingAsync(
        Guid projectId,
        ChangeWorkProviderBindingRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        WorkManagementProviderRegistry registry,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Access access = await RequireProjectOwnerAsync(projectId, db, authorizer, http, ct);

        if (access.Error is not null)
        {
            return access.Error;
        }

        string providerKey = request.ProviderKey?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(providerKey))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "provider_key 不能为空");
        }

        if (!registry.TryGet(providerKey, out IWorkManagementProvider provider))
        {
            return ApiErrors.BadRequest(ApiErrors.UnknownWorkProvider,
                $"provider_key 未注册：{providerKey}（已注册：{string.Join(", ", registry.Keys)}）");
        }

        Guid? connectionId = request.ConnectionId;
        string? settingsJson = request.Settings?.GetRawText();

        if (connectionId is { } cid)
        {
            bool connectionExists = await db.WorkManagementConnections.AnyAsync(c => c.Id == cid, ct);

            if (!connectionExists)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "connection_id 不存在");
            }
        }

        var bindingContext = new WorkItemBindingContext(
            BindingId: Guid.Empty, // 新建时还没有 id；provider 校验不应依赖它
            ProviderKey: providerKey,
            ConnectionId: connectionId,
            ExternalProjectRef: request.ExternalProjectRef,
            SettingsJson: settingsJson);

        string? bindingError = provider.ValidateBinding(bindingContext);

        if (bindingError is not null)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, bindingError);
        }

        Guid userId = http.RequireUserId();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // ① 老 active 全部下台（立即生效，先于 ②）
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE work_item_bindings
               SET is_active = false, updated_at = {now}
               WHERE project_id = {projectId} AND is_active = true", ct);

        // ② 复用同 provider 的历史 binding（UNIQUE(project_id, provider_key)），否则新建
        DbWorkItemBinding? target = await db.WorkItemBindings
            .FirstOrDefaultAsync(b => b.ProjectId == projectId && b.ProviderKey == providerKey, ct);

        bool created = target is null;

        if (target is null)
        {
            target = new DbWorkItemBinding
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                ProviderKey = providerKey,
                CreatedAt = now,
            };
            db.WorkItemBindings.Add(target);
        }

        target.ConnectionId = connectionId;
        target.ExternalProjectRef = request.ExternalProjectRef;
        target.Settings = settingsJson;
        target.IsActive = true;
        target.UpdatedAt = now;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.WorkProviderBindingChanged,
            TargetType: "work_item_binding", TargetId: target.Id,
            Detail: new
            {
                project_id = projectId,
                provider_key = providerKey,
                created = created,
                connection_id = connectionId,
            }));

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(ToBindingSummary(target));
    }

    // ════════════════════════ WorkItem 列表 / 创建 ════════════════════════

    private static async Task<IResult> ListWorkItemsAsync(
        Guid projectId,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "type")] string? type,
        [FromQuery(Name = "assignee_type")] string? assigneeType,
        [FromQuery(Name = "assignee_id")] Guid? assigneeId,
        [FromQuery(Name = "limit")] int? limit,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        Access access = await RequireProjectMemberAsync(projectId, db, authorizer, http, ct);

        if (access.Error is not null)
        {
            return access.Error;
        }

        IQueryable<DbWorkItem> query = db.WorkItems.Where(w => w.ProjectId == projectId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!WorkItemStatusMap.TryParse(status, out WorkItemStatus parsedStatus))
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "status 非法");
            }

            query = query.Where(w => w.Status == parsedStatus.ToDbValue());
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!WorkItemTypeMap.TryParse(type, out WorkItemType parsedType))
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "type 非法");
            }

            query = query.Where(w => w.Type == parsedType.ToDbValue());
        }

        if (!string.IsNullOrWhiteSpace(assigneeType))
        {
            if (!WorkAssigneeTypeMap.TryParse(assigneeType, out WorkAssigneeType parsedAssignee))
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "assignee_type 非法");
            }

            query = query.Where(w => w.AssigneeType == parsedAssignee.ToDbValue());
        }

        if (assigneeId is { } aid)
        {
            query = query.Where(w => w.AssigneeId == aid);
        }

        int take = Math.Clamp(limit ?? DefaultListSize, 1, MaxPageSize);

        List<DbWorkItem> rows = await query
            .OrderByDescending(w => w.UpdatedAt)
            .Take(take)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(ToWorkItemSummary).ToList());
    }

    private static async Task<IResult> CreateWorkItemAsync(
        Guid projectId,
        CreateWorkItemRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        WorkManagementProviderRegistry registry,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Access access = await RequireProjectMemberAsync(projectId, db, authorizer, http, ct);

        if (access.Error is not null)
        {
            return access.Error;
        }

        if (!WorkItemTypeMap.TryParse(request.Type, out WorkItemType itemType))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "type 必须是 TASK / STORY / BUG / EPIC");
        }

        string title = request.Title?.Trim() ?? string.Empty;

        if (title.Length == 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "title 不能为空");
        }

        // assignee 成对校验（DDL 也有 CHECK，但先给出可读错误而不是 23514）
        string? assigneeTypeValue = null;
        Guid? assigneeId = null;
        WorkAssigneeType? assigneeEnum = null;

        if (request.AssigneeType is not null || request.AssigneeId is not null)
        {
            if (!WorkAssigneeTypeMap.TryParse(request.AssigneeType, out WorkAssigneeType assigneeType))
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "assignee_type 必须是 HUMAN / AGENT");
            }

            if (request.AssigneeId is not { } parsedAssigneeId)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                    "assignee_type 与 assignee_id 必须同时提供");
            }

            string? assigneeError = await ValidateAssigneeAsync(db, assigneeType, parsedAssigneeId, ct);

            if (assigneeError is not null)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, assigneeError);
            }

            assigneeTypeValue = assigneeType.ToDbValue();
            assigneeId = parsedAssigneeId;
            assigneeEnum = assigneeType;
        }

        // ① CREATE 走 active binding（E8 §5.1）
        DbWorkItemBinding binding = await EnsureActiveBindingAsync(db, projectId, ct);

        if (!registry.TryGet(binding.ProviderKey, out IWorkManagementProvider provider))
        {
            return ApiErrors.BadRequest(ApiErrors.UnknownWorkProvider,
                $"active binding 的 provider_key 未注册：{binding.ProviderKey}");
        }

        var bindingContext = ToBindingContext(binding);

        var input = new ProviderWorkItemInput(
            Type: itemType,
            Title: title,
            Description: request.Description,
            AssigneeType: assigneeEnum,
            AssigneeId: assigneeId,
            DueAt: request.DueAt);

        ProviderWriteOutcome outcome = provider.OnCreate(input, bindingContext);

        // Provider 创建时若未裁决状态，落到 canonical 起点 OPEN
        WorkItemStatus createdStatus = outcome.Status ?? WorkItemStatus.OPEN;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var item = new DbWorkItem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Type = itemType.ToDbValue(),
            Title = title,
            Description = request.Description,
            Status = createdStatus.ToDbValue(),
            CanonicalStatusCategory = createdStatus.ToCanonicalCategoryDbValue(),
            AssigneeType = assigneeTypeValue,
            AssigneeId = assigneeId,
            DueAt = request.DueAt,
            CreatedByType = AuditActorTypes.User,
            CreatedById = http.RequireUserId(),
            BindingId = binding.Id,
            ProviderKey = binding.ProviderKey,
            ExternalRef = outcome.ExternalRef,
            ExternalUrl = outcome.ExternalUrl,
            ProviderStatus = outcome.ProviderStatus,
            ProviderUpdatedAt = outcome.ProviderStatus is null ? null : now,
            ProviderMeta = outcome.ProviderMetaJson,
            SearchText = WorkItemSearchText.Build(title, request.Description),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.WorkItems.Add(item);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, item.CreatedById, AuditActions.WorkItemCreated,
            TargetType: "work_item", TargetId: item.Id,
            Detail: new
            {
                project_id = projectId,
                type = item.Type,
                binding_id = binding.Id,
                provider_key = binding.ProviderKey,
            }));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // 唯一可能撞的是 uq_work_items_binding_external（external_ref 重复）
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                "同一 binding 下 external_ref 重复");
        }

        return Results.Created($"/work-items/{item.Id}", ToWorkItemSummary(item));
    }

    // ════════════════════════ 单个 WorkItem ════════════════════════

    private static async Task<IResult> GetWorkItemAsync(
        Guid id,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        (DbWorkItem? item, IResult? error) = await LoadItemWithAccessAsync(id, db, authorizer, http, ct);

        if (error is not null)
        {
            return error;
        }

        return Results.Ok(ToWorkItemSummary(item!));
    }

    private static async Task<IResult> UpdateWorkItemAsync(
        Guid id,
        UpdateWorkItemRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        WorkManagementProviderRegistry registry,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        (DbWorkItem? item, IResult? error) = await LoadItemWithAccessAsync(id, db, authorizer, http, ct);

        if (error is not null)
        {
            return error;
        }

        DbWorkItem target = item!;

        if (!WorkItemStatusMap.TryParse(target.Status, out WorkItemStatus currentStatus))
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, $"work item 状态不可识别：{target.Status}");
        }

        // 状态若在 PATCH 里改，同样必须过状态机
        WorkItemStatus? nextStatus = null;

        if (request.Status is not null)
        {
            if (!WorkItemStatusMap.TryParse(request.Status, out WorkItemStatus parsedNext))
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "status 非法");
            }

            string? transitionError = WorkItemStateMachine.WhyCannotTransition(currentStatus, parsedNext);

            if (transitionError is not null)
            {
                return ApiErrors.ConflictResult(ApiErrors.Conflict, transitionError);
            }

            nextStatus = parsedNext;
        }

        string? title = request.Title?.Trim();

        if (title is not null && title.Length == 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "title 不能为空");
        }

        string? assigneeTypeValue = target.AssigneeType;
        Guid? assigneeId = target.AssigneeId;
        bool assigneeTouched = false;

        if (request.ClearAssignee == true)
        {
            assigneeTypeValue = null;
            assigneeId = null;
            assigneeTouched = true;
        }
        else if (request.AssigneeType is not null || request.AssigneeId is not null)
        {
            if (!WorkAssigneeTypeMap.TryParse(request.AssigneeType, out WorkAssigneeType assigneeType))
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "assignee_type 必须是 HUMAN / AGENT");
            }

            if (request.AssigneeId is not { } parsedAssigneeId)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                    "assignee_type 与 assignee_id 必须同时提供");
            }

            string? assigneeError = await ValidateAssigneeAsync(db, assigneeType, parsedAssigneeId, ct);

            if (assigneeError is not null)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, assigneeError);
            }

            assigneeTypeValue = assigneeType.ToDbValue();
            assigneeId = parsedAssigneeId;
            assigneeTouched = true;
        }

        bool clearDueAt = request.ClearDueAt == true;
        DateTimeOffset? dueAt = clearDueAt ? null : request.DueAt ?? target.DueAt;

        // ② UPDATE 走 work_item.binding（E8 §5.1）——这里刻意不查 active binding
        DbWorkItemBinding? binding = await db.WorkItemBindings
            .FirstOrDefaultAsync(b => b.Id == target.BindingId, ct);

        if (binding is null)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                "work item 的 binding 已不存在，无法路由更新（数据不一致）");
        }

        if (!registry.TryGet(binding.ProviderKey, out IWorkManagementProvider provider))
        {
            return ApiErrors.BadRequest(ApiErrors.UnknownWorkProvider,
                $"work item binding 的 provider_key 未注册：{binding.ProviderKey}");
        }

        var changes = new ProviderWorkItemChanges(
            Title: title,
            Description: request.Description,
            Status: nextStatus,
            AssigneeType: assigneeTouched && assigneeTypeValue is not null
                ? WorkAssigneeTypeMap.TryParse(assigneeTypeValue, out WorkAssigneeType parsedAt) ? parsedAt : null
                : null,
            AssigneeId: assigneeId,
            ClearAssignee: request.ClearAssignee == true,
            DueAt: dueAt,
            ClearDueAt: clearDueAt);

        ProviderWriteOutcome outcome = provider.OnUpdate(
            new ProviderWorkItemRef(binding.ProviderKey, target.Id, target.ExternalRef),
            changes,
            ToBindingContext(binding));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (title is not null)
        {
            target.Title = title;
        }

        if (request.Description is not null)
        {
            target.Description = request.Description;
        }

        // 状态优先级：调用方显式请求 > Provider 裁决 > 保持原状态
        // （保持原状态这一档是必需的：只改描述时 nextStatus 与 outcome.Status 都是 null）
        WorkItemStatus effectiveStatus = nextStatus ?? outcome.Status ?? currentStatus;

        target.Status = effectiveStatus.ToDbValue();
        target.CanonicalStatusCategory = effectiveStatus.ToCanonicalCategoryDbValue();
        target.AssigneeType = assigneeTypeValue;
        target.AssigneeId = assigneeId;
        target.DueAt = dueAt;
        target.ExternalRef = outcome.ExternalRef ?? target.ExternalRef;
        target.ExternalUrl = outcome.ExternalUrl ?? target.ExternalUrl;
        target.ProviderStatus = outcome.ProviderStatus ?? target.ProviderStatus;
        target.ProviderMeta = outcome.ProviderMetaJson ?? target.ProviderMeta;
        target.ProviderUpdatedAt = outcome.ProviderStatus is null ? target.ProviderUpdatedAt : now;
        target.SearchText = WorkItemSearchText.Build(target.Title, target.Description);
        target.UpdatedAt = now;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, http.RequireUserId(), AuditActions.WorkItemUpdated,
            TargetType: "work_item", TargetId: target.Id,
            Detail: new
            {
                provider_key = binding.ProviderKey,
                binding_id = binding.Id,
                status = target.Status,
            }));

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToWorkItemSummary(target));
    }

    private static async Task<IResult> TransitionWorkItemAsync(
        Guid id,
        TransitionWorkItemRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        WorkManagementProviderRegistry registry,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        (DbWorkItem? item, IResult? error) = await LoadItemWithAccessAsync(id, db, authorizer, http, ct);

        if (error is not null)
        {
            return error;
        }

        DbWorkItem target = item!;

        if (!WorkItemStatusMap.TryParse(request.Status, out WorkItemStatus nextStatus))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "status 必须是 OPEN / IN_PROGRESS / IN_REVIEW / DONE / CLOSED");
        }

        if (!WorkItemStatusMap.TryParse(target.Status, out WorkItemStatus currentStatus))
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, $"work item 状态不可识别：{target.Status}");
        }

        string? transitionError = WorkItemStateMachine.WhyCannotTransition(currentStatus, nextStatus);

        if (transitionError is not null)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, transitionError);
        }

        // 状态迁移同样必须走 work_item.binding 的 Provider：
        // 对外部 Provider（E9 Jira）这是把本地状态推回去的唯一时机，绕过它 = 两边长期漂移。
        DbWorkItemBinding? binding = await db.WorkItemBindings
            .FirstOrDefaultAsync(b => b.Id == target.BindingId, ct);

        if (binding is null)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                "work item 的 binding 已不存在，无法路由状态迁移（数据不一致）");
        }

        if (!registry.TryGet(binding.ProviderKey, out IWorkManagementProvider provider))
        {
            return ApiErrors.BadRequest(ApiErrors.UnknownWorkProvider,
                $"work item binding 的 provider_key 未注册：{binding.ProviderKey}");
        }

        provider.OnUpdate(
            new ProviderWorkItemRef(binding.ProviderKey, target.Id, target.ExternalRef),
            new ProviderWorkItemChanges(
                Title: null,
                Description: null,
                Status: nextStatus,
                AssigneeType: null,
                AssigneeId: null,
                ClearAssignee: false,
                DueAt: null,
                ClearDueAt: false),
            ToBindingContext(binding));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        target.Status = nextStatus.ToDbValue();
        target.CanonicalStatusCategory = nextStatus.ToCanonicalCategoryDbValue();
        target.UpdatedAt = now;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, http.RequireUserId(), AuditActions.WorkItemStatusChanged,
            TargetType: "work_item", TargetId: target.Id,
            Detail: new { from = currentStatus.ToDbValue(), to = nextStatus.ToDbValue() }));

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToWorkItemSummary(target));
    }

    // ════════════════════════ 搜索 ════════════════════════

    private static async Task<IResult> SearchWorkItemsAsync(
        [FromQuery(Name = "project_id")] Guid projectId,
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "limit")] int? limit,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        if (projectId == Guid.Empty)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "project_id 不能为空");
        }

        Access access = await RequireProjectMemberAsync(projectId, db, authorizer, http, ct);

        if (access.Error is not null)
        {
            return access.Error;
        }

        string keyword = q?.Trim() ?? string.Empty;

        if (keyword.Length == 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "q 不能为空");
        }

        int take = Math.Clamp(limit ?? DefaultListSize, 1, MaxPageSize);

        // 全文索引 + ILIKE 兜底：'simple' 配置不切分中日韩文字，
        // 只靠 tsquery 会让中文标题搜不到自己的子串。
        List<DbWorkItem> rows = await db.WorkItems
            .FromSqlInterpolated($@"
                SELECT * FROM work_items
                WHERE project_id = {projectId}
                  AND (
                    to_tsvector('simple', search_text) @@ plainto_tsquery('simple', {keyword})
                    OR search_text ILIKE '%' || {keyword} || '%'
                  )
                ORDER BY updated_at DESC
                LIMIT {take}")
            .ToListAsync(ct);

        return Results.Ok(rows.Select(ToWorkItemSummary).ToList());
    }

    // ════════════════════════ 评论 ════════════════════════

    private static async Task<IResult> ListCommentsAsync(
        Guid id,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        (DbWorkItem? item, IResult? error) = await LoadItemWithAccessAsync(id, db, authorizer, http, ct);

        if (error is not null)
        {
            return error;
        }

        List<DbWorkComment> rows = await db.WorkComments
            .Where(c => c.WorkItemId == item!.Id)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(ToCommentSummary).ToList());
    }

    private static async Task<IResult> AddCommentAsync(
        Guid id,
        AddWorkCommentRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        (DbWorkItem? item, IResult? error) = await LoadItemWithAccessAsync(id, db, authorizer, http, ct);

        if (error is not null)
        {
            return error;
        }

        string body = request.Body?.Trim() ?? string.Empty;

        if (body.Length == 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "body 不能为空");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid userId = http.RequireUserId();

        var comment = new DbWorkComment
        {
            Id = Guid.NewGuid(),
            WorkItemId = item!.Id,
            AuthorType = WorkCommentAuthorTypeMap.HumanValue,
            AuthorId = userId,
            Body = body,
            CreatedAt = now,
        };

        db.WorkComments.Add(comment);

        item.UpdatedAt = now;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.WorkCommentAdded,
            TargetType: "work_item", TargetId: item.Id,
            Detail: new { comment_id = comment.Id }));

        await db.SaveChangesAsync(ct);

        return Results.Created($"/work-items/{item.Id}/comments", ToCommentSummary(comment));
    }

    // ════════════════════════ 关联 ════════════════════════

    private static async Task<IResult> CreateRelationAsync(
        Guid id,
        CreateWorkRelationRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        (DbWorkItem? item, IResult? error) = await LoadItemWithAccessAsync(id, db, authorizer, http, ct);

        if (error is not null)
        {
            return error;
        }

        DbWorkItem from = item!;

        if (request.ToWorkItemId == from.Id)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "不能与自身建立关联");
        }

        if (!WorkRelationTypeMap.TryParse(request.RelationType, out WorkRelationType relationType))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "relation_type 必须是 BLOCKS / BLOCKED_BY / RELATES_TO / PARENT_OF / CHILD_OF");
        }

        DbWorkItem? to = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == request.ToWorkItemId, ct);

        if (to is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "目标 work item 不存在");
        }

        if (to.ProjectId != from.ProjectId)
        {
            // 跨 project 关联会让「按 project 过滤」的列表出现幽灵依赖
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "只能与同一 project 内的 work item 建立关联");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        WorkRelationType inverse = relationType.Inverse();

        var pairs = new List<(Guid FromId, Guid ToId, WorkRelationType Type)>
        {
            (from.Id, to.Id, relationType),
            (to.Id, from.Id, inverse),
        };

        var created = new List<DbWorkRelation>();

        foreach ((Guid fromId, Guid toId, WorkRelationType type) in pairs)
        {
            string typeValue = type.ToDbValue();

            bool exists = await db.WorkRelations.AnyAsync(
                r => r.FromWorkItemId == fromId && r.ToWorkItemId == toId && r.RelationType == typeValue, ct);

            if (exists)
            {
                continue;
            }

            var relation = new DbWorkRelation
            {
                Id = Guid.NewGuid(),
                FromWorkItemId = fromId,
                ToWorkItemId = toId,
                RelationType = typeValue,
                CreatedAt = now,
            };

            db.WorkRelations.Add(relation);
            created.Add(relation);
        }

        if (created.Count == 0)
        {
            return Results.Ok(new { created = 0, message = "关联已存在" });
        }

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, http.RequireUserId(), AuditActions.WorkRelationCreated,
            TargetType: "work_item", TargetId: from.Id,
            Detail: new
            {
                to_work_item_id = to.Id,
                relation_type = relationType.ToDbValue(),
                inverse = inverse.ToDbValue(),
            }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/work-items/{from.Id}/relations",
            created.Select(r => new WorkRelationSummary(
                r.Id, r.FromWorkItemId, r.ToWorkItemId, r.RelationType,
                r.CreatedAt.ToUnixTimeMilliseconds())).ToList());
    }

    // ════════════════════════ WorkItem ↔ Execution ════════════════════════

    private static async Task<IResult> ListExecutionsAsync(
        Guid id,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        (DbWorkItem? item, IResult? error) = await LoadItemWithAccessAsync(id, db, authorizer, http, ct);

        if (error is not null)
        {
            return error;
        }

        var rows = await db.AgentExecutions
            .Where(e => e.WorkItemRef == item!.Id)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new
            {
                e.Id,
                e.AgentId,
                e.Status,
                e.ActiveAttemptNo,
                e.AttemptCount,
                e.LastPersistedSeq,
                e.CreatedAt,
                e.StartedAt,
                e.CompletedAt,
            })
            .ToListAsync(ct);

        Dictionary<Guid, string> agentNames = await db.Agents
            .Where(a => rows.Select(r => r.AgentId).Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        return Results.Ok(rows.Select(r => new WorkItemExecutionSummary(
            r.Id,
            r.AgentId,
            agentNames.TryGetValue(r.AgentId, out string? name) ? name : null,
            r.Status,
            r.ActiveAttemptNo,
            r.AttemptCount,
            r.LastPersistedSeq,
            r.CreatedAt.ToUnixTimeMilliseconds(),
            r.StartedAt?.ToUnixTimeMilliseconds(),
            r.CompletedAt?.ToUnixTimeMilliseconds())).ToList());
    }

    /// <summary>
    /// Work Delivery 主链路入口：把 WorkItem 指派给 Agent 并创建 Execution。
    /// </summary>
    private static async Task<IResult> AssignAsync(
        Guid id,
        AssignWorkItemRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        OutboxWriter outboxWriter,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        (DbWorkItem? item, IResult? error) = await LoadItemWithAccessAsync(id, db, authorizer, http, ct);

        if (error is not null)
        {
            return error;
        }

        DbWorkItem target = item!;

        if (!WorkItemStatusMap.TryParse(target.Status, out WorkItemStatus currentStatus))
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, $"work item 状态不可识别：{target.Status}");
        }

        if (currentStatus is WorkItemStatus.CLOSED)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, "已关闭的 work item 不能再次指派");
        }

        DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == request.AgentId, ct);

        if (agent is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不存在");
        }

        if (!AgentLifecycleMap.TryParse(agent.Lifecycle, out AgentLifecycle lifecycle) ||
            lifecycle is not AgentLifecycle.Active)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"agent lifecycle={agent.Lifecycle}，不能被指派新工作");
        }

        int deadlineS = request.DeadlineS ?? DefaultDeadlineSeconds;

        if (deadlineS is < 30 or > 7200)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "deadline_s 必须在 30-7200 秒之间");
        }

        WorkDelivery.AssignmentResult result = await WorkDelivery.AssignAsync(
            db, outboxWriter, audit, http, target, agent, http.RequireUserId(), deadlineS, ct);

        return Results.Ok(new
        {
            work_item = ToWorkItemSummary(target),
            trigger_id = result.TriggerId,
            collaboration_request_id = result.CollaborationRequestId,
            execution_id = result.ExecutionId,
            idempotent = result.Idempotent,
        });
    }

    // ════════════════════════ helpers ════════════════════════

    /// <summary>Project 访问判定结果（<see cref="Error"/> 非空即失败）。</summary>
    private sealed record Access(Project? Project, IResult? Error);

    private static async Task<Access> RequireProjectMemberAsync(
        Guid projectId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return new Access(null, ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在"));
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        return roles.IsProjectMember
            ? new Access(project, null)
            : new Access(null, ApiErrors.Forbidden(ApiErrors.NotMember, "你不是该项目成员"));
    }

    private static async Task<Access> RequireProjectOwnerAsync(
        Guid projectId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        Access access = await RequireProjectMemberAsync(projectId, db, authorizer, http, ct);

        if (access.Error is not null)
        {
            return access;
        }

        Guid userId = http.RequireUserId();
        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, access.Project!, ct);

        return roles.CanManageProject
            ? access
            : new Access(null, ApiErrors.Forbidden(ApiErrors.NotOwner, "只有 project owner 能切换 Provider"));
    }

    /// <summary>加载 WorkItem 并校验当前用户是其 project 成员。</summary>
    private static async Task<(DbWorkItem? Item, IResult? Error)> LoadItemWithAccessAsync(
        Guid id,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        DbWorkItem? item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);

        if (item is null)
        {
            return (null, ApiErrors.NotFoundResult(ApiErrors.NotFound, "work item 不存在"));
        }

        Access access = await RequireProjectMemberAsync(item.ProjectId, db, authorizer, http, ct);

        return access.Error is not null ? (null, access.Error) : (item, null);
    }

    /// <summary>
    /// F3：Project 默认 binding = builtin。
    /// </summary>
    /// <remarks>
    /// 惰性创建（首次用到时）而不是在 project 创建时写一行：E1 的 project 创建路径
    /// 不应该知道 Work Management 的存在，否则加 Provider 又要回头改 Identity 模块。
    /// </remarks>
    private static async Task<DbWorkItemBinding> EnsureActiveBindingAsync(
        MateOSDbContext db,
        Guid projectId,
        CancellationToken ct)
    {
        DbWorkItemBinding? active = await db.WorkItemBindings
            .FirstOrDefaultAsync(b => b.ProjectId == projectId && b.IsActive, ct);

        if (active is not null)
        {
            return active;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var binding = new DbWorkItemBinding
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ProviderKey = BuiltInProviderKey.Value,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.WorkItemBindings.Add(binding);

        return binding;
    }

    private static async Task<string?> ValidateAssigneeAsync(
        MateOSDbContext db,
        WorkAssigneeType assigneeType,
        Guid assigneeId,
        CancellationToken ct)
    {
        switch (assigneeType)
        {
            case WorkAssigneeType.HUMAN:
            {
                bool exists = await db.Users.AnyAsync(u => u.Id == assigneeId, ct);
                return exists ? null : "assignee_id 对应的 user 不存在";
            }

            case WorkAssigneeType.AGENT:
            {
                DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == assigneeId, ct);

                if (agent is null)
                {
                    return "assignee_id 对应的 agent 不存在";
                }

                return AgentLifecycleMap.TryParse(agent.Lifecycle, out AgentLifecycle lifecycle)
                       && lifecycle is AgentLifecycle.Active
                    ? null
                    : $"agent lifecycle={agent.Lifecycle}，不能作为 assignee";
            }

            default:
                return "assignee_type 非法";
        }
    }

    private static WorkItemBindingContext ToBindingContext(DbWorkItemBinding binding) =>
        new(binding.Id, binding.ProviderKey, binding.ConnectionId, binding.ExternalProjectRef, binding.Settings);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true;

    private static WorkItemSummary ToWorkItemSummary(DbWorkItem item)
    {
        WorkItemStatus status = WorkItemStatusMap.TryParse(item.Status, out WorkItemStatus parsed)
            ? parsed
            : WorkItemStatus.OPEN;

        return new WorkItemSummary(
            item.Id,
            item.ProjectId,
            item.Type,
            item.Title,
            item.Description,
            item.Status,
            item.CanonicalStatusCategory,
            item.AssigneeType,
            item.AssigneeId,
            item.DueAt?.ToUnixTimeMilliseconds(),
            item.CreatedByType,
            item.CreatedById,
            item.BindingId,
            item.ProviderKey,
            item.ExternalRef,
            item.ExternalUrl,
            item.ProviderStatus,
            item.ProviderUpdatedAt?.ToUnixTimeMilliseconds(),
            item.CreatedAt.ToUnixTimeMilliseconds(),
            item.UpdatedAt.ToUnixTimeMilliseconds(),
            WorkItemStateMachine.AllowedTargets(status).Select(s => s.ToDbValue()).ToList());
    }

    private static WorkItemBindingSummary ToBindingSummary(DbWorkItemBinding binding) => new(
        binding.Id,
        binding.ProjectId,
        binding.ProviderKey,
        binding.ConnectionId,
        binding.ExternalProjectRef,
        binding.IsActive,
        binding.CreatedAt.ToUnixTimeMilliseconds(),
        binding.UpdatedAt.ToUnixTimeMilliseconds());

    private static WorkCommentSummary ToCommentSummary(DbWorkComment comment) => new(
        comment.Id,
        comment.WorkItemId,
        comment.AuthorType,
        comment.AuthorId,
        comment.Body,
        comment.CreatedAt.ToUnixTimeMilliseconds());
}
