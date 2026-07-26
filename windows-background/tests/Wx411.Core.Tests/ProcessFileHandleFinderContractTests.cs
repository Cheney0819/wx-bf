using System.Runtime.InteropServices;
using Wx411.Core.Windows;

namespace Wx411.Core.Tests;

public sealed class ProcessFileHandleFinderContractTests
{
    [Fact]
    public void FinderExposesBestEffortWindowsOnlyApi()
    {
        var method = typeof(ProcessFileHandleFinder).GetMethod(
            nameof(ProcessFileHandleFinder.FindProcessIdsHoldingFile),
            [typeof(string), typeof(IReadOnlyCollection<int>)]);

        Assert.NotNull(method);
        Assert.Equal(typeof(IReadOnlyList<int>), method!.ReturnType);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Empty(ProcessFileHandleFinder.FindProcessIdsHoldingFile(
                "/tmp/definitely-not-a-windows-path.db",
                [1234]));
        }
    }

    [Fact]
    public void EmptyCandidateSetIsRejectedBeforeAnyPlatformOrNativeQuery()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "ProcessFileHandleFinder.cs"));
        var emptyCheck = source.IndexOf("candidatePids is { Count: 0 }", StringComparison.Ordinal);
        var platformCheck = source.IndexOf("RuntimeInformation.IsOSPlatform", StringComparison.Ordinal);
        var nativeQuery = source.IndexOf("TryQuerySystemHandles", StringComparison.Ordinal);

        Assert.True(emptyCheck >= 0);
        Assert.True(emptyCheck < platformCheck);
        Assert.True(emptyCheck < nativeQuery);
    }

    [Fact]
    public void PathQueriesUseOneBoundedExecutorAndStopAfterTimeout()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "ProcessFileHandleFinder.cs"));

        Assert.Contains("BoundedHandlePathQueryExecutor", source, StringComparison.Ordinal);
        Assert.Contains("HandlePathQueryStatus.TimedOut", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("task.Wait", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FinderUsesSystemHandlesAndDuplicatesBeforePathComparison()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "Windows", "ProcessFileHandleFinder.cs"));

        Assert.Contains("NtQuerySystemInformation", source, StringComparison.Ordinal);
        Assert.Contains("SystemExtendedHandleInformation", source, StringComparison.Ordinal);
        Assert.Contains("PROCESS_DUP_HANDLE", source, StringComparison.Ordinal);
        Assert.Contains("DuplicateHandle", source, StringComparison.Ordinal);
        Assert.Contains("GetFinalPathNameByHandleW", source, StringComparison.Ordinal);
        Assert.Contains("GetFileType", source, StringComparison.Ordinal);
        Assert.Contains("ObjectNameTimeout", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeDosPath", source, StringComparison.Ordinal);
    }
}
