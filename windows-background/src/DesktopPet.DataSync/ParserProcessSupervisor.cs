using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Background.Infrastructure;

namespace DesktopPet.DataSync;

public sealed class ParserProcessSupervisor
{
    private const int MaximumInstallManifestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly string[] EnvironmentAllowlist =
    [
        "SystemRoot",
        "WINDIR",
        "TEMP",
        "TMP",
        "COMSPEC",
    ];

    private readonly string _installManifestPath;
    private readonly ParserSupervisorOptions _options;
    private readonly IParserPostKillWaiter _postKillWaiter;

    public ParserProcessSupervisor(
        string installManifestPath,
        ParserSupervisorOptions? options = null,
        IParserPostKillWaiter? postKillWaiter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installManifestPath);
        _installManifestPath = Path.GetFullPath(installManifestPath);
        _options = options ?? ParserSupervisorOptions.Default;
        _postKillWaiter = postKillWaiter ?? DefaultParserPostKillWaiter.Instance;
        if (_options.SoftTimeout <= TimeSpan.Zero ||
            _options.HardTimeout <= _options.SoftTimeout ||
            _options.PostKillGrace <= TimeSpan.Zero ||
            _options.PostKillGrace > TimeSpan.FromSeconds(30) ||
            _options.DiagnosticByteLimit is < 1024 or > 1024 * 1024 ||
            string.IsNullOrWhiteSpace(_options.RequiredExecutableName))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public async Task<ParserProcessResult> RunAsync(
        string jobManifestPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobManifestPath);
        var executable = await ValidateExecutableAsync(cancellationToken);
        var fullJobPath = Path.GetFullPath(jobManifestPath);
        if (!File.Exists(fullJobPath))
            throw new FileNotFoundException("Parser job manifest is missing.", fullJobPath);
        var jobRoot = Path.GetDirectoryName(fullJobPath) ??
            throw new InvalidDataException("Parser job manifest has no job root.");
        var cancellationRequest = Path.Combine(jobRoot, "cancel.request");
        TryDelete(cancellationRequest);

        using var process = new Process
        {
            StartInfo = CreateStartInfo(executable, fullJobPath, jobRoot),
            EnableRaisingEvents = true,
        };
        if (!process.Start()) throw new InvalidOperationException("Parser process did not start.");
        var stopwatch = Stopwatch.StartNew();
        var stdoutTask = ReadCappedAsync(
            process.StandardOutput.BaseStream,
            _options.DiagnosticByteLimit);
        var stderrTask = ReadCappedAsync(
            process.StandardError.BaseStream,
            _options.DiagnosticByteLimit);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var cancellationSignal = CreateCancellationSignal(cancellationToken);
        using var cancellationRegistration = cancellationSignal.Registration;
        var softDelay = Task.Delay(_options.SoftTimeout);
        var first = await Task.WhenAny(exitTask, softDelay, cancellationSignal.Task);
        var softCancellationRequested = false;
        var hardKilled = false;
        if (first != exitTask)
        {
            softCancellationRequested = true;
            await AtomicFile.ReplaceAsync(
                cancellationRequest,
                "1"u8.ToArray(),
                CancellationToken.None);
            var hardRemaining = _options.HardTimeout - stopwatch.Elapsed;
            if (hardRemaining <= TimeSpan.Zero ||
                await Task.WhenAny(exitTask, Task.Delay(hardRemaining)) != exitTask)
            {
                hardKilled = true;
                TryKillTree(process);
            }
        }

        var completionTask = Task.WhenAll(exitTask, stdoutTask, stderrTask);
        if (!await _postKillWaiter.WaitAsync(completionTask, _options.PostKillGrace))
        {
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            _ = completionTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new ParserSupervisorException(
                "parser_cleanup_timeout",
                "Parser process cleanup did not complete within the bounded grace.");
        }

        await exitTask;
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        cancellationToken.ThrowIfCancellationRequested();
        return new ParserProcessResult(
            process.ExitCode,
            stdout.Text.TrimEnd('\r', '\n'),
            stderr.Text.TrimEnd('\r', '\n'),
            stdout.Truncated,
            stderr.Truncated,
            softCancellationRequested,
            hardKilled);
    }

    private async Task<string> ValidateExecutableAsync(CancellationToken cancellationToken)
    {
        var info = new FileInfo(_installManifestPath);
        if (!info.Exists)
            throw new FileNotFoundException("Parser install manifest is missing.", _installManifestPath);
        if (info.Length > MaximumInstallManifestBytes)
            throw new InvalidDataException("Parser install manifest is too large.");
        var json = await File.ReadAllBytesAsync(_installManifestPath, cancellationToken);
        ParserInstallManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ParserInstallManifest>(json, JsonOptions) ??
                throw new InvalidDataException("Parser install manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Parser install manifest is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException("Parser install schema is unsupported.");
        ValidateSha256(manifest.Sha256);
        var installRoot = Path.GetDirectoryName(_installManifestPath) ??
            throw new InvalidDataException("Parser install manifest has no directory.");
        var executable = Path.IsPathRooted(manifest.ExecutablePath)
            ? Path.GetFullPath(manifest.ExecutablePath)
            : Path.GetFullPath(Path.Combine(installRoot, manifest.ExecutablePath));
        if (!IsBelowRoot(executable, installRoot) ||
            !string.Equals(
                Path.GetFileName(executable),
                _options.RequiredExecutableName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Parser executable identity is not allowed.");
        }
        if (!File.Exists(executable))
            throw new FileNotFoundException("Parser executable is missing.", executable);
        var actual = await FileSha256Async(executable, cancellationToken);
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Parser executable hash is not allowlisted.");
        return executable;
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        string jobManifestPath,
        string jobRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = jobRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--job");
        startInfo.ArgumentList.Add(jobManifestPath);
        startInfo.Environment.Clear();
        foreach (var name in EnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) startInfo.Environment[name] = value;
        }
        return startInfo;
    }

    private static async Task<CappedText> ReadCappedAsync(Stream stream, int byteLimit)
    {
        var buffer = new byte[8192];
        using var kept = new MemoryStream(Math.Min(byteLimit, 64 * 1024));
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            var remaining = byteLimit - checked((int)kept.Length);
            if (remaining > 0)
                kept.Write(buffer, 0, Math.Min(remaining, read));
            if (read > remaining) truncated = true;
        }
        return new CappedText(
            Encoding.UTF8.GetString(kept.GetBuffer(), 0, checked((int)kept.Length)),
            truncated);
    }

    private static (Task Task, CancellationTokenRegistration Registration) CreateCancellationSignal(
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            return (Task.Delay(Timeout.InfiniteTimeSpan), default);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion);
        return (completion.Task, registration);
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // A concurrent natural exit already completed process cleanup.
        }
    }

    private static bool IsBelowRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) &&
            relative != "." &&
            !relative.Replace('\\', '/').Split('/').Any(part => part == "..");
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Parser SHA-256 is invalid.");
    }

    private static async Task<string> FileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException)
        {
            // A stale request is harmless; the parser also has an internal deadline.
        }
        catch (UnauthorizedAccessException)
        {
            // A stale request is harmless; the parser also has an internal deadline.
        }
    }

    private sealed record CappedText(string Text, bool Truncated);

    private sealed class DefaultParserPostKillWaiter : IParserPostKillWaiter
    {
        internal static DefaultParserPostKillWaiter Instance { get; } = new();

        public async Task<bool> WaitAsync(Task completion, TimeSpan grace) =>
            await Task.WhenAny(completion, Task.Delay(grace)) == completion;
    }
}
