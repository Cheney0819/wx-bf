namespace Wx411.Core.Tests;

public sealed class MainFormEvidenceContractTests
{
    [Fact]
    public void MainFormOwnsOnlyEvidenceOrchestration()
    {
        var source = MainFormSource();

        Assert.Contains("private readonly EvidenceSessionRecorder _evidenceRecorder;", source, StringComparison.Ordinal);
        Assert.Contains("private readonly EvidenceBundleService _evidenceBundleService;", source, StringComparison.Ordinal);
        Assert.Contains("evidenceRecorder ?? new EvidenceSessionRecorder(DisplayVersion)", source, StringComparison.Ordinal);
        Assert.Contains("evidenceBundleService ?? new EvidenceBundleService()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ZipArchive", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA256", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteIntegrityChecker", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainFormProvidesOneClickEvidenceButtonAndBusyState()
    {
        var source = MainFormSource();
        var layout = Slice(source, "private void BuildLayout()", "private static Label MakeFieldLabel(");
        var busy = Slice(source, "private void SetBusy(", "private void SetSourceControlsEnabled(");

        Assert.Contains("Text = \"导出证据包\"", source, StringComparison.Ordinal);
        Assert.Contains("_evidenceButton.Click += async (_, _) => await ExportEvidenceAsync();", source, StringComparison.Ordinal);
        Assert.Contains("actionPanel.Controls.Add(_evidenceButton);", layout, StringComparison.Ordinal);
        Assert.Contains("_evidenceButton.Enabled = !busy;", busy, StringComparison.Ordinal);
        Assert.Contains("_cancelButton.Enabled = busy && canCancel;", busy, StringComparison.Ordinal);
    }

    [Fact]
    public void PreciseCaptureRecordsEveryTerminalOutcome()
    {
        var source = MainFormSource();
        var capture = source[RequiredIndex(source, "private async Task StartCaptureAsync()")..];

        Assert.Contains("EvidenceOperationKind.PreciseCapture", capture, StringComparison.Ordinal);
        Assert.Contains("BeginEvidenceOperationAsync(", capture, StringComparison.Ordinal);
        Assert.Contains("EvidenceOperationOutcome.Success", capture, StringComparison.Ordinal);
        Assert.Contains("EvidenceOperationOutcome.Cancelled", capture, StringComparison.Ordinal);
        Assert.Contains("EvidenceOperationOutcome.Failed", capture, StringComparison.Ordinal);
        Assert.Contains("result.OutputPaths", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void PreciseCaptureUsesItsServiceVaultForTicketSnapshots()
    {
        var source = MainFormSource();
        var capture = source[RequiredIndex(source, "private async Task StartCaptureAsync()")..];
        var snapshotIndex = RequiredIndex(capture, "var pendingTicketIdsBefore = TrySnapshotPendingCaptureTicketIds()");
        var beginIndex = RequiredIndex(capture, "BeginEvidenceOperationAsync(");
        var captureIndex = RequiredIndex(capture, "_captureRecoveryService.CaptureAndDecryptAsync(");

        Assert.Contains("private readonly PendingCaptureVault _pendingCaptureVault;", source, StringComparison.Ordinal);
        Assert.Contains("_pendingCaptureVault = vault;", source, StringComparison.Ordinal);
        Assert.Contains("captureBackend,\n            _pendingCaptureVault", source, StringComparison.Ordinal);
        Assert.True(snapshotIndex < beginIndex);
        Assert.True(beginIndex < captureIndex);
        Assert.Contains("private IReadOnlyList<string>? TrySnapshotPendingCaptureTicketIds()", source, StringComparison.Ordinal);
        Assert.Contains("_pendingCaptureVault.SnapshotRecordIds()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PreciseCaptureRegistersCancelledTicketsOnlyAfterCompletion()
    {
        var source = MainFormSource();
        var capture = source[RequiredIndex(source, "private async Task StartCaptureAsync()")..];
        var cancellation = SliceBraceBlock(capture, "catch (OperationCanceledException)");
        var registrationGuard = SliceBraceBlock(cancellation, "if (evidenceOperation is not null)");
        var recorderCatch = SliceBraceBlock(registrationGuard, "catch (Exception ex)");
        var completionIndex = RequiredIndex(cancellation, "await CompleteEvidenceOperationAsync(");
        var afterSnapshotIndex = RequiredIndex(cancellation, "var pendingTicketIdsAfter = TrySnapshotPendingCaptureTicketIds()");
        var registrationIndex = RequiredIndex(cancellation, "RecordCancelledPendingTickets");

        Assert.Contains("RecordCancelledPendingTickets", registrationGuard, StringComparison.Ordinal);
        Assert.Contains("Except(pendingTicketIdsBefore, StringComparer.OrdinalIgnoreCase)", registrationGuard, StringComparison.Ordinal);
        Assert.Contains(".ToArray();", registrationGuard, StringComparison.Ordinal);
        Assert.Contains("RecordCancelledPendingTickets(evidenceOperation, null)", registrationGuard, StringComparison.Ordinal);
        Assert.True(completionIndex < afterSnapshotIndex);
        Assert.True(afterSnapshotIndex < registrationIndex);
        AssertCatchOnlyAppendsLog(recorderCatch);
    }

    [Fact]
    public void PreciseCaptureConfirmsFollowUpOnlyAfterSuccessfulCompletion()
    {
        var source = MainFormSource();
        var capture = source[RequiredIndex(source, "private async Task StartCaptureAsync()")..];
        var success = Slice(capture, "var result = await Task.Run(", "catch (OperationCanceledException)");
        var followUpGuard = SliceBraceBlock(success, "if (evidenceOperation is not null)");
        var recorderCatch = SliceBraceBlock(followUpGuard, "catch (Exception ex)");

        Assert.Contains("RecordPendingCaptureFollowUp(", followUpGuard, StringComparison.Ordinal);
        Assert.Contains("result.LoadedPendingCaptureTicketIds", followUpGuard, StringComparison.Ordinal);
        Assert.True(
            success.IndexOf("await CompleteEvidenceOperationAsync(", StringComparison.Ordinal) <
            success.IndexOf("if (evidenceOperation is not null)", StringComparison.Ordinal));
        AssertCatchOnlyAppendsLog(recorderCatch);
        Assert.DoesNotContain("RecordPendingCaptureFollowUp", Slice(capture, "catch (OperationCanceledException)", "finally"), StringComparison.Ordinal);
    }

    [Fact]
    public void PendingTicketSnapshotHelperLogsAndAbsorbsMetadataEnumerationFailures()
    {
        var source = MainFormSource();
        var helper = SliceBraceBlock(source, "private IReadOnlyList<string>? TrySnapshotPendingCaptureTicketIds()");
        var snapshotTry = SliceBraceBlock(helper, "try");
        var snapshotCatch = SliceBraceBlock(helper, "catch (Exception ex)");

        Assert.Contains("_pendingCaptureVault.SnapshotRecordIds()", snapshotTry, StringComparison.Ordinal);
        AssertCatchAppendsLogAndReturnsNull(snapshotCatch);
    }

    [Fact]
    public void CatchStatementAssertionsRejectAdditionalSideEffects()
    {
        Assert.ThrowsAny<Exception>(() => AssertCatchOnlyAppendsLog(
            "{\n    AppendLog(\"warning\");\n    SetStatus(\"failed\", 0);\n}"));
        Assert.ThrowsAny<Exception>(() => AssertCatchAppendsLogAndReturnsNull(
            "{\n    AppendLog(\"warning\");\n    SetBusy(false);\n    return null;\n}"));
    }

    [Fact]
    public void ExportUsesSessionSnapshotAndVisibleWindowLog()
    {
        var source = MainFormSource();
        var export = Slice(source, "private async Task ExportEvidenceAsync()", "private async Task<EvidenceOperationHandle?> BeginEvidenceOperationAsync(");

        Assert.Contains("_evidenceRecorder.Snapshot()", export, StringComparison.Ordinal);
        Assert.Contains("_log.Text", export, StringComparison.Ordinal);
        Assert.Contains("_evidenceBundleService.ExportAsync(", export, StringComparison.Ordinal);
        Assert.Contains("_lastOutputPath = result.BundlePath", export, StringComparison.Ordinal);
        Assert.Contains("SetBusy(true, canCancel: false)", export, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportCancelsAndAwaitsRefreshWithoutReenablingOperationControls()
    {
        var source = MainFormSource();
        var export = Slice(source, "private async Task ExportEvidenceAsync()", "private async Task<EvidenceOperationHandle?> BeginEvidenceOperationAsync(");
        var sourceControls = Slice(source, "private void SetSourceControlsEnabled(", "private void SetStatus(");

        Assert.Contains("private bool _isBusy;", source, StringComparison.Ordinal);
        Assert.Contains("private TaskCompletionSource? _refreshCompletion;", source, StringComparison.Ordinal);
        Assert.Contains("var refreshCompletion = _refreshCompletion;", export, StringComparison.Ordinal);
        Assert.Contains("_refreshCancellation?.Cancel();", export, StringComparison.Ordinal);
        Assert.Contains("await refreshCompletion.Task;", export, StringComparison.Ordinal);
        Assert.True(
            export.IndexOf("SetBusy(true, canCancel: false)", StringComparison.Ordinal) <
            export.IndexOf("_refreshCancellation?.Cancel();", StringComparison.Ordinal));
        Assert.True(
            export.IndexOf("await refreshCompletion.Task;", StringComparison.Ordinal) <
            export.IndexOf("_evidenceRecorder.Snapshot()", StringComparison.Ordinal));
        Assert.Contains("if (_runCancellation is not null || _isBusy) return;", sourceControls, StringComparison.Ordinal);
        Assert.Contains("_evidenceButton.Enabled = enabled;", sourceControls, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulExportShowsConciseResultDialog()
    {
        var source = MainFormSource();
        var export = Slice(source, "private async Task ExportEvidenceAsync()", "private async Task<EvidenceOperationHandle?> BeginEvidenceOperationAsync(");

        Assert.Contains("MessageBox.Show(", export, StringComparison.Ordinal);
        Assert.Contains("\"证据包已生成\"", export, StringComparison.Ordinal);
        Assert.Contains("result.Assessment.Overall", export, StringComparison.Ordinal);
    }

    private static string MainFormSource() => TestSourceTree.ReadWindowsEasy(
        Path.Combine("src", "Wx411.Easy", "MainForm.cs"));

    private static string Slice(string source, string start, string end)
    {
        var startIndex = RequiredIndex(source, start);
        var endIndex = RequiredIndex(source, end, startIndex + start.Length);
        return source[startIndex..endIndex];
    }

    private static string SliceBraceBlock(string source, string marker)
    {
        var markerIndex = RequiredIndex(source, marker);
        var openingBraceIndex = source.IndexOf('{', markerIndex + marker.Length);
        Assert.True(openingBraceIndex >= 0, $"Missing opening brace after: {marker}");

        var depth = 0;
        for (var index = openingBraceIndex; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            if (source[index] == '}' && --depth == 0)
                return source[openingBraceIndex..(index + 1)];
        }

        throw new InvalidOperationException($"Unclosed block after: {marker}");
    }

    private static void AssertCatchOnlyAppendsLog(string catchBlock)
    {
        Assert.Matches(@"^\{\s*AppendLog\([^;\r\n]*\);\s*\}$", catchBlock);
    }

    private static void AssertCatchAppendsLogAndReturnsNull(string catchBlock)
    {
        Assert.Matches(@"^\{\s*AppendLog\([^;\r\n]*\);\s*return null;\s*\}$", catchBlock);
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Missing source marker: {value}");
        return index;
    }
}
