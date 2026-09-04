using System.Diagnostics;
using Footprint.Core.Capture;
using Footprint.Core.Runtime;

namespace Footprint.Worker;

public sealed record CaptureWorkerOptions(
    string PipeName,
    string RunId,
    string StateRoot,
    string RuntimeAssemblyPath,
    string? DeviceId = null,
    string? RemoteRestartCommandId = null,
    string? LogFilePath = null,
    string? EventOutboxDirectory = null);

public interface ICaptureWorkerRuntime
{
    Task<CaptureRunTerminalStatus> RunAsync(CaptureWorkerOptions options, CancellationToken cancellationToken);
}

public interface IProcessPriorityController
{
    void Set(ProcessPriorityClass priority);
}

public sealed class CurrentProcessPriorityController : IProcessPriorityController
{
    public void Set(ProcessPriorityClass priority) => Process.GetCurrentProcess().PriorityClass = priority;
}

public static class CaptureWorkerExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 64;
    public const int DecompressionFailed = 65;
    public const int RuntimeFailure = 70;
    public const int Waiting = 75;
    public const int UnsupportedProfile = 78;
    public const int Cancelled = 130;
}

public sealed class CaptureWorkerHost(
    ICaptureWorkerRuntime runtime,
    IProcessPriorityController priority,
    Action<string>? reportError = null)
{
    private readonly Action<string> _reportError = reportError ?? Console.Error.WriteLine;

    public static bool CreatesNoUserInterface => true;

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseOptions(args, out var options))
        {
            _reportError("采集工作进程参数无效。");
            return CaptureWorkerExitCodes.InvalidArguments;
        }

        try
        {
            priority.Set(ProcessPriorityClass.BelowNormal);
            WriteAudit(options, "采集工作进程已启动", "正在进入密钥提取链路");
            var terminalStatus = await runtime.RunAsync(options, cancellationToken);
            WriteAudit(options, "采集工作进程已完成", TerminalStatusZh(terminalStatus));
            return MapExitCode(terminalStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _reportError("采集工作进程已取消。");
            WriteAudit(options, "采集工作进程已取消", "密钥提取链路已停止");
            return CaptureWorkerExitCodes.Cancelled;
        }
        catch (Exception exception)
        {
            _reportError("采集工作进程运行失败。");
            var diagnostics = FormatExceptionDiagnostics(exception);
            _reportError(diagnostics);
            WriteAudit(options, "采集工作进程运行失败", diagnostics);
            return CaptureWorkerExitCodes.RuntimeFailure;
        }
    }

    internal static bool TryParseOptions(string[] args, out CaptureWorkerOptions options)
    {
        options = default!;
        if (args is null) return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--pipe"] = string.Empty,
            ["--run-id"] = string.Empty,
            ["--state-root"] = string.Empty,
            ["--runtime-assembly"] = string.Empty,
            ["--device-id"] = string.Empty,
            ["--remote-restart-command-id"] = string.Empty,
            ["--log-file"] = string.Empty,
            ["--event-outbox"] = string.Empty
        };

        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !values.ContainsKey(args[index]) ||
                string.IsNullOrWhiteSpace(args[index + 1]) || values[args[index]].Length != 0)
            {
                return false;
            }

            values[args[index]] = args[index + 1];
        }

        if (values.Where(pair => pair.Key is not "--remote-restart-command-id" and not "--log-file")
            .Any(pair => string.IsNullOrWhiteSpace(pair.Value))) return false;

        try
        {
            options = new CaptureWorkerOptions(
                values["--pipe"],
                values["--run-id"],
                Path.GetFullPath(values["--state-root"]),
                Path.GetFullPath(values["--runtime-assembly"]),
                values["--device-id"],
                string.IsNullOrWhiteSpace(values["--remote-restart-command-id"])
                    ? null
                    : values["--remote-restart-command-id"],
                string.IsNullOrWhiteSpace(values["--log-file"])
                    ? null
                    : Path.GetFullPath(values["--log-file"]),
                Path.GetFullPath(values["--event-outbox"]));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static int MapExitCode(CaptureRunTerminalStatus terminalStatus) => terminalStatus switch
    {
        CaptureRunTerminalStatus.WaitingForPhase03 => CaptureWorkerExitCodes.Success,
        CaptureRunTerminalStatus.WaitingForRemoteRestart => CaptureWorkerExitCodes.Waiting,
        CaptureRunTerminalStatus.WaitingForRestartEnablement => CaptureWorkerExitCodes.Waiting,
        CaptureRunTerminalStatus.UnsupportedProfile => CaptureWorkerExitCodes.UnsupportedProfile,
        CaptureRunTerminalStatus.DecompressionFailed => CaptureWorkerExitCodes.DecompressionFailed,
        CaptureRunTerminalStatus.Cancelled => CaptureWorkerExitCodes.Cancelled,
        _ => CaptureWorkerExitCodes.RuntimeFailure
    };

    private static void WriteAudit(CaptureWorkerOptions options, string eventZh, string resultZh)
    {
        if (string.IsNullOrWhiteSpace(options.LogFilePath)) return;
        try
        {
            var outbox = string.IsNullOrWhiteSpace(options.EventOutboxDirectory) ||
                         string.IsNullOrWhiteSpace(options.DeviceId)
                ? null
                : new SourceEventOutbox(options.EventOutboxDirectory, options.DeviceId);
            new KeyExtractionAuditLog(options.LogFilePath, outbox).Write("采集", options.RunId, eventZh, resultZh);
        }
        catch (Exception) { }
    }

    internal static string FormatExceptionDiagnostics(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var builder = new System.Text.StringBuilder();
        var current = exception;
        var depth = 0;
        while (current is not null)
        {
            var prefix = depth == 0 ? "EXCEPTION" : $"INNER_EXCEPTION_{depth}";
            builder.Append(prefix).Append("_TYPE=").AppendLine(current.GetType().FullName ?? current.GetType().Name);
            builder.Append(prefix).Append("_MESSAGE=").AppendLine(current.Message);
            if (current is CachedKeyStoreException cachedKeyStore)
            {
                builder.Append(prefix).Append("_CODE=").AppendLine(cachedKeyStore.Code);
                if (cachedKeyStore.InternalCause is not null)
                {
                    builder.Append(prefix).AppendLine("_INTERNAL_CAUSE_BEGIN");
                    builder.AppendLine(cachedKeyStore.InternalCause.ToString());
                    builder.Append(prefix).AppendLine("_INTERNAL_CAUSE_END");
                    if (FindWin32Exception(cachedKeyStore.InternalCause) is { } internalWin32)
                        builder.Append(prefix).Append("_WIN32_NATIVE_ERROR_CODE=")
                            .AppendLine(internalWin32.NativeErrorCode.ToString(
                                System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else if (current is System.ComponentModel.Win32Exception win32)
            {
                builder.Append(prefix).Append("_WIN32_NATIVE_ERROR_CODE=")
                    .AppendLine(win32.NativeErrorCode.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
            }
            builder.Append(prefix).AppendLine("_STACK_TRACE_BEGIN");
            builder.AppendLine(current.StackTrace ?? "<unavailable>");
            builder.Append(prefix).AppendLine("_STACK_TRACE_END");
            current = current.InnerException;
            depth++;
        }
        return builder.ToString().TrimEnd();
    }

    private static System.ComponentModel.Win32Exception? FindWin32Exception(Exception exception)
    {
        if (exception is System.ComponentModel.Win32Exception win32) return win32;
        if (exception is AggregateException aggregate)
        {
            foreach (var item in aggregate.InnerExceptions)
                if (FindWin32Exception(item) is { } nested) return nested;
        }
        return exception.InnerException is null ? null : FindWin32Exception(exception.InnerException);
    }

    private static string TerminalStatusZh(CaptureRunTerminalStatus status) => status switch
    {
        CaptureRunTerminalStatus.WaitingForPhase03 => "采集完成，等待上传",
        CaptureRunTerminalStatus.WaitingForRemoteRestart => "等待远程微信重启命令",
        CaptureRunTerminalStatus.WaitingForRestartEnablement => "等待允许微信重启",
        CaptureRunTerminalStatus.UnsupportedProfile => "微信版本配置不受支持",
        CaptureRunTerminalStatus.DecompressionFailed => "数据库解压失败",
        CaptureRunTerminalStatus.Cancelled => "采集已取消",
        _ => "采集运行失败"
    };
}
