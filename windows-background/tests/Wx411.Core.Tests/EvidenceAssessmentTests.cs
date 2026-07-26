namespace Wx411.Core.Tests;

public sealed class EvidenceAssessmentTests
{
    [Fact]
    public void SuccessfulPreciseOperationPassesGateBAndRetiresGateA()
    {
        var session = Session(
            Operation(EvidenceOperationKind.PreciseCapture, EvidenceOperationOutcome.Success));

        var assessment = EvidenceGateEvaluator.Assess(session);

        Assert.Equal(EvidenceGateStatus.NotApplicable, assessment.GateA.Status);
        Assert.Contains("RC8", assessment.GateA.Detail, StringComparison.Ordinal);
        Assert.Equal(EvidenceGateStatus.Pass, assessment.GateB.Status);
        Assert.Equal(EvidenceGateStatus.Incomplete, assessment.Overall);
    }

    [Fact]
    public void CleanCancellationWithoutPendingTicketProofStaysIncomplete()
    {
        var cancelled = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled,
            pendingCaptureTicketNotReused: null);

        var assessment = EvidenceGateEvaluator.Assess(Session(cancelled));

        Assert.Equal(EvidenceGateStatus.Incomplete, assessment.GateC.Status);
        Assert.Contains("DPAPI", assessment.GateC.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationWithLeakedSidecarFailsGateC()
    {
        var cancelled = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled,
            directoryAfter: DirectorySnapshot(
                new EvidenceDirectoryEntry(
                    "message_0.readable.sqlite-wal",
                    IsDirectory: false,
                    Length: 0,
                    DateTimeOffset.UtcNow,
                    IsTemporaryArtifact: true)));

        var assessment = EvidenceGateEvaluator.Assess(Session(cancelled));

        Assert.Equal(EvidenceGateStatus.Fail, assessment.GateC.Status);
        Assert.Equal(EvidenceGateStatus.Fail, assessment.Overall);
    }

    [Fact]
    public void ExplicitCancellationFailureOverridesEarlierPassingCancellation()
    {
        var passing = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled,
            pendingCaptureTicketNotReused: true);
        var leaking = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled,
            directoryAfter: DirectorySnapshot(
                new EvidenceDirectoryEntry(
                    "message_0.readable.sqlite-wal",
                    IsDirectory: false,
                    Length: 0,
                    DateTimeOffset.UtcNow,
                    IsTemporaryArtifact: true)));

        var assessment = EvidenceGateEvaluator.Assess(Session(passing, leaking));

        Assert.Equal(EvidenceGateStatus.Fail, assessment.GateC.Status);
        Assert.Equal(EvidenceGateStatus.Fail, assessment.Overall);
    }

    [Fact]
    public void ExplicitTicketReuseFailsEvenWhenDirectoryInspectionFailed()
    {
        var cancelled = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled,
            directoryAfter: new EvidenceDirectorySnapshot(
                "output",
                DateTimeOffset.UtcNow,
                Array.Empty<EvidenceDirectoryEntry>(),
                Error: "directory_access_denied"),
            pendingCaptureTicketNotReused: false);

        var assessment = EvidenceGateEvaluator.Assess(Session(cancelled));

        Assert.Equal(EvidenceGateStatus.Fail, assessment.GateC.Status);
        Assert.Equal(EvidenceGateStatus.Fail, assessment.Overall);
    }

    [Fact]
    public void MissingOutputDirectoryIsCleanWhenPendingTicketWasNotReused()
    {
        var cancelled = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled,
            directoryAfter: new EvidenceDirectorySnapshot(
                "output",
                DateTimeOffset.UtcNow,
                Array.Empty<EvidenceDirectoryEntry>(),
                Error: "directory_not_found"),
            pendingCaptureTicketNotReused: true);

        var assessment = EvidenceGateEvaluator.Assess(Session(cancelled));

        Assert.Equal(EvidenceGateStatus.Pass, assessment.GateC.Status);
    }

    [Fact]
    public void MatchingSourceHashesPassGateD()
    {
        var operation = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Success,
            sourceBefore: File("source.db", "aa"),
            sourceAfter: File("source.db", "aa"));

        var assessment = EvidenceGateEvaluator.Assess(Session(operation));

        Assert.Equal(EvidenceGateStatus.Pass, assessment.GateD.Status);
    }

    [Fact]
    public void ChangedSourceHashFailsInformationalGateDWithoutOverridingCurrentWorkflow()
    {
        var operation = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Success,
            sourceBefore: File("source.db", "aa"),
            sourceAfter: File("source.db", "bb"));

        var assessment = EvidenceGateEvaluator.Assess(Session(operation));

        Assert.Equal(EvidenceGateStatus.Fail, assessment.GateD.Status);
        Assert.Equal(EvidenceGateStatus.Incomplete, assessment.Overall);
    }

    [Fact]
    public void PreciseSuccessAndCleanCancellationProduceOverallPass()
    {
        var precise = Operation(EvidenceOperationKind.PreciseCapture, EvidenceOperationOutcome.Success);
        var cancelled = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled,
            pendingCaptureTicketNotReused: true);

        var assessment = EvidenceGateEvaluator.Assess(Session(precise, cancelled));

        Assert.Equal(EvidenceGateStatus.NotApplicable, assessment.GateA.Status);
        Assert.Equal(EvidenceGateStatus.Pass, assessment.GateB.Status);
        Assert.Equal(EvidenceGateStatus.Pass, assessment.GateC.Status);
        Assert.Equal(EvidenceGateStatus.Pass, assessment.GateD.Status);
        Assert.Equal(EvidenceGateStatus.Pass, assessment.Overall);
    }

    [Theory]
    [InlineData(true, EvidenceGateStatus.Pass)]
    [InlineData(false, EvidenceGateStatus.Fail)]
    public void KnownPendingTicketCorrelationSetsGateCStatus(
        bool pendingCaptureTicketNotReused,
        EvidenceGateStatus expectedStatus)
    {
        var cancelled = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled,
            pendingCaptureTicketNotReused: pendingCaptureTicketNotReused);

        var assessment = EvidenceGateEvaluator.Assess(Session(cancelled));

        Assert.Equal(expectedStatus, assessment.GateC.Status);
    }

    [Fact]
    public void UnknownPendingTicketCorrelationLeavesGateCIncomplete()
    {
        var cancelled = Operation(
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Cancelled,
            pendingCaptureTicketNotReused: null);

        var assessment = EvidenceGateEvaluator.Assess(Session(cancelled));

        Assert.Equal(EvidenceGateStatus.Incomplete, assessment.GateC.Status);
    }

    private static EvidenceSessionSnapshot Session(params EvidenceOperationRecord[] operations) => new(
        "1.5-dev",
        DateTimeOffset.UtcNow,
        "Windows",
        Is64BitProcess: true,
        Is64BitOperatingSystem: true,
        Array.AsReadOnly(operations));

    private static EvidenceOperationRecord Operation(
        EvidenceOperationKind kind,
        EvidenceOperationOutcome outcome,
        EvidenceFileRecord? sourceBefore = null,
        EvidenceFileRecord? sourceAfter = null,
        EvidenceDirectorySnapshot? directoryAfter = null,
        bool? pendingCaptureTicketNotReused = null)
    {
        var source = sourceBefore ?? File("source.db", "aa");
        return new EvidenceOperationRecord(
            Guid.NewGuid(),
            kind,
            outcome,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow,
            new EvidenceProcessSelection(null, "自动捕获全部 Weixin.exe", ScanAll: true),
            source,
            sourceAfter ?? source,
            "output",
            DirectorySnapshot(),
            directoryAfter ?? DirectorySnapshot(),
            outcome == EvidenceOperationOutcome.Success
                ? Array.AsReadOnly(new[] { File("output.sqlite", "bb", "ok") })
                : Array.Empty<EvidenceFileRecord>(),
            ErrorType: outcome == EvidenceOperationOutcome.Failed ? "InvalidOperationException" : null,
            ErrorMessage: outcome == EvidenceOperationOutcome.Failed ? "failed" : null,
            pendingCaptureTicketNotReused);
    }

    private static EvidenceFileRecord File(string path, string sha256, string? integrityCheck = null) => new(
        path,
        Exists: true,
        Length: 123,
        sha256,
        integrityCheck,
        Error: null);

    private static EvidenceDirectorySnapshot DirectorySnapshot(params EvidenceDirectoryEntry[] entries) => new(
        "output",
        DateTimeOffset.UtcNow,
        Array.AsReadOnly(entries),
        Error: null);
}
