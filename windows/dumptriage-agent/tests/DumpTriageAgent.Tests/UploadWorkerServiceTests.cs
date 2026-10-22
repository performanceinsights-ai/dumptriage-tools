using DumpTriage.Common.Policy;
using System.Net;
using DumpTriage.Common.Config;
using DumpTriage.Common.Models;
using DumpTriage.Common.Upload;
using DumpTriageAgent.Upload;

namespace DumpTriageAgent.Tests;

public class UploadWorkerServiceTests
{
    [Fact]
    public async Task Manual_submit_while_paused_queues_the_dump_without_uploading()
    {
        // website#1492: a manual submit ran fire-and-forget straight from the pipe
        // thread and ignored the pause.
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp", DumpQueueState.PendingApproval);
        h.State.Pause();

        h.State.RequestManualSubmit(record.Id);
        await h.Worker.ProcessTrayRequestsAsync(CancellationToken.None);

        Assert.Empty(h.Server.Requests);
        Assert.Equal(DumpQueueState.Queued, (await h.ReloadAsync(record)).State);
    }

    [Fact]
    public async Task A_record_left_in_Submitting_is_requeued_at_start_without_a_retry()
    {
        // website#1485: a crash or reboot mid-upload left it in Submitting for good.
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp", DumpQueueState.Submitting);

        await h.Worker.RecoverInterruptedAsync(CancellationToken.None);

        var reloaded = await h.ReloadAsync(record);
        Assert.Equal(DumpQueueState.Queued, reloaded.State);
        Assert.Equal(0, reloaded.RetryCount);
    }

    [Fact]
    public async Task Shutdown_mid_upload_puts_the_record_back_in_the_queue()
    {
        // website#1485: the OperationCanceledException at shutdown skipped every
        // state write and left the record in Submitting.
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp");
        using var stopping = new CancellationTokenSource();
        h.Server.Respond = async request =>
        {
            stopping.Cancel();
            await Task.Delay(Timeout.Infinite, stopping.Token);
            throw new InvalidOperationException("unreachable");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Worker.TickAsync(stopping.Token));

        var reloaded = await h.ReloadAsync(record);
        Assert.Equal(DumpQueueState.Queued, reloaded.State);
        Assert.Equal(0, reloaded.RetryCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"detail\":\"invalid API key\"}", "API key was rejected")]
    [InlineData(HttpStatusCode.Forbidden, "{\"detail\":\"this account has been closed\"}", "account has been closed")]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"detail\":\"monthly quota of 5 dumps exceeded\"}", "monthly upload quota")]
    public async Task An_answer_no_dump_can_get_past_holds_the_whole_queue_without_spending_retries(
        HttpStatusCode status, string body, string reasonPart)
    {
        // website#1487: a revoked key or a closed account burnt every queued dump's
        // retries, and a 429 failed the dump for good although quotas reset.
        await using var h = await WorkerHarness.CreateAsync();
        var first = await h.AddDumpAsync("a.dmp");
        var second = await h.AddDumpAsync("b.dmp");
        h.Server.RespondWith(status, body);

        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Single(h.Server.Requests);
        foreach (var record in new[] { first, second })
        {
            var reloaded = await h.ReloadAsync(record);
            Assert.Equal(DumpQueueState.Queued, reloaded.State);
            Assert.Equal(0, reloaded.RetryCount);
        }
        var hold = h.State.ActiveUploadHold(DateTimeOffset.UtcNow);
        Assert.NotNull(hold);
        Assert.Contains(reasonPart, hold.Reason);

        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Single(h.Server.Requests); // still held
    }

    [Fact]
    public void A_quota_hold_is_long()
    {
        Assert.True(UploadWorkerService.QueueHoldFor(UploadOutcome.QuotaExceeded).Duration >= TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    public async Task A_rejected_dump_fails_at_once_and_the_queue_moves_on(HttpStatusCode status)
    {
        // website#1487: a 422 was retried five times, re-sending multi-GB files.
        await using var h = await WorkerHarness.CreateAsync();
        var first = await h.AddDumpAsync("a.dmp");
        var second = await h.AddDumpAsync("b.dmp");
        h.Server.RespondWith(status, "{\"detail\":\"not a recognised dump format\"}");

        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(2, h.Server.Requests.Count);
        Assert.Equal(DumpQueueState.Failed, (await h.ReloadAsync(first)).State);
        Assert.Equal(DumpQueueState.Failed, (await h.ReloadAsync(second)).State);
        Assert.Null(h.State.ActiveUploadHold(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_dump_held_open_elsewhere_stays_queued()
    {
        // website#1489: the IOException reached the catch-all and failed the dump.
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp");

        await using (new FileStream(record.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await h.Worker.TickAsync(CancellationToken.None);
        }

        var reloaded = await h.ReloadAsync(record);
        Assert.Equal(DumpQueueState.Queued, reloaded.State);
        Assert.NotNull(reloaded.LastError);
        Assert.Empty(h.Server.Requests);
    }

    [Fact]
    public async Task A_deleted_dump_fails()
    {
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp");
        File.Delete(record.FilePath);

        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(DumpQueueState.Failed, (await h.ReloadAsync(record)).State);
    }

    [Fact]
    public async Task No_usable_api_key_holds_the_queue_instead_of_failing_dumps()
    {
        await using var h = await WorkerHarness.CreateAsync();
        h.Config.ProtectedApiKey = null;
        var record = await h.AddDumpAsync("a.dmp");

        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(DumpQueueState.Queued, (await h.ReloadAsync(record)).State);
        Assert.Equal(UploadWorkerService.NoUsableKeyReason, h.State.ActiveUploadHold(DateTimeOffset.UtcNow)?.Reason);
    }

    [Fact]
    public async Task A_failed_central_copy_does_not_fail_an_accepted_upload()
    {
        // website#1490: Task.WhenAll(upload, copy) failed the record on any copy
        // exception, although the server had returned 201.
        await using var h = await WorkerHarness.CreateAsync(
            new CentralCopyConfig { Enabled = true, DestinationPath = "bad\0path" });
        var record = await h.AddDumpAsync("a.dmp");

        await h.Worker.TickAsync(CancellationToken.None);

        var reloaded = await h.ReloadAsync(record);
        Assert.Equal(DumpQueueState.Submitted, reloaded.State);
        Assert.NotNull(reloaded.CentralCopyError);
        Assert.Null(reloaded.CentralCopiedAtUtc);
    }

    [Fact]
    public async Task A_transient_failure_backs_off_and_stops_the_tick()
    {
        // website#1488: a failure was retried on the very next 30 s poll, and the
        // tick went on to try (and fail) every other queued dump too.
        await using var h = await WorkerHarness.CreateAsync();
        var first = await h.AddDumpAsync("a.dmp");
        var second = await h.AddDumpAsync("b.dmp");
        h.Server.RespondWith(HttpStatusCode.ServiceUnavailable, "{\"detail\":\"malware scanner unavailable\"}");

        var before = DateTimeOffset.UtcNow;
        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Single(h.Server.Requests);
        var failed = await h.ReloadAsync(first);
        Assert.Equal(DumpQueueState.Queued, failed.State);
        Assert.Equal(1, failed.RetryCount);
        Assert.NotNull(failed.FirstFailedAtUtc);
        Assert.InRange(failed.NextAttemptAtUtc!.Value, before + UploadWorkerService.RetryBackoffBase, DateTimeOffset.UtcNow + UploadWorkerService.RetryBackoffBase);
        Assert.Equal(0, (await h.ReloadAsync(second)).RetryCount); // not tried

        // Next tick: the first isn't due yet, the second goes.
        h.Server.RespondWith(HttpStatusCode.Created);
        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(2, h.Server.Requests.Count);
        Assert.Equal(DumpQueueState.Queued, (await h.ReloadAsync(first)).State);
        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(second)).State);
    }

    [Fact]
    public async Task A_scheduled_pass_cut_short_by_an_outage_keeps_its_slot_open()
    {
        // website#1516 review: the tick now stops at the first transient failure, so a
        // slot marked used then left the other dumps for the next day's slot.
        var always = new BatchingConfig { Mode = BatchMode.ScheduledTimeOfDay, ScheduledTimesLocal = { TimeOnly.MinValue } };
        await using var h = await WorkerHarness.CreateAsync(batching: always);
        var first = await h.AddDumpAsync("a.dmp");
        var second = await h.AddDumpAsync("b.dmp");
        h.Server.RespondWith(HttpStatusCode.ServiceUnavailable, "{\"detail\":\"down\"}");
        await h.Worker.TickAsync(CancellationToken.None);

        h.Server.RespondWith(HttpStatusCode.Created);
        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(second)).State);

        // website#1567: that pass reached every due dump, but a.dmp was still in its
        // backoff, so the slot stays open and a.dmp goes once its backoff is
        // over, not at the next day's slot.
        await h.EndBackoffAsync(first);
        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(3, h.Server.Requests.Count);
        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(first)).State);

        // Every dump of the slot is through now: the slot is used up.
        var later = await h.AddDumpAsync("c.dmp");
        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Equal(DumpQueueState.Queued, (await h.ReloadAsync(later)).State);
    }

    [Fact]
    public async Task A_scheduled_pass_with_every_dump_in_backoff_does_not_use_up_its_slot()
    {
        // website#1567: nothing was due, so the pass uploaded nothing, yet it marked
        // the slot used and the dump waited for the next day's slot.
        var always = new BatchingConfig { Mode = BatchMode.ScheduledTimeOfDay, ScheduledTimesLocal = { TimeOnly.MinValue } };
        await using var h = await WorkerHarness.CreateAsync(batching: always);
        var record = await h.AddDumpAsync("a.dmp");
        record.RetryCount = 1;
        record.FirstFailedAtUtc = DateTimeOffset.UtcNow;
        record.NextAttemptAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);
        await h.Store.UpsertAsync(record);

        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Empty(h.Server.Requests); // not due yet

        await h.EndBackoffAsync(record);
        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Single(h.Server.Requests);
        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(record)).State);
    }

    [Fact]
    public async Task A_dump_queued_while_a_slot_is_held_open_waits_for_the_next_slot()
    {
        // website#1567 review: a slot kept open for a dump in its backoff took every
        // dump queued meanwhile too, so one failing dump turned the schedule
        // into Immediate for as long as it kept failing.
        var nightly = new BatchingConfig { Mode = BatchMode.ScheduledTimeOfDay, ScheduledTimesLocal = { new TimeOnly(2, 0) } };
        await using var h = await WorkerHarness.CreateAsync(batching: nightly);
        var clock = new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero);
        h.Worker.ScheduleClock = () => clock;
        var failing = await h.AddDumpAsync("a.dmp");
        h.Server.RespondWith(HttpStatusCode.ServiceUnavailable, "{\"detail\":\"down\"}");
        await h.Worker.TickAsync(CancellationToken.None); // the slot opens; a.dmp backs off

        h.Server.RespondWith(HttpStatusCode.Created);
        clock = clock.AddHours(8); // working hours
        var later = await h.AddDumpAsync("b.dmp");
        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Single(h.Server.Requests); // b.dmp waits, a.dmp isn't due

        // The held-open slot still lets its own dump through once its backoff is over.
        await h.EndBackoffAsync(failing);
        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(failing)).State);
        Assert.Equal(DumpQueueState.Queued, (await h.ReloadAsync(later)).State);

        clock = new DateTimeOffset(2026, 10, 16, 2, 0, 0, TimeSpan.Zero);
        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(later)).State);
    }

    [Fact]
    public async Task A_slot_held_open_by_a_failing_dump_ends_when_the_next_slot_opens()
    {
        // The next slot takes over: the failing dump is one of its dumps, and
        // a dump queued after that slot opened waits for the one after.
        var nightly = new BatchingConfig { Mode = BatchMode.ScheduledTimeOfDay, ScheduledTimesLocal = { new TimeOnly(2, 0) } };
        await using var h = await WorkerHarness.CreateAsync(batching: nightly);
        var clock = new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero);
        h.Worker.ScheduleClock = () => clock;
        var failing = await h.AddDumpAsync("a.dmp");
        h.Server.RespondWith(HttpStatusCode.ServiceUnavailable, "{\"detail\":\"down\"}");
        await h.Worker.TickAsync(CancellationToken.None);
        var queuedOnDayOne = await h.AddDumpAsync("b.dmp");

        h.Server.RespondWith(HttpStatusCode.Created);
        clock = new DateTimeOffset(2026, 10, 16, 2, 0, 0, TimeSpan.Zero);
        await h.Worker.TickAsync(CancellationToken.None); // a.dmp is still backing off
        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(queuedOnDayOne)).State);

        clock = clock.AddHours(8);
        var queuedOnDayTwo = await h.AddDumpAsync("c.dmp");
        await h.EndBackoffAsync(failing);
        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(failing)).State);
        Assert.Equal(DumpQueueState.Queued, (await h.ReloadAsync(queuedOnDayTwo)).State);
    }

    [Fact]
    public async Task A_slot_with_nothing_queued_is_used_up_so_a_dump_queued_later_waits_for_the_next()
    {
        // dumptriage-tools#9: an empty 02:00 slot left the next dump to go at
        // once (09:30), during the hours the schedule is there to avoid.
        var nightly = new BatchingConfig { Mode = BatchMode.ScheduledTimeOfDay, ScheduledTimesLocal = { new TimeOnly(2, 0) } };
        await using var h = await WorkerHarness.CreateAsync(batching: nightly);
        var clock = new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero);
        h.Worker.ScheduleClock = () => clock;
        await h.Worker.TickAsync(CancellationToken.None); // nothing queued

        clock = clock.AddHours(8);
        var later = await h.AddDumpAsync("a.dmp");
        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Empty(h.Server.Requests);

        clock = new DateTimeOffset(2026, 10, 16, 2, 0, 0, TimeSpan.Zero);
        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(later)).State);
    }

    [Fact]
    public async Task A_first_run_waits_for_the_first_slot()
    {
        // dumptriage-tools#9: with no slot on record the service uploaded at once.
        var nightly = new BatchingConfig { Mode = BatchMode.ScheduledTimeOfDay, ScheduledTimesLocal = { new TimeOnly(2, 0) } };
        await using var h = await WorkerHarness.CreateAsync(batching: nightly, firstRun: true);
        var clock = new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);
        h.Worker.ScheduleClock = () => clock;
        var record = await h.AddDumpAsync("a.dmp");

        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Empty(h.Server.Requests);

        clock = new DateTimeOffset(2026, 10, 16, 2, 0, 0, TimeSpan.Zero);
        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(record)).State);
    }

    [Fact]
    public async Task A_restart_remembers_the_slot_already_taken()
    {
        // dumptriage-tools#9: the last slot lived in memory, so a restart after
        // the 02:00 slot flushed again at once.
        var nightly = new BatchingConfig { Mode = BatchMode.ScheduledTimeOfDay, ScheduledTimesLocal = { new TimeOnly(2, 0) } };
        await using var h = await WorkerHarness.CreateAsync(batching: nightly);
        var clock = new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero);
        h.Worker.ScheduleClock = () => clock;
        await h.AddDumpAsync("a.dmp");
        await h.Worker.TickAsync(CancellationToken.None);
        Assert.Single(h.Server.Requests);

        using var restarted = h.Restart();
        restarted.ScheduleClock = () => clock.AddHours(8);
        var later = await h.AddDumpAsync("b.dmp");
        await restarted.TickAsync(CancellationToken.None);

        Assert.Single(h.Server.Requests);
        Assert.Equal(DumpQueueState.Queued, (await h.ReloadAsync(later)).State);
    }

    [Fact]
    public async Task A_scheduled_pass_that_reached_every_dump_uses_up_its_slot()
    {
        var always = new BatchingConfig { Mode = BatchMode.ScheduledTimeOfDay, ScheduledTimesLocal = { TimeOnly.MinValue } };
        await using var h = await WorkerHarness.CreateAsync(batching: always);
        await h.AddDumpAsync("a.dmp");
        h.Server.RespondWith(HttpStatusCode.Created);
        await h.Worker.TickAsync(CancellationToken.None);
        var later = await h.AddDumpAsync("b.dmp");

        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(DumpQueueState.Queued, (await h.ReloadAsync(later)).State); // waits for the next slot
    }

    [Fact]
    public async Task A_dump_that_failed_often_is_still_retried_until_the_give_up_time()
    {
        // website#1488: five failures, however close together, failed it for good.
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp");
        record.RetryCount = 10;
        record.FirstFailedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        await h.Store.UpsertAsync(record);
        h.Server.RespondWith(HttpStatusCode.BadGateway, "");

        await h.Worker.TickAsync(CancellationToken.None);

        var reloaded = await h.ReloadAsync(record);
        Assert.Equal(DumpQueueState.Queued, reloaded.State);
        Assert.Equal(11, reloaded.RetryCount);
    }

    [Fact]
    public async Task A_dump_gives_up_once_it_has_been_failing_for_the_give_up_time()
    {
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp");
        record.RetryCount = 30;
        record.FirstFailedAtUtc = DateTimeOffset.UtcNow - UploadWorkerService.GiveUpAfter - TimeSpan.FromMinutes(1);
        await h.Store.UpsertAsync(record);
        h.Server.RespondWith(HttpStatusCode.BadGateway, "");

        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(DumpQueueState.Failed, (await h.ReloadAsync(record)).State);
    }

    [Fact]
    public async Task Retry_failed_from_the_tray_puts_failed_dumps_back_in_the_queue()
    {
        // website#1488: nothing ever took a dump out of Failed.
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp", DumpQueueState.Failed);
        record.RetryCount = 7;
        record.FirstFailedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(8);
        record.NextAttemptAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);
        await h.Store.UpsertAsync(record);

        h.State.RequestRetryFailed();
        await h.Worker.ProcessTrayRequestsAsync(CancellationToken.None);

        var reloaded = await h.ReloadAsync(record);
        Assert.Equal(DumpQueueState.Queued, reloaded.State);
        Assert.Equal(0, reloaded.RetryCount);
        Assert.Null(reloaded.FirstFailedAtUtc);
        Assert.Null(reloaded.NextAttemptAtUtc);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(100, 360)]
    public void The_retry_delay_doubles_up_to_a_cap(int retryCount, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), UploadWorkerService.RetryDelay(retryCount));
    }

    [Fact]
    public async Task A_file_whose_bytes_changed_since_hashing_is_not_uploaded_and_is_detected_again()
    {
        // website#1493: nothing re-checked the file between hashing and an upload
        // that may come hours later.
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp");
        await File.WriteAllBytesAsync(record.FilePath, "MDMP"u8.ToArray().Concat(new byte[100]).ToArray());
        var rescans = new List<string>();
        h.State.RescanRequested += rescans.Add;

        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Empty(h.Server.Requests);
        var reloaded = await h.ReloadAsync(record);
        // Superseded, not Failed: the new contents are their own record, and
        // this one must not sit in the tray's retry count (website#1516 review).
        Assert.Equal(DumpQueueState.Superseded, reloaded.State);
        Assert.Equal(UploadWorkerService.FileChangedError, reloaded.LastError);
        Assert.Equal(0, await h.Store.RequeueFailedAsync());
        Assert.Equal(new[] { record.FilePath }, rescans);
    }

    [Fact]
    public async Task A_file_only_touched_since_hashing_is_rehashed_and_uploaded()
    {
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp");
        var touched = DateTime.UtcNow.AddMinutes(3);
        File.SetLastWriteTimeUtc(record.FilePath, touched);

        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Single(h.Server.Requests);
        var reloaded = await h.ReloadAsync(record);
        Assert.Equal(DumpQueueState.Submitted, reloaded.State);
        Assert.Equal(new DateTimeOffset(touched, TimeSpan.Zero), reloaded.FileLastWriteUtc);
    }

    [Fact]
    public async Task The_upload_carries_the_hash_taken_at_detection()
    {
        // website#1495: the server had no way to tell the bytes it got from the ones the agent hashed.
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp");

        await h.Worker.TickAsync(CancellationToken.None);

        var request = Assert.Single(h.Server.Requests).Request;
        Assert.Equal(record.Id, Assert.Single(request.Headers.GetValues("X-Content-SHA256")));
    }

    [Fact]
    public async Task A_server_hash_mismatch_ends_the_record_and_detects_the_file_again()
    {
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp");
        h.Server.RespondWith(
            HttpStatusCode.UnprocessableEntity,
            "{\"detail\":\"content_sha256_mismatch: the uploaded file's sha256 differs from the X-Content-SHA256 header\"}");
        var rescans = new List<string>();
        h.State.RescanRequested += rescans.Add;

        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(DumpQueueState.Superseded, (await h.ReloadAsync(record)).State);
        Assert.Equal(new[] { record.FilePath }, rescans);
    }

    [Fact]
    public async Task Manual_submit_uploads_on_the_worker_loop()
    {
        await using var h = await WorkerHarness.CreateAsync();
        var record = await h.AddDumpAsync("a.dmp", DumpQueueState.PendingApproval);

        h.State.RequestManualSubmit(record.Id);
        Assert.Empty(h.Server.Requests); // queued for the loop, not sent from the caller's thread
        await h.Worker.ProcessTrayRequestsAsync(CancellationToken.None);

        Assert.Single(h.Server.Requests);
        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(record)).State);
    }

    [Fact]
    public async Task ATraceTheServerRefuses_DoesNotHoldBackTheDumps()
    {
        // Review of #39: a trace refused with 403 traces_disabled ended the pass, so dumps queued after it waited.
        await using var h = await WorkerHarness.CreateAsync();
        var added = await h.AddDumpAsync("trace.zip");
        var trace = new DumpRecord
        {
            Id = added.Id, FilePath = added.FilePath, SizeBytes = added.SizeBytes, FileLastWriteUtc = added.FileLastWriteUtc, Sha256 = added.Sha256,
            DumpType = DumpType.Trace, State = DumpQueueState.Queued, Manifest = "{\"schemaVersion\":1,\"traceType\":\"etl\"}",
            DetectedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(1), // queued first
        };
        await h.Store.UpsertAsync(trace);
        var dump = await h.AddDumpAsync("a.dmp");
        h.Server.Respond = request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/traces", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"detail\":\"traces_disabled\"}") }
            : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"id\":\"d1\"}") });

        await h.Worker.TickAsync(CancellationToken.None);

        Assert.Equal(DumpQueueState.Submitted, (await h.ReloadAsync(dump)).State);
        Assert.Equal(DumpQueueState.Queued, (await h.ReloadAsync(trace)).State);
    }
}
