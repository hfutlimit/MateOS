using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MateOS.Contracts.Protocol;

/// <summary>trigger / actor 类型（03 §2 <c>from_actor.type</c>）。</summary>
public static class ActorTypes
{
    public const string User = "USER";
    public const string Agent = "AGENT";
    public const string System = "SYSTEM";
}

/// <summary>
/// 所有 envelope 的外层信封。等价于 03 §2 的 <c>{ "type", "id", "ts", "payload" }</c>。
/// </summary>
/// <param name="Type">
/// 见 <see cref="EnvelopeTypes"/>。未知类型必须被拒收。
/// </param>
/// <param name="Id">
/// UUIDv4。<b>协议级幂等键</b>：Runtime 记录最近 1 小时 envelope.id 集合（Redis Set，TTL 1h），
/// 重复 envelope.id 立即忽略（03 §5.1）。
/// </param>
/// <param name="Ts">Unix 毫秒时间戳（03 §2 示例 <c>1736380800000</c>）。</param>
/// <param name="Payload">对应类型的 payload；序列化形状由 JSON Schema 约束。</param>
public sealed record Envelope(string Type, Guid Id, long Ts, JsonNode? Payload);

// ─────────────────────────── E4 · Collaboration 域 ───────────────────────────

/// <summary>协作参与方引用。</summary>
public sealed record ActorRef(string Type, Guid Id);

/// <summary>
/// WorkItem 引用。<c>execution</c> 可独立存在，故所有引用点均可空（I2）。
/// </summary>
/// <remarks>
/// 用<b>对象</b>而不是裸 id：Agent 需要知道这条工作来自哪个 Provider
/// （builtin 与 jira 的后续动作完全不同）。
/// <paramref name="ExternalRef"/> 可空 —— builtin 没有外部标识
/// （契约 schema：<c>contracts/schemas/execution/dispatch.json#/$defs/work_item_ref</c>）。
/// </remarks>
public sealed record WorkItemRef(string ProviderKey, string WorkItemId, string? ExternalRef);

/// <summary>请求上下文的引用集合（01 §T+6，不含各自的事实内容）。</summary>
public sealed record CollaborationContextRefs(
    Guid? ChannelId,
    long? MessageSeq,
    IReadOnlyList<Guid>? MemoryRefs,
    WorkItemRef? WorkItemRef);

/// <summary>Runtime → Agent：触发 Agent 决策。<b>此时 Execution 尚未创建</b>（01 §T+7，v0.4.3 修正）。</summary>
public sealed record CollaborationRequestPayload(
    Guid CollaborationRequestId,
    ActorRef FromActor,
    CollaborationContextRefs ContextRefs,
    IReadOnlyList<string> RequiredCapabilities,
    int? DeadlineS);

/// <summary>ACCEPT 时必须提供的三件套（01 §T+11）。</summary>
public sealed record DecisionAnalysis(bool Capability, double? ContextScore, bool Permission);

/// <summary>
/// Agent → Runtime：决策结果。
/// <c>ACCEPT</c> = 「我要不要接这个工作」（业务接受），与 <c>execution.dispatch_ack</c>（transport 收据）是两件事（03 §2 v0.7）。
/// </summary>
public sealed record CollaborationDecisionPayload(
    Guid CollaborationRequestId,
    string Decision,
    string? Reason,
    DecisionAnalysis? Analysis,
    IReadOnlyList<string>? Needs);

/// <summary>Runtime → Agent：协作请求被取消。</summary>
public sealed record CollaborationCancelledPayload(Guid CollaborationRequestId, string Reason);

/// <summary>Runtime → Agent：路由结果回执。</summary>
public sealed record CollaborationResolvedPayload(
    Guid CollaborationRequestId,
    string Status,
    Guid? TargetExecutionId,
    IReadOnlyDictionary<string, double>? Scores,
    long ResolvedAtMs);

// ─────────────────────────── E7 · Execution 域 ───────────────────────────

/// <summary>执行输入快照。</summary>
public sealed record ExecutionInput(string? Prompt, JsonNode? Params);

/// <summary>执行上下文快照（dispatch 时冻结，resume 时原样回传）。</summary>
/// <remarks>
/// <para>
/// 前三个字段是 <b>V2 的注入位</b>（记忆引用 / 近期消息 / 权限快照），S1 阶段恒为 <c>null</c>：
/// 设计原则是「上下文注入走引用」，注入本身尚未实现。
/// </para>
/// <para>
/// <paramref name="Refs"/> 是<b>原始引用集合的透传位</b>
/// （channel_id / message_seq / project_id / work_item_id）。
/// 刻意保留：在注入落地前，若只留前三个字段，这些引用会无处安放而被丢掉。
/// </para>
/// </remarks>
public sealed record ExecutionContext(
    IReadOnlyList<Guid>? MemoryRefs,
    JsonNode? RecentMessages,
    JsonNode? Permissions,
    JsonNode? Refs);

/// <summary>
/// Runtime → Agent：派发一次执行。
/// </summary>
/// <remarks>
/// 03 §2 的 payload 示例未列 <paramref name="AttemptNo"/>，但 §7.2 的 <c>dispatch_to_agent</c>
/// 实现明确带 <c>attempt_no</c>，且 <c>execution.dispatch_ack</c> 必须回带同一
/// <c>(execution_id, attempt_no)</c> 供 Runtime 回填 <c>dispatch_acked_at</c>，故此处保留。
/// </remarks>
public sealed record ExecutionDispatchPayload(
    Guid ExecutionId,
    int AttemptNo,
    Guid? CollaborationRequestId,
    WorkItemRef? WorkItemRef,
    ExecutionInput Input,
    ExecutionContext Context,
    int? DeadlineS,
    string IdempotencyKey);

/// <summary>
/// Agent → Runtime：dispatch 送达收据（03 §7.2）。
/// </summary>
/// <remarks>
/// 只有一个含义：dispatch 已送达并被接收。没有 <c>accepted=false</c> 分支，
/// 不会触发重路由——业务接受已在 <see cref="CollaborationDecisionPayload"/> 阶段完成。
/// </remarks>
public sealed record ExecutionDispatchAckPayload(
    Guid ExecutionId,
    int AttemptNo,
    bool Received,
    string? ProtocolError)
{
    /// <summary>
    /// 是否携带协议层异常（未知 execution / 过期 attempt）。
    /// </summary>
    /// <remarks>
    /// <b>必须 <see cref="JsonIgnoreAttribute"/></b>：这是给服务端判别用的派生便利属性，
    /// 不属于 wire 契约。不标的话会被序列化成 <c>is_protocol_error</c>，
    /// 而 schema 是 <c>additionalProperties: false</c> —— 契约校验会（也确实）失败。
    /// </remarks>
    [JsonIgnore]
    public bool IsProtocolError => !string.IsNullOrEmpty(ProtocolError);
}

/// <summary>Runtime → Agent：执行有输出。Agent 负责 <c>provider_event_id</c> 与单调 <c>seq</c>（03 §3.2）。</summary>
public sealed record ExecutionEventPayload(
    Guid ExecutionId,
    int AttemptNo,
    string EventType,
    string ProviderEventId,
    long Seq,
    JsonNode? Payload);

/// <summary>执行产出。</summary>
public sealed record ExecutionOutput(string? Markdown, string? Code);

/// <summary>用量统计。</summary>
public sealed record ExecutionUsage(long? TokensIn, long? TokensOut, long? DurationMs);

/// <summary>产物引用（实体走 S3 预签名，03 §6）。</summary>
public sealed record ExecutionArtifact(string Kind, string Name, string S3Key);

/// <summary>Agent → Runtime：终态 envelope。<c>status</c> 取 <see cref="ExecutionStatuses"/> 的终态值。</summary>
public sealed record ExecutionResultPayload(
    Guid ExecutionId,
    int AttemptNo,
    string Status,
    ExecutionOutput? Output,
    ExecutionUsage? Usage,
    IReadOnlyList<ExecutionArtifact>? Artifacts);

/// <summary>Agent → Runtime：执行错误。是否需要重试由 Runtime 判定（08）。</summary>
public sealed record ExecutionErrorPayload(
    Guid ExecutionId,
    int AttemptNo,
    string Code,
    string Message,
    int? RetryAfterS);

/// <summary>Runtime → Agent：取消执行。</summary>
public sealed record ExecutionCancelPayload(Guid ExecutionId, int AttemptNo, string Reason);

/// <summary>Agent → Runtime：断线重连后主动询问续传位点（03 §3.1，v0.4.3 反向协议）。</summary>
public sealed record ExecutionResumeRequestPayload(Guid ExecutionId, int AttemptNo)
{
    /// <summary>
    /// 03 §3.2 的伪码在推 <c>resume_request</c> 时额外带 <c>expected_seq</c>（gap 场景）。
    /// 正常重连时为空。
    /// </summary>
    public long? ExpectedSeq { get; init; }
}

/// <summary>dispatch 的冻结快照，resume 时原样回传（03 §3.3）。</summary>
public sealed record ExecutionDispatchSnapshot(
    Guid ExecutionId,
    ExecutionInput Input,
    ExecutionContext Context,
    int? DeadlineS);

/// <summary>
/// Runtime → Agent：续传位点。
/// </summary>
/// <remarks>
/// <paramref name="LastPersistedSeq"/> 是 <b>连续位点</b>，不是 <c>MAX(seq)</c>（03 §3.2 修复 P1-2）：
/// Agent 必须从 <c>LastPersistedSeq + 1</c> 续发。
/// </remarks>
public sealed record ExecutionResumeAckPayload(
    Guid ExecutionId,
    int AttemptNo,
    long LastPersistedSeq,
    ExecutionDispatchSnapshot? Snapshot);

/// <summary>Agent → Runtime：纯 activity 上报，只写 presence / UI 派生，不参与协议状态判定（03 §7）。</summary>
public sealed record StatusPayload(string Status, string? Reason, long? SinceMs);
