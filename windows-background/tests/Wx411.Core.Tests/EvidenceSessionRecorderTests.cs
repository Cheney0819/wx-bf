using System.Text.Json;

namespace Wx411.Core.Tests;

public sealed class EvidenceSessionRecorderTests
{
    [Fact]
    public async Task BeginAndCompleteSuccessRecordsAllEvidence()
    {
        var inspector = new FakeInspector();
        var recorder = new EvidenceSessionRecorder("1.5-dev", inspector);
        var process = new RecoveryProcessSelection(42, "Weixin.exe");

        var handle = await recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            process,
            "source.db",
            "output",
            CancellationToken.None);
        await recorder.CompleteAsync(
            handle,
            EvidenceOperationOutcome.Success,
            new[] { "output.sqlite" },
            error: null,
            CancellationToken.None);

        var operation = Assert.Single(recorder.Snapshot().Operations);
        Assert.Equal(EvidenceOperationOutcome.Success, operation.Outcome);
        Assert.Equal(42, operation.Process.Pid);
        Assert.Equal("source.db", operation.SourceBefore.Path);
        Assert.Equal("source.db", operation.SourceAfter?.Path);
        Assert.Equal("ok", Assert.Single(operation.Outputs).IntegrityCheck);
        Assert.NotNull(operation.DirectoryAfter);
        Assert.Null(operation.ErrorType);
        Assert.Null(operation.ErrorMessage);
        Assert.Contains(inspector.Inspections, item => item == ("output.sqlite", true));
    }

    [Fact]
    public async Task CancellationRecordsCleanAfterDirectoryAndNoOutputs()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var handle = await recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(null, "automatic", ScanAll: true),
            "source.db",
            "output",
            CancellationToken.None);

        await recorder.CompleteAsync(
            handle,
            EvidenceOperationOutcome.Cancelled,
            Array.Empty<string>(),
            error: null,
            CancellationToken.None);

        var operation = Assert.Single(recorder.Snapshot().Operations);
        Assert.Equal(EvidenceOperationOutcome.Cancelled, operation.Outcome);
        Assert.Empty(operation.Outputs);
        Assert.NotNull(operation.SourceAfter);
        Assert.NotNull(operation.DirectoryAfter);
    }

    [Fact]
    public async Task FailureStoresOnlyExceptionTypeAndMessage()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var handle = await recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(7, "Weixin.exe"),
            "source.db",
            "output",
            CancellationToken.None);

        await recorder.CompleteAsync(
            handle,
            EvidenceOperationOutcome.Failed,
            Array.Empty<string>(),
            new InvalidOperationException("candidate rejected"),
            CancellationToken.None);

        var operation = Assert.Single(recorder.Snapshot().Operations);
        Assert.Equal("InvalidOperationException", operation.ErrorType);
        Assert.Equal("candidate rejected", operation.ErrorMessage);
        Assert.DoesNotContain(" at ", operation.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteRejectsUnknownOrAlreadyCompletedHandle()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var unknown = new EvidenceOperationHandle(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.CompleteAsync(
            unknown,
            EvidenceOperationOutcome.Failed,
            Array.Empty<string>(),
            error: null,
            CancellationToken.None));

        var handle = await recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(1, "Weixin.exe"),
            "source.db",
            "output",
            CancellationToken.None);
        await recorder.CompleteAsync(
            handle,
            EvidenceOperationOutcome.Cancelled,
            Array.Empty<string>(),
            error: null,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.CompleteAsync(
            handle,
            EvidenceOperationOutcome.Success,
            Array.Empty<string>(),
            error: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task SnapshotIsDetachedFromLaterOperations()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        _ = await recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(1, "Weixin.exe"),
            "first.db",
            "output",
            CancellationToken.None);
        var first = recorder.Snapshot();

        _ = await recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(2, "Weixin.exe"),
            "second.db",
            "output",
            CancellationToken.None);

        Assert.Single(first.Operations);
        Assert.Equal(2, recorder.Snapshot().Operations.Count);
    }

    [Fact]
    public async Task CancellationDuringBeginInspectionStillRecordsCancelledOperation()
    {
        var inspector = new BlockingInspector();
        var recorder = new EvidenceSessionRecorder("1.5-dev", inspector);
        using var cancellation = new CancellationTokenSource();

        var begin = recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(8, "Weixin.exe"),
            "source.db",
            "output",
            cancellation.Token);
        await inspector.InspectionStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => begin);

        var operation = Assert.Single(recorder.Snapshot().Operations);
        Assert.Equal(EvidenceOperationOutcome.Cancelled, operation.Outcome);
        Assert.NotNull(operation.CompletedUtc);
        Assert.Contains("cancel", operation.SourceBefore.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("directory")]
    public async Task NonCancellationBeginInspectionFailureRemovesUnreturnedOperation(string failurePoint)
    {
        var inspector = new FakeInspector
        {
            InspectionExceptionFactory = failurePoint == "source"
                ? (_, _) => new IOException("source inspection failed")
                : null,
            DirectoryException = failurePoint == "directory"
                ? new IOException("directory snapshot failed")
                : null,
        };
        var recorder = new EvidenceSessionRecorder("1.5-dev", inspector);

        await Assert.ThrowsAsync<IOException>(() => recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(8, "Weixin.exe"),
            "source.db",
            "output",
            CancellationToken.None));

        var session = recorder.Snapshot();
        Assert.Empty(session.Operations);

        var assessment = EvidenceGateEvaluator.Assess(session);
        Assert.Equal(EvidenceGateStatus.Incomplete, assessment.GateB.Status);
        Assert.NotEqual(EvidenceGateStatus.Fail, assessment.GateB.Status);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("output")]
    [InlineData("directory")]
    public async Task CompletionInspectionFailureStillRecordsTerminalOutcome(string failurePoint)
    {
        var inspector = new FakeInspector();
        var recorder = new EvidenceSessionRecorder("1.5-dev", inspector);
        var handle = await recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(9, "Weixin.exe"),
            "source.db",
            "output",
            CancellationToken.None);
        inspector.InspectionExceptionFactory = (path, _) =>
            failurePoint == "source" && path == "source.db" ||
            failurePoint == "output" && path == "output.sqlite"
                ? new IOException(failurePoint + " inspection failed")
                : null;
        inspector.DirectoryException = failurePoint == "directory"
            ? new IOException("directory inspection failed")
            : null;

        await Assert.ThrowsAsync<IOException>(() => recorder.CompleteAsync(
            handle,
            EvidenceOperationOutcome.Success,
            new[] { "output.sqlite" },
            error: null,
            CancellationToken.None));

        var operation = Assert.Single(recorder.Snapshot().Operations);
        Assert.Equal(EvidenceOperationOutcome.Success, operation.Outcome);
        Assert.NotNull(operation.CompletedUtc);
        Assert.Equal("IOException", operation.ErrorType);
        Assert.Contains("inspection failed", operation.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputPathEnumerationFailureStillRecordsTerminalOutcome()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var handle = await recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(10, "Weixin.exe"),
            "source.db",
            "output",
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.CompleteAsync(
            handle,
            EvidenceOperationOutcome.Success,
            new ThrowingPaths(),
            error: null,
            CancellationToken.None));

        var operation = Assert.Single(recorder.Snapshot().Operations);
        Assert.Equal(EvidenceOperationOutcome.Success, operation.Outcome);
        Assert.NotNull(operation.CompletedUtc);
        Assert.Equal("InvalidOperationException", operation.ErrorType);
        Assert.Equal("path enumeration failed", operation.ErrorMessage);
    }

    [Fact]
    public async Task FollowUpCorrelatesCaseInsensitiveCancelledTicketIdsWithoutExposingThem()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var cancelled = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);
        var createdTicketIds = new List<string> { "Pending-A" };

        recorder.RecordCancelledPendingTickets(cancelled, createdTicketIds);
        createdTicketIds[0] = "mutated-after-recording";

        var successful = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Success);
        var loadedTicketIds = new List<string> { "pending-a" };

        recorder.RecordPendingCaptureFollowUp(successful, loadedTicketIds);
        loadedTicketIds.Clear();

        var operation = recorder.Snapshot().Operations.Single(item => item.OperationId == cancelled.OperationId);
        Assert.False(operation.PendingCaptureTicketNotReused);
        Assert.DoesNotContain("Pending-A", JsonSerializer.Serialize(recorder.Snapshot()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FollowUpSetsKnownCancellationTicketEvidenceAndLeavesUnknownEvidenceIncomplete()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var empty = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);
        var intersecting = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);
        var unknown = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);

        recorder.RecordCancelledPendingTickets(empty, Array.Empty<string>());
        recorder.RecordCancelledPendingTickets(intersecting, new[] { "created-ticket" });
        recorder.RecordCancelledPendingTickets(unknown, null);

        var successful = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Success);
        recorder.RecordPendingCaptureFollowUp(successful, new[] { "CREATED-TICKET" });

        var operations = recorder.Snapshot().Operations.ToDictionary(item => item.OperationId);
        Assert.True(operations[empty.OperationId].PendingCaptureTicketNotReused);
        Assert.False(operations[intersecting.OperationId].PendingCaptureTicketNotReused);
        Assert.Null(operations[unknown.OperationId].PendingCaptureTicketNotReused);
    }

    [Fact]
    public async Task PendingTicketCallsRejectInvalidHandlesAndDoNotMutateOtherCancellations()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var cancelled = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);
        recorder.RecordCancelledPendingTickets(cancelled, new[] { "valid-ticket" });

        var preciseFailure = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Failed);
        var preciseSuccess = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Success);

        Assert.Throws<InvalidOperationException>(() => recorder.RecordCancelledPendingTickets(
            new EvidenceOperationHandle(Guid.NewGuid()),
            new[] { "invalid-ticket" }));
        Assert.Throws<InvalidOperationException>(() => recorder.RecordCancelledPendingTickets(
            preciseSuccess,
            new[] { "invalid-ticket" }));
        Assert.Throws<InvalidOperationException>(() => recorder.RecordCancelledPendingTickets(
            preciseFailure,
            new[] { "invalid-ticket" }));
        Assert.Throws<InvalidOperationException>(() => recorder.RecordPendingCaptureFollowUp(
            new EvidenceOperationHandle(Guid.NewGuid()),
            new[] { "valid-ticket" }));
        Assert.Throws<InvalidOperationException>(() => recorder.RecordPendingCaptureFollowUp(
            cancelled,
            new[] { "valid-ticket" }));
        Assert.Throws<InvalidOperationException>(() => recorder.RecordPendingCaptureFollowUp(
            preciseFailure,
            new[] { "valid-ticket" }));

        recorder.RecordPendingCaptureFollowUp(preciseSuccess, new[] { "VALID-TICKET" });

        var operation = recorder.Snapshot().Operations.Single(item => item.OperationId == cancelled.OperationId);
        Assert.False(operation.PendingCaptureTicketNotReused);
    }

    [Fact]
    public async Task FollowUpDoesNotUpdateCancellationCompletedAfterSuccessfulOperationStarted()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var successful = await recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(1, "Weixin.exe"),
            "source.db",
            "output",
            CancellationToken.None);
        var cancelled = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);
        recorder.RecordCancelledPendingTickets(cancelled, new[] { "created-ticket" });

        await recorder.CompleteAsync(
            successful,
            EvidenceOperationOutcome.Success,
            Array.Empty<string>(),
            error: null,
            CancellationToken.None);
        recorder.RecordPendingCaptureFollowUp(successful, new[] { "created-ticket" });

        var operation = recorder.Snapshot().Operations.Single(item => item.OperationId == cancelled.OperationId);
        Assert.Null(operation.PendingCaptureTicketNotReused);
    }

    [Fact]
    public async Task TicketIdEnumerationHappensOutsideTheRecorderLock()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var cancelled = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);

        recorder.RecordCancelledPendingTickets(
            cancelled,
            new SnapshotDuringEnumerationTicketIds(recorder, "created-ticket"));

        var successful = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Success);
        recorder.RecordPendingCaptureFollowUp(
            successful,
            new SnapshotDuringEnumerationTicketIds(recorder, "loaded-ticket"));

        var operation = recorder.Snapshot().Operations.Single(item => item.OperationId == cancelled.OperationId);
        Assert.True(operation.PendingCaptureTicketNotReused);
    }

    [Fact]
    public async Task ThrowingTicketIdEnumerationDoesNotMutateEvidence()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var cancelled = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);
        recorder.RecordCancelledPendingTickets(cancelled, new[] { "created-ticket" });

        Assert.Throws<InvalidOperationException>(() => recorder.RecordCancelledPendingTickets(
            cancelled,
            new ThrowingTicketIds()));

        var successful = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Success);
        Assert.Throws<InvalidOperationException>(() => recorder.RecordPendingCaptureFollowUp(
            successful,
            new ThrowingTicketIds()));

        var beforeFollowUp = recorder.Snapshot().Operations.Single(item => item.OperationId == cancelled.OperationId);
        Assert.Null(beforeFollowUp.PendingCaptureTicketNotReused);

        recorder.RecordPendingCaptureFollowUp(successful, new[] { "CREATED-TICKET" });

        var operation = recorder.Snapshot().Operations.Single(item => item.OperationId == cancelled.OperationId);
        Assert.False(operation.PendingCaptureTicketNotReused);
    }

    [Fact]
    public async Task TerminalPendingTicketEvidenceRejectsReplacementForPassingAndFailingCancellations()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var passing = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);
        var failing = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);
        recorder.RecordCancelledPendingTickets(passing, new[] { "passing-ticket" });
        recorder.RecordCancelledPendingTickets(failing, new[] { "failing-ticket" });

        var successful = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Success);
        recorder.RecordPendingCaptureFollowUp(successful, new[] { "FAILING-TICKET" });

        Assert.Throws<InvalidOperationException>(() => recorder.RecordCancelledPendingTickets(
            passing,
            new[] { "replacement-ticket" }));
        Assert.Throws<InvalidOperationException>(() => recorder.RecordCancelledPendingTickets(
            failing,
            new[] { "replacement-ticket" }));

        var operations = recorder.Snapshot().Operations.ToDictionary(item => item.OperationId);
        Assert.True(operations[passing.OperationId].PendingCaptureTicketNotReused);
        Assert.False(operations[failing.OperationId].PendingCaptureTicketNotReused);
    }

    [Fact]
    public async Task RunningPreciseHandlesAreRejectedByPendingTicketCalls()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var running = await recorder.BeginAsync(
            EvidenceOperationKind.PreciseCapture,
            new RecoveryProcessSelection(1, "Weixin.exe"),
            "source.db",
            "output",
            CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => recorder.RecordCancelledPendingTickets(
            running,
            new[] { "created-ticket" }));
        Assert.Throws<InvalidOperationException>(() => recorder.RecordPendingCaptureFollowUp(
            running,
            new[] { "loaded-ticket" }));

        var operation = Assert.Single(recorder.Snapshot().Operations);
        Assert.Equal(EvidenceOperationOutcome.Running, operation.Outcome);
        Assert.Null(operation.PendingCaptureTicketNotReused);
    }

    [Fact]
    public async Task NonEmptyDisjointTicketIdsProducePassingEvidenceAndRepeatedFollowUpPreservesIt()
    {
        var recorder = new EvidenceSessionRecorder("1.5-dev", new FakeInspector());
        var cancelled = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled);
        recorder.RecordCancelledPendingTickets(cancelled, new[] { "created-ticket" });

        var successful = await CompleteOperationAsync(
            recorder,
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Success);
        recorder.RecordPendingCaptureFollowUp(successful, new[] { "different-ticket" });
        recorder.RecordPendingCaptureFollowUp(successful, new[] { "CREATED-TICKET" });

        var operation = recorder.Snapshot().Operations.Single(item => item.OperationId == cancelled.OperationId);
        Assert.True(operation.PendingCaptureTicketNotReused);
    }

    private static async Task<EvidenceOperationHandle> CompleteOperationAsync(
        EvidenceSessionRecorder recorder,
        EvidenceOperationKind kind,
        EvidenceOperationOutcome outcome)
    {
        var handle = await recorder.BeginAsync(
            kind,
            new RecoveryProcessSelection(1, "Weixin.exe"),
            "source.db",
            "output",
            CancellationToken.None);
        await recorder.CompleteAsync(
            handle,
            outcome,
            Array.Empty<string>(),
            error: null,
            CancellationToken.None);
        return handle;
    }

    private sealed class FakeInspector : IEvidenceFileInspector
    {
        public List<(string Path, bool VerifySqlite)> Inspections { get; } = new();
        public Func<string, bool, Exception?>? InspectionExceptionFactory { get; set; }
        public Exception? DirectoryException { get; set; }

        public Task<EvidenceFileRecord> InspectAsync(
            string path,
            bool verifySqlite,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exception = InspectionExceptionFactory?.Invoke(path, verifySqlite);
            if (exception is not null) throw exception;
            Inspections.Add((path, verifySqlite));
            return Task.FromResult(new EvidenceFileRecord(
                path,
                Exists: true,
                Length: 10,
                Sha256: path.Contains("source", StringComparison.Ordinal) ? "aa" : "bb",
                IntegrityCheck: verifySqlite ? "ok" : null,
                Error: null));
        }

        public EvidenceDirectorySnapshot CaptureDirectory(string path)
        {
            if (DirectoryException is not null) throw DirectoryException;
            return new EvidenceDirectorySnapshot(
                path,
                DateTimeOffset.UtcNow,
                Array.Empty<EvidenceDirectoryEntry>(),
                Error: null);
        }
    }

    private sealed class BlockingInspector : IEvidenceFileInspector
    {
        public TaskCompletionSource InspectionStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<EvidenceFileRecord> InspectAsync(
            string path,
            bool verifySqlite,
            CancellationToken cancellationToken)
        {
            InspectionStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public EvidenceDirectorySnapshot CaptureDirectory(string path) => throw new InvalidOperationException(
            "Directory capture must not run after cancelled source inspection.");
    }

    private sealed class ThrowingPaths : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException(
            "path enumeration failed");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingTicketIds : IReadOnlyCollection<string>
    {
        public int Count => 1;

        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException(
            "ticket ID enumeration failed");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SnapshotDuringEnumerationTicketIds : IReadOnlyCollection<string>
    {
        private readonly EvidenceSessionRecorder _recorder;
        private readonly string[] _ticketIds;

        public SnapshotDuringEnumerationTicketIds(EvidenceSessionRecorder recorder, params string[] ticketIds)
        {
            _recorder = recorder;
            _ticketIds = ticketIds;
        }

        public int Count => _ticketIds.Length;

        public IEnumerator<string> GetEnumerator()
        {
            var snapshot = Task.Run(_recorder.Snapshot);
            if (!snapshot.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("Snapshot could not acquire the recorder lock during enumeration.");

            return ((IEnumerable<string>)_ticketIds).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
