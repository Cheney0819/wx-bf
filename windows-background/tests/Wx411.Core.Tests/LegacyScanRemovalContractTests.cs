namespace Wx411.Core.Tests;

public sealed class LegacyScanRemovalContractTests
{
    private static readonly string[] LegacySourceFiles =
    [
        "MemoryScanRecoveryService.cs",
        "ProcessMemoryScanner.cs",
        "KeyMaterialScanner.cs",
        "Aes256KeyScheduleScanner.cs",
        "CodecHolderScanner.cs",
        "ProcessScanDiagnostics.cs",
    ];

    [Fact]
    public void MainFormExposesOnlyPreciseCaptureAsRecoveryCommand()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Easy", "MainForm.cs"));

        Assert.Contains("Text = \"刷新列表\"", source, StringComparison.Ordinal);
        Assert.Contains("Text = \"定位 key 并解密\"", source, StringComparison.Ordinal);
        Assert.Contains("AcceptButton = _captureButton;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("一键恢复并生成副本", source, StringComparison.Ordinal);
        Assert.DoesNotContain("兼容检查 30 秒", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StartRecoveryAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_startButton", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_diagnosticButton", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PreciseCaptureUsesIndependentProcessDiscovery()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "CallpointCaptureRecoveryService.cs"));

        Assert.Contains("TargetProcessDiscovery.Discover()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessMemoryScanner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessInfo", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyScanProductionFilesAreRemoved()
    {
        var core = Path.Combine(TestSourceTree.FindWindowsEasyRoot(), "src", "Wx411.Core");

        foreach (var file in LegacySourceFiles)
            Assert.False(File.Exists(Path.Combine(core, file)), $"Legacy scan file still exists: {file}");
    }

    [Fact]
    public void LegacyDiagnosticFilePipelineIsRemoved()
    {
        var models = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "EvidenceModels.cs"));
        var recorder = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "EvidenceSessionRecorder.cs"));
        var bundle = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "EvidenceBundleService.cs"));
        var mainForm = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Easy", "MainForm.cs"));

        Assert.DoesNotContain("IReadOnlyList<EvidenceFileRecord> Diagnostics", models, StringComparison.Ordinal);
        Assert.DoesNotContain("requireDiagnostic", models, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnosticPaths", recorder, StringComparison.Ordinal);
        Assert.DoesNotContain("AddDiagnosticsAsync", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostics/", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnosticPaths", mainForm, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredGateAndLiveSourceObservationDoNotBlockCurrentWorkflow()
    {
        var precise = Operation(
            EvidenceOperationOutcome.Success,
            sourceBefore: FileRecord("source.db", "aa"),
            sourceAfter: FileRecord("source.db", "bb"));
        var cancelled = Operation(
            EvidenceOperationOutcome.Cancelled,
            pendingCaptureTicketNotReused: true);

        var assessment = EvidenceGateEvaluator.Assess(Session(precise, cancelled));

        Assert.Equal("NotApplicable", assessment.GateA.Status.ToString());
        Assert.Equal(EvidenceGateStatus.Pass, assessment.GateB.Status);
        Assert.Equal(EvidenceGateStatus.Pass, assessment.GateC.Status);
        Assert.Equal(EvidenceGateStatus.Fail, assessment.GateD.Status);
        Assert.Equal(EvidenceGateStatus.Pass, assessment.Overall);
    }

    [Fact]
    public void FailedPreciseCaptureStillFailsCurrentWorkflow()
    {
        var failed = Operation(EvidenceOperationOutcome.Failed);
        var cancelled = Operation(
            EvidenceOperationOutcome.Cancelled,
            pendingCaptureTicketNotReused: true);

        var assessment = EvidenceGateEvaluator.Assess(Session(failed, cancelled));

        Assert.Equal(EvidenceGateStatus.Fail, assessment.GateB.Status);
        Assert.Equal(EvidenceGateStatus.Pass, assessment.GateC.Status);
        Assert.Equal(EvidenceGateStatus.Fail, assessment.Overall);
    }

    [Fact]
    public void CurrentInstructionsDoNotSendUsersThroughLegacyScan()
    {
        foreach (var path in new[] { "使用说明.txt", "诊断测试步骤.txt", "build-win-x64.ps1" })
        {
            var content = TestSourceTree.ReadWindowsEasy(path);
            Assert.DoesNotContain("一键恢复并生成副本", content, StringComparison.Ordinal);
            Assert.DoesNotContain("兼容检查 30 秒", content, StringComparison.Ordinal);
        }
    }

    private static EvidenceSessionSnapshot Session(params EvidenceOperationRecord[] operations) => new(
        "1.5-dev",
        DateTimeOffset.UtcNow,
        "Windows",
        Is64BitProcess: true,
        Is64BitOperatingSystem: true,
        Array.AsReadOnly(operations));

    private static EvidenceOperationRecord Operation(
        EvidenceOperationOutcome outcome,
        EvidenceFileRecord? sourceBefore = null,
        EvidenceFileRecord? sourceAfter = null,
        bool? pendingCaptureTicketNotReused = null)
    {
        var before = sourceBefore ?? FileRecord("source.db", "aa");
        return new EvidenceOperationRecord(
            Guid.NewGuid(),
            EvidenceOperationKind.PreciseCapture,
            outcome,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow,
            new EvidenceProcessSelection(null, "自动捕获全部 Weixin.exe", ScanAll: true),
            before,
            sourceAfter ?? before,
            "output",
            DirectorySnapshot(),
            DirectorySnapshot(),
            outcome == EvidenceOperationOutcome.Success
                ? Array.AsReadOnly(new[] { FileRecord("output.sqlite", "cc", "ok") })
                : Array.Empty<EvidenceFileRecord>(),
            outcome == EvidenceOperationOutcome.Failed ? "InvalidOperationException" : null,
            outcome == EvidenceOperationOutcome.Failed ? "failed" : null,
            pendingCaptureTicketNotReused);
    }

    private static EvidenceFileRecord FileRecord(
        string path,
        string sha256,
        string? integrityCheck = null) => new(
        path,
        Exists: true,
        Length: 123,
        sha256,
        integrityCheck,
        Error: null);

    private static EvidenceDirectorySnapshot DirectorySnapshot() => new(
        "output",
        DateTimeOffset.UtcNow,
        Array.Empty<EvidenceDirectoryEntry>(),
        Error: null);
}
