namespace MateOS.Contracts.Protocol;

/// <summary>
/// Runtime ↔ Agent Connector 的 envelope 类型常量。
/// </summary>
/// <remarks>
/// 依据 docs/design/detailed/03-ws-connection-and-resume.md §2「完整 message type」。
/// 命名空间约定（SYSTEM_DESIGN §6.5）：<c>collaboration.*</c> 属 E4，<c>execution.*</c> 属 E7，严格分离。
/// 协议层技术中立：本文件只定义语义，不绑定实现语言。
/// </remarks>
public static class EnvelopeTypes
{
    // ─── 连接管理 ───
    public const string Hello = "hello";
    public const string HelloAck = "hello_ack";
    public const string HelloNack = "hello_nack";
    public const string Heartbeat = "heartbeat";

    // ─── E4 拥有：Collaboration 域 ───
    public const string CollaborationRequest = "collaboration.request";
    public const string CollaborationDecision = "collaboration.decision";
    public const string CollaborationCancelled = "collaboration.cancelled";
    public const string CollaborationResolved = "collaboration.resolved";

    // ─── E7 拥有：Execution 域 ───
    public const string ExecutionDispatch = "execution.dispatch";
    public const string ExecutionDispatchAck = "execution.dispatch_ack";
    public const string ExecutionEvent = "execution.event";
    public const string ExecutionResult = "execution.result";
    public const string ExecutionError = "execution.error";
    public const string ExecutionCancel = "execution.cancel";
    public const string ExecutionResumeRequest = "execution.resume_request";
    public const string ExecutionResumeAck = "execution.resume_ack";

    /// <summary>
    /// Agent activity 上报。纯 UI 语义，<b>不承载 ACK</b>（03 §2 v0.7 语义定型）。
    /// </summary>
    public const string Status = "status";

    /// <summary>
    /// 全部合法 envelope 类型。用于契约校验（未知类型必须被拒）。
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Hello, HelloAck, HelloNack, Heartbeat,
        CollaborationRequest, CollaborationDecision, CollaborationCancelled, CollaborationResolved,
        ExecutionDispatch, ExecutionDispatchAck, ExecutionEvent, ExecutionResult,
        ExecutionError, ExecutionCancel, ExecutionResumeRequest, ExecutionResumeAck,
        Status,
    };

    /// <summary>envelope 的合法方向（03 §2 逐条定义）。</summary>
    public static readonly IReadOnlyDictionary<string, EnvelopeDirection> Directions =
        new Dictionary<string, EnvelopeDirection>(StringComparer.Ordinal)
        {
            [Hello] = EnvelopeDirection.AgentToRuntime,
            [Heartbeat] = EnvelopeDirection.AgentToRuntime,
            [HelloAck] = EnvelopeDirection.RuntimeToAgent,
            [HelloNack] = EnvelopeDirection.RuntimeToAgent,

            [CollaborationRequest] = EnvelopeDirection.RuntimeToAgent,
            [CollaborationDecision] = EnvelopeDirection.AgentToRuntime,
            [CollaborationCancelled] = EnvelopeDirection.RuntimeToAgent,
            [CollaborationResolved] = EnvelopeDirection.RuntimeToAgent,

            [ExecutionDispatch] = EnvelopeDirection.RuntimeToAgent,
            [ExecutionDispatchAck] = EnvelopeDirection.AgentToRuntime,
            [ExecutionEvent] = EnvelopeDirection.AgentToRuntime,
            [ExecutionResult] = EnvelopeDirection.AgentToRuntime,
            [ExecutionError] = EnvelopeDirection.AgentToRuntime,
            [ExecutionCancel] = EnvelopeDirection.RuntimeToAgent,
            [ExecutionResumeRequest] = EnvelopeDirection.AgentToRuntime,
            [ExecutionResumeAck] = EnvelopeDirection.RuntimeToAgent,

            [Status] = EnvelopeDirection.AgentToRuntime,
        };
}

/// <summary>envelope 的方向语义：由谁发往谁。</summary>
public enum EnvelopeDirection
{
    /// <summary>Agent → Runtime。</summary>
    AgentToRuntime,

    /// <summary>Runtime → Agent。</summary>
    RuntimeToAgent,
}
