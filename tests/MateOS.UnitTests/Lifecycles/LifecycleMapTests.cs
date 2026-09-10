using MateOS.Contracts.Protocol;
using MateOS.Domain.Lifecycles;

namespace MateOS.UnitTests.Lifecycles;

/// <summary>
/// 保证领域枚举与 DB / 协议的字符串取值严格一致。
/// 这三张枚举一旦与 DDL CHECK 漂移，写库会在运行期才炸——因此在此处钉死。
/// </summary>
public sealed class LifecycleMapTests
{
    [Fact]
    public void 执行状态应映射为DDL中的取值()
    {
        Assert.Equal("PENDING", ExecutionStatus.Pending.ToDbValue());
        Assert.Equal("RUNNING", ExecutionStatus.Running.ToDbValue());
        Assert.Equal("SUCCEEDED", ExecutionStatus.Succeeded.ToDbValue());
        Assert.Equal("FAILED", ExecutionStatus.Failed.ToDbValue());
        Assert.Equal("CANCELLED", ExecutionStatus.Cancelled.ToDbValue());
        Assert.Equal("TIMEOUT", ExecutionStatus.Timeout.ToDbValue());
    }

    [Fact]
    public void attempt状态应只有五个取值()
    {
        // SYSTEM_DESIGN §5.2：STARTED / RUNNING / COMPLETED / FAILED / INTERRUPTED
        Assert.Equal(5, Enum.GetValues<AttemptStatus>().Length);

        Assert.Equal("STARTED", AttemptStatus.Started.ToDbValue());
        Assert.Equal("RUNNING", AttemptStatus.Running.ToDbValue());
        Assert.Equal("COMPLETED", AttemptStatus.Completed.ToDbValue());
        Assert.Equal("FAILED", AttemptStatus.Failed.ToDbValue());
        Assert.Equal("INTERRUPTED", AttemptStatus.Interrupted.ToDbValue());
    }

    [Fact]
    public void 协作请求状态应收敛为六态()
    {
        // detailed/00：PENDING / ACCEPTED / REJECTED / NEED_CONTEXT / UNRESOLVED / CANCELLED
        // （不含已废弃的 EXECUTING / COMPLETED / FAILED —— 那是 v0.4.5 修掉的镜像态）
        Assert.Equal(6, Enum.GetValues<CollaborationRequestStatus>().Length);

        Assert.Equal("PENDING", CollaborationRequestStatus.Pending.ToDbValue());
        Assert.Equal("ACCEPTED", CollaborationRequestStatus.Accepted.ToDbValue());
        Assert.Equal("REJECTED", CollaborationRequestStatus.Rejected.ToDbValue());
        Assert.Equal("NEED_CONTEXT", CollaborationRequestStatus.NeedContext.ToDbValue());
        Assert.Equal("UNRESOLVED", CollaborationRequestStatus.Unresolved.ToDbValue());
        Assert.Equal("CANCELLED", CollaborationRequestStatus.Cancelled.ToDbValue());
    }

    [Theory]
    [InlineData("PENDING", ExecutionStatus.Pending)]
    [InlineData("RUNNING", ExecutionStatus.Running)]
    [InlineData("SUCCEEDED", ExecutionStatus.Succeeded)]
    [InlineData("FAILED", ExecutionStatus.Failed)]
    [InlineData("CANCELLED", ExecutionStatus.Cancelled)]
    [InlineData("TIMEOUT", ExecutionStatus.Timeout)]
    public void 执行状态解析应与序列化互为逆运算(string dbValue, ExecutionStatus expected)
    {
        Assert.True(LifecycleMap.TryParseExecution(dbValue, out ExecutionStatus parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(dbValue, parsed.ToDbValue());
    }

    [Theory]
    [InlineData("STARTED", AttemptStatus.Started)]
    [InlineData("RUNNING", AttemptStatus.Running)]
    [InlineData("COMPLETED", AttemptStatus.Completed)]
    [InlineData("FAILED", AttemptStatus.Failed)]
    [InlineData("INTERRUPTED", AttemptStatus.Interrupted)]
    public void attempt状态解析应与序列化互为逆运算(string dbValue, AttemptStatus expected)
    {
        Assert.True(LifecycleMap.TryParseAttempt(dbValue, out AttemptStatus parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(dbValue, parsed.ToDbValue());
    }

    [Theory]
    [InlineData("ACCEPTED", CollaborationRequestStatus.Accepted)]
    [InlineData("NEED_CONTEXT", CollaborationRequestStatus.NeedContext)]
    [InlineData("UNRESOLVED", CollaborationRequestStatus.Unresolved)]
    public void 协作状态解析应与序列化互为逆运算(string dbValue, CollaborationRequestStatus expected)
    {
        Assert.True(LifecycleMap.TryParseCollaboration(dbValue, out CollaborationRequestStatus parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(dbValue, parsed.ToDbValue());
    }

    [Theory]
    [InlineData("EXECUTING")]     // detailed/00 明确废弃（不再镜像 Execution status）
    [InlineData("DONE")]
    [InlineData("executing")]     // 大小写敏感：DB 存的是 SCREAMING_SNAKE
    [InlineData("")]
    [InlineData(null)]
    public void 未知取值不应被解析(string? dbValue)
    {
        Assert.False(LifecycleMap.TryParseExecution(dbValue, out _));
        Assert.False(LifecycleMap.TryParseAttempt(dbValue, out _));
        Assert.False(LifecycleMap.TryParseCollaboration(dbValue, out _));
    }

    [Fact]
    public void 终态判定应与CAS条件一致()
    {
        // I6：CAS 写作 WHERE status IN ('PENDING','RUNNING')，等价于「非终态才可写」
        Assert.False(ExecutionStatus.Pending.IsTerminal());
        Assert.False(ExecutionStatus.Running.IsTerminal());

        Assert.True(ExecutionStatus.Succeeded.IsTerminal());
        Assert.True(ExecutionStatus.Failed.IsTerminal());
        Assert.True(ExecutionStatus.Cancelled.IsTerminal());
        Assert.True(ExecutionStatus.Timeout.IsTerminal());
    }

    [Fact]
    public void 接受即终态()
    {
        // 01 §T+13：E4 建 Execution 失败也不回滚 CR —— ACCEPTED 是终态，不镜像 Execution 状态
        Assert.True(CollaborationRequestStatus.Accepted.IsTerminal());
        Assert.False(CollaborationRequestStatus.Pending.IsTerminal());
    }

    [Fact]
    public void 协议常量应与DDL取值同源()
    {
        Assert.Equal(ExecutionStatuses.Succeeded, ExecutionStatus.Succeeded.ToDbValue());
        Assert.Equal(AttemptStatuses.Interrupted, AttemptStatus.Interrupted.ToDbValue());
        Assert.Equal(CollaborationStatuses.NeedContext, CollaborationRequestStatus.NeedContext.ToDbValue());
    }
}
