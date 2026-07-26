using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopPet.DataSync.Tests;

public sealed class ParserProcessSupervisorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-parser-supervisor-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RejectsExecutableHashOutsideAllowlist()
    {
        if (OperatingSystem.IsWindows()) return;
        var fixture = await CreateExecutableAsync("printf '{\"schemaVersion\":1}'");
        await File.AppendAllTextAsync(fixture.ExecutablePath, "\n# drift");
        var supervisor = CreateSupervisor(fixture.InstallManifestPath);

        await Assert.ThrowsAsync<CryptographicException>(() =>
            supervisor.RunAsync(fixture.JobPath, default));
    }

    [Fact]
    public async Task ClearsInheritedServerKeyAndProxyEnvironment()
    {
        if (OperatingSystem.IsWindows()) return;
        var fixture = await CreateExecutableAsync(
            "printf '%s' \"${WECHAT_MONITOR_SERVER_TOKEN:-}|${HTTPS_PROXY:-}|${DATABASE_KEY:-}\" >&2\n" +
            "printf '{\"schemaVersion\":1,\"resultPath\":\"result.json\",\"jobId\":\"job-1\",\"sourceSetId\":\"source-1\"}'");
        using var token = new EnvironmentVariableScope("WECHAT_MONITOR_SERVER_TOKEN", "secret-token");
        using var proxy = new EnvironmentVariableScope("HTTPS_PROXY", "http://proxy.invalid");
        using var key = new EnvironmentVariableScope("DATABASE_KEY", "secret-key");

        var result = await CreateSupervisor(fixture.InstallManifestPath)
            .RunAsync(fixture.JobPath, default);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("||", result.Stderr);
        Assert.DoesNotContain("secret", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StderrIsCappedAt64KiB()
    {
        if (OperatingSystem.IsWindows()) return;
        var fixture = await CreateExecutableAsync(
            "i=0\nwhile [ $i -lt 70000 ]; do printf x >&2; i=$((i+1)); done\n" +
            "printf '{\"schemaVersion\":1}'");

        var result = await CreateSupervisor(fixture.InstallManifestPath)
            .RunAsync(fixture.JobPath, default);

        Assert.Equal(64 * 1024, Encoding.UTF8.GetByteCount(result.Stderr));
        Assert.True(result.StderrTruncated);
    }

    [Fact]
    public async Task SoftDeadlineCreatesCancellationRequestBeforeHardKill()
    {
        if (OperatingSystem.IsWindows()) return;
        var fixture = await CreateExecutableAsync(
            "while [ ! -f cancel.request ]; do sleep 0.01; done\nexit 130");
        var supervisor = CreateSupervisor(
            fixture.InstallManifestPath,
            softTimeout: TimeSpan.FromMilliseconds(100),
            hardTimeout: TimeSpan.FromSeconds(2));

        var result = await supervisor.RunAsync(fixture.JobPath, default);

        Assert.True(result.SoftCancellationRequested);
        Assert.False(result.HardKilled);
        Assert.Equal(130, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(fixture.JobRoot, "cancel.request")));
    }

    [Fact]
    public async Task HardDeadlineKillsEntireUncooperativeProcessTree()
    {
        if (OperatingSystem.IsWindows()) return;
        var fixture = await CreateExecutableAsync("while true; do sleep 1; done");
        var supervisor = CreateSupervisor(
            fixture.InstallManifestPath,
            softTimeout: TimeSpan.FromMilliseconds(50),
            hardTimeout: TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        var result = await supervisor.RunAsync(fixture.JobPath, default);

        Assert.True(result.SoftCancellationRequested);
        Assert.True(result.HardKilled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task NonCompletingPostKillCleanupReturnsStableBoundedFailure()
    {
        if (OperatingSystem.IsWindows()) return;
        var fixture = await CreateExecutableAsync("while true; do sleep 1; done");
        var waiter = new NeverCompletingPostKillWaiter();
        var supervisor = new ParserProcessSupervisor(
            fixture.InstallManifestPath,
            new ParserSupervisorOptions(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(150),
                64 * 1024,
                "wx_parser.exe",
                TimeSpan.FromMilliseconds(100)),
            waiter);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<ParserSupervisorException>(() =>
            supervisor.RunAsync(fixture.JobPath, default));

        Assert.Equal("parser_cleanup_timeout", exception.Code);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Equal(TimeSpan.FromMilliseconds(100), waiter.ObservedGrace);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NormallyExitedParserWithDescendantRetainingPipesHasBoundedCleanup()
    {
        if (OperatingSystem.IsWindows()) return;
        var fixture = await CreateExecutableAsync("(sleep 2) &\nexit 0");
        var supervisor = new ParserProcessSupervisor(
            fixture.InstallManifestPath,
            new ParserSupervisorOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                64 * 1024,
                "wx_parser.exe",
                TimeSpan.FromMilliseconds(100)));
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<ParserSupervisorException>(() =>
            supervisor.RunAsync(fixture.JobPath, default));

        Assert.Equal("parser_cleanup_timeout", exception.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CallerCancellationPropagatesAfterProcessCleanup()
    {
        if (OperatingSystem.IsWindows()) return;
        var fixture = await CreateExecutableAsync(
            "while [ ! -f cancel.request ]; do sleep 0.01; done\nexit 130");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateSupervisor(
                fixture.InstallManifestPath,
                softTimeout: TimeSpan.FromSeconds(2),
                hardTimeout: TimeSpan.FromSeconds(3))
            .RunAsync(fixture.JobPath, cancellation.Token));

        Assert.True(File.Exists(Path.Combine(fixture.JobRoot, "cancel.request")));
    }

    private ParserProcessSupervisor CreateSupervisor(
        string installManifestPath,
        TimeSpan? softTimeout = null,
        TimeSpan? hardTimeout = null) => new(
        installManifestPath,
        new ParserSupervisorOptions(
            softTimeout ?? TimeSpan.FromSeconds(120),
            hardTimeout ?? TimeSpan.FromSeconds(180),
            64 * 1024,
            "wx_parser.exe",
            TimeSpan.FromSeconds(2)));

    private async Task<ProcessFixture> CreateExecutableAsync(string body)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix parser fixture requested on Windows.");
        var installRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"), "parser");
        var jobRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"), "job-1");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(jobRoot);
        var executable = Path.Combine(installRoot, "wx_parser.exe");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var sha256 = await Sha256Async(executable);
        var installManifest = Path.Combine(installRoot, "parser-install.json");
        await File.WriteAllTextAsync(
            installManifest,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                executablePath = executable,
                sha256,
            }));
        var jobPath = Path.Combine(jobRoot, "job.json");
        await File.WriteAllTextAsync(jobPath, "{}");
        return new ProcessFixture(installManifest, executable, jobRoot, jobPath);
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record ProcessFixture(
        string InstallManifestPath,
        string ExecutablePath,
        string JobRoot,
        string JobPath);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        internal EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private sealed class NeverCompletingPostKillWaiter : IParserPostKillWaiter
    {
        internal TimeSpan? ObservedGrace { get; private set; }

        public Task<bool> WaitAsync(Task completion, TimeSpan grace)
        {
            ObservedGrace = grace;
            return Task.FromResult(false);
        }
    }
}
