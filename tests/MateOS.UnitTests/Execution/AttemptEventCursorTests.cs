using MateOS.Domain.Execution;

namespace MateOS.UnitTests.Execution;

/// <summary>
/// 覆盖 detailed/03 §3.2（contiguous cursor）与 §9 的 e2e 断言点 test_004 / test_008。
/// </summary>
public sealed class AttemptEventCursorTests
{
    [Fact]
    public void 顺序事件应全部被接受并推进位点()
    {
        var cursor = new AttemptEventCursor();

        for (long seq = 1; seq <= 5; seq++)
        {
            EventIngestResult result = cursor.Ingest($"evt-{seq}", seq);

            Assert.Equal(EventIngestOutcome.Accepted, result.Outcome);
            Assert.True(result.ShouldPersist);
            Assert.Equal(seq, result.LastPersistedSeq);
        }

        Assert.Equal(5, cursor.LastPersistedSeq);
        Assert.Equal(6, cursor.ExpectedSeq);
        Assert.Equal(5, cursor.AcceptedEventCount);
    }

    [Fact]
    public void 落后于游标的事件应被静默忽略且不改变位点()
    {
        var cursor = new AttemptEventCursor(lastPersistedSeq: 5);

        EventIngestResult result = cursor.Ingest("evt-40", seq: 4);

        Assert.Equal(EventIngestOutcome.Duplicate, result.Outcome);
        Assert.Equal(DuplicateCause.SeqBehindCursor, result.Cause);
        Assert.False(result.ShouldPersist);
        Assert.Equal(5, cursor.LastPersistedSeq);
        Assert.True(result.RequiresResumeProbe is false);
    }

    [Fact]
    public void 越过游标的事件应判为gap并要求从期望位点重发()
    {
        // detailed/03 §9 test_008：Given last_persisted_seq=5, When agent sends seq=7 (skipped 6),
        //                            Then event rejected, resume_request pushed to agent
        var cursor = new AttemptEventCursor(lastPersistedSeq: 5);

        EventIngestResult result = cursor.Ingest("evt-7", seq: 7);

        Assert.Equal(EventIngestOutcome.Gap, result.Outcome);
        Assert.True(result.RequiresResumeProbe);
        Assert.Equal(6, result.ResumeFromSeq);
        Assert.False(result.ShouldPersist);

        // gap 不得推进位点——否则丢失的 seq 6 会被永久跳过（这正是 MAX(seq) 的旧 bug）
        Assert.Equal(5, cursor.LastPersistedSeq);
    }

    [Fact]
    public void gap补齐后应能继续正常接收()
    {
        var cursor = new AttemptEventCursor(lastPersistedSeq: 5);

        Assert.Equal(EventIngestOutcome.Gap, cursor.Ingest("evt-7", 7).Outcome);
        Assert.Equal(EventIngestOutcome.Accepted, cursor.Ingest("evt-6", 6).Outcome);
        Assert.Equal(EventIngestOutcome.Accepted, cursor.Ingest("evt-7", 7).Outcome);

        Assert.Equal(7, cursor.LastPersistedSeq);
    }

    [Fact]
    public void 相同事件幂等键再次出现应判为重复()
    {
        // 03 §9 test_004：同 provider_event_id 发两次 → 第二次被静默丢弃
        var cursor = new AttemptEventCursor();

        Assert.Equal(EventIngestOutcome.Accepted, cursor.Ingest("evt-dup", seq: 1).Outcome);
        EventIngestResult second = cursor.Ingest("evt-dup", seq: 1);

        Assert.Equal(EventIngestOutcome.Duplicate, second.Outcome);
        Assert.Equal(1, cursor.LastPersistedSeq);
        Assert.Equal(1, cursor.AcceptedEventCount);
    }

    [Fact]
    public void 幂等键重放但携带新seq时应按幂等键拒绝()
    {
        // UNIQUE(attempt_id, provider_event_id) 优先于 seq 判定：
        // 客户端若改发新 seq 但复用旧 provider_event_id，属客户端幂等失效，必须挡在入库之前。
        var cursor = new AttemptEventCursor();

        cursor.Ingest("evt-x", seq: 1);
        EventIngestResult replay = cursor.Ingest("evt-x", seq: 2);

        Assert.Equal(EventIngestOutcome.Duplicate, replay.Outcome);
        Assert.Equal(DuplicateCause.ProviderEventIdReplay, replay.Cause);
        Assert.Equal(1, cursor.LastPersistedSeq);
    }

    [Fact]
    public void 跨进程重建游标后旧事件不得二次入库()
    {
        // Runtime 重启后按 DB 真实状态重建：last_persisted_seq + 已落库的 provider_event_id 集合。
        var rebuilt = new AttemptEventCursor(
            lastPersistedSeq: 3,
            persistedProviderEventIds: ["evt-1", "evt-2", "evt-3"]);

        EventIngestResult replayed = rebuilt.Ingest("evt-2", seq: 2);

        Assert.Equal(EventIngestOutcome.Duplicate, replayed.Outcome);
        Assert.Equal(3, rebuilt.LastPersistedSeq);

        // 下一条新事件仍可正常接收
        Assert.Equal(EventIngestOutcome.Accepted, rebuilt.Ingest("evt-4", seq: 4).Outcome);
        Assert.Equal(4, rebuilt.LastPersistedSeq);
    }

    [Fact]
    public void 位点为零时首条事件从一接受()
    {
        // detailed/03 §3.5：last_persisted_seq=0 表示尚无任何事件落库。
        var cursor = new AttemptEventCursor();

        Assert.Equal(0, cursor.LastPersistedSeq);
        Assert.Equal(1, cursor.ExpectedSeq);
        Assert.Equal(EventIngestOutcome.Accepted, cursor.Ingest("evt-1", 1).Outcome);
    }

    [Fact]
    public void 空幂等键应被拒绝()
    {
        var cursor = new AttemptEventCursor();

        Assert.Throws<ArgumentException>(() => cursor.Ingest("  ", seq: 1));
    }

    [Fact]
    public void 构造时负位点应被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AttemptEventCursor(lastPersistedSeq: -1));
    }
}
