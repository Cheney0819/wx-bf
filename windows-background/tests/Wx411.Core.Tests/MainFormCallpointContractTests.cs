namespace Wx411.Core.Tests;

public sealed class MainFormCallpointContractTests
{
    [Fact]
    public void MainFormInjectsBackendAndRunsRecoveryOffTheUiThread()
    {
        var mainForm = Source("src", "Wx411.Easy", "MainForm.cs");
        var service = Source("src", "Wx411.Core", "CallpointCaptureRecoveryService.cs");
        var start = mainForm[RequiredIndex(mainForm, "private async Task StartCaptureAsync()")..];

        Assert.Contains("Func<ICallpointCaptureBackend>", service, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(", start, StringComparison.Ordinal);
        Assert.Contains("CaptureAndDecryptAsync", start, StringComparison.Ordinal);
        Assert.DoesNotContain("new DebugCaptureBackend()", start, StringComparison.Ordinal);
        Assert.DoesNotContain("StableDatabaseSnapshot", start, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureStartsWithLightweightDynamicDescriptors()
    {
        var service = Source("src", "Wx411.Core", "CallpointCaptureRecoveryService.cs");
        var catalog = Source("src", "Wx411.Core", "DatabaseProbeCatalog.cs");

        Assert.Contains("DatabaseProbeCatalog.Create", service, StringComparison.Ordinal);
        Assert.Contains("RefreshCatalogPeriodicallyAsync", service, StringComparison.Ordinal);
        Assert.Contains("catalog.RefreshAsync", service, StringComparison.Ordinal);
        Assert.Contains("collector.Synchronize", service, StringComparison.Ordinal);
        Assert.DoesNotContain("StableDatabaseSnapshot.Read", service, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllBytes", catalog, StringComparison.Ordinal);
        Assert.Contains("FileOptions.RandomAccess", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugEventThreadOnlyTransfersBoundedCandidates()
    {
        var backend = Source("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs");
        var method = Slice(
            backend,
            "public Task<CapturedKeyMaterial?> CaptureToChannelWhenModuleLoadsAsync(",
            "private CapturedKeyMaterial? RunCaptureSync(");

        Assert.Contains("channel.TryWrite", method, StringComparison.Ordinal);
        Assert.Contains("shouldStop", method, StringComparison.Ordinal);
        Assert.DoesNotContain("DatabaseProbeCatalog", method, StringComparison.Ordinal);
        Assert.DoesNotContain("CipherProfileProbe", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", method, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateConsumerRefreshesThenSamplesOutsideBackend()
    {
        var service = Source("src", "Wx411.Core", "CallpointCaptureRecoveryService.cs");
        var consumer = Slice(
            service,
            "private static async Task ConsumeCandidatesAsync(",
            "private static async Task RefreshCatalogPeriodicallyAsync(");

        var refresh = RequiredIndex(consumer, "catalog.RefreshAsync");
        var synchronize = RequiredIndex(consumer, "collector.Synchronize", refresh);
        var collect = RequiredIndex(consumer, "collector.TryCollect", synchronize);
        Assert.True(refresh < synchronize && synchronize < collect);
        Assert.Contains("using (candidate)", consumer, StringComparison.Ordinal);
        Assert.Contains("SavePendingCaptureToVault", consumer, StringComparison.Ordinal);
    }

    [Fact]
    public void DetachAndChannelDrainPrecedeSequentialExporter()
    {
        var service = Source("src", "Wx411.Core", "CallpointCaptureRecoveryService.cs");
        var capture = RequiredIndex(service, "CaptureToChannelWhenModuleLoadsAsync");
        var complete = RequiredIndex(service, "channel.Complete()", capture);
        var drain = RequiredIndex(service, "await consumer", complete);
        var export = RequiredIndex(service, "_exporter.ExportAsync", drain);

        Assert.True(capture < complete && complete < drain && drain < export);
        Assert.Contains("foreach (var match in collector.PendingMatches.ToArray())", service, StringComparison.Ordinal);
        Assert.Contains("collector.ConfirmExport(match)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticModeWaitsAndPrioritizesExplicitCandidatePids()
    {
        var service = Source("src", "Wx411.Core", "CallpointCaptureRecoveryService.cs");

        Assert.Contains("WaitForCaptureProcessSelectionsAsync", service, StringComparison.Ordinal);
        Assert.Contains("ProcessFileHandleFinder.FindProcessIdsHoldingFile", service, StringComparison.Ordinal);
        Assert.Contains("targets.Select(item => item.Pid).ToArray()", service, StringComparison.Ordinal);
        Assert.Contains("早鸟等待 Weixin.exe 启动", service, StringComparison.Ordinal);
        Assert.Contains("EnqueueNewCaptureTargets", service, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendOwnsExactProfileSelectionAndVerifiedCleanup()
    {
        var backend = Source("src", "Wx411.Core", "Windows", "DebugCaptureBackend.cs");

        Assert.Contains("ModuleInspectionCache", backend, StringComparison.Ordinal);
        Assert.Contains("identity.Profile", backend, StringComparison.Ordinal);
        Assert.Contains("CallpointProfiles.MaxBreakpointsPerAttach", backend, StringComparison.Ordinal);
        Assert.Contains("BreakpointRestoreStatus.Restored", backend, StringComparison.Ordinal);
        Assert.Contains("BreakpointRestoreStatus.ProcessExited", backend, StringComparison.Ordinal);
        Assert.True(
            RequiredIndex(backend, "RestoreBreakpoints()") <
            RequiredIndex(backend, "DebugActiveProcessStop((uint)pid)"));
    }

    [Fact]
    public void DpapiTicketsLoadBeforeCaptureAndDeleteOnlyAfterCompletedExport()
    {
        var service = Source("src", "Wx411.Core", "CallpointCaptureRecoveryService.cs");
        var load = RequiredIndex(service, "LoadPendingCapturesFromVault(");
        var capture = RequiredIndex(service, "CaptureToChannelWhenModuleLoadsAsync", load);
        var export = RequiredIndex(service, "DatabaseExportStatus.Completed", capture);
        var delete = RequiredIndex(service, "DeleteVaultRecords", export);

        Assert.True(load < capture && capture < export && export < delete);
        Assert.Contains("vault.Save(", service, StringComparison.Ordinal);
        Assert.Contains("WindowsDpapiProtector", Source("src", "Wx411.Easy", "MainForm.cs"), StringComparison.Ordinal);
    }

    private static string Source(params string[] path) =>
        TestSourceTree.ReadWindowsEasy(Path.Combine(path));

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = RequiredIndex(source, startMarker);
        var end = RequiredIndex(source, endMarker, start + startMarker.Length);
        return source[start..end];
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected source marker was not found: {value}");
        return index;
    }
}
