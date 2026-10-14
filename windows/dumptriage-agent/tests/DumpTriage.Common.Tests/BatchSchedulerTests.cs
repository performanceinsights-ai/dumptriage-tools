using DumpTriage.Common.Policy;

namespace DumpTriage.Common.Tests;

public class BatchSchedulerTests
{
    [Fact]
    public void Immediate_AlwaysSubmitsNow()
    {
        var config = new BatchingConfig { Mode = BatchMode.Immediate };
        var result = BatchScheduler.ShouldSubmitNow(config, DateTimeOffset.Now, TimeSpan.Zero, null);
        Assert.True(result);
    }

    [Fact]
    public void Idle_BelowThreshold_DoesNotSubmit()
    {
        var config = new BatchingConfig { Mode = BatchMode.Idle, IdleMinutes = 15 };
        var result = BatchScheduler.ShouldSubmitNow(config, DateTimeOffset.Now, TimeSpan.FromMinutes(5), null);
        Assert.False(result);
    }

    [Fact]
    public void Idle_AtOrAboveThreshold_Submits()
    {
        var config = new BatchingConfig { Mode = BatchMode.Idle, IdleMinutes = 15 };
        var result = BatchScheduler.ShouldSubmitNow(config, DateTimeOffset.Now, TimeSpan.FromMinutes(15), null);
        Assert.True(result);
    }

    [Fact]
    public void ScheduledTimeOfDay_BeforeScheduledTime_DoesNotSubmit()
    {
        var config = new BatchingConfig
        {
            Mode = BatchMode.ScheduledTimeOfDay,
            ScheduledTimesLocal = { new TimeOnly(22, 0) },
        };
        var now = new DateTimeOffset(2026, 9, 22, 18, 0, 0, TimeSpan.Zero);
        var result = BatchScheduler.ShouldSubmitNow(config, now, TimeSpan.Zero, null);
        Assert.False(result);
    }

    [Fact]
    public void ScheduledTimeOfDay_NoFlushRecordedYet_WaitsForTheNextSlot()
    {
        var config = new BatchingConfig
        {
            Mode = BatchMode.ScheduledTimeOfDay,
            ScheduledTimesLocal = { new TimeOnly(22, 0) },
        };
        var now = new DateTimeOffset(2026, 9, 22, 22, 30, 0, TimeSpan.Zero);
        var result = BatchScheduler.ShouldSubmitNow(config, now, TimeSpan.Zero, lastScheduledFlushUtc: null);
        Assert.False(result); // the caller records a first run as a flush (#9)
    }

    [Fact]
    public void ScheduledTimeOfDay_AlreadyFlushedSinceScheduledTime_DoesNotSubmitAgain()
    {
        var config = new BatchingConfig
        {
            Mode = BatchMode.ScheduledTimeOfDay,
            ScheduledTimesLocal = { new TimeOnly(22, 0) },
        };
        var scheduledInstant = new DateTimeOffset(2026, 9, 22, 22, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 9, 22, 22, 45, 0, TimeSpan.Zero);
        var lastFlush = scheduledInstant.AddMinutes(1);

        var result = BatchScheduler.ShouldSubmitNow(config, now, TimeSpan.Zero, lastFlush);
        Assert.False(result);
    }

    [Fact]
    public void ScheduledTimeOfDay_NewDayAfterPriorFlush_SubmitsAgain()
    {
        var config = new BatchingConfig
        {
            Mode = BatchMode.ScheduledTimeOfDay,
            ScheduledTimesLocal = { new TimeOnly(22, 0) },
        };
        var lastFlush = new DateTimeOffset(2026, 9, 22, 22, 1, 0, TimeSpan.Zero);
        var nextDay = new DateTimeOffset(2026, 9, 23, 22, 5, 0, TimeSpan.Zero);

        var result = BatchScheduler.ShouldSubmitNow(config, nextDay, TimeSpan.Zero, lastFlush);
        Assert.True(result);
    }
}
