using DesktopPet.Background.Infrastructure;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;
using DesktopPet.DataSync.Identity;
using DesktopPet.DataSync.Upload;
using DesktopPet.DataSync.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopPet.DataSync.Worker;

public static class Program
{
    public const int DuplicateInstanceExitCode = 10;

    public static async Task<int> Main(string[] args)
    {
        DataSyncCommandMode commandMode;
        try
        {
            commandMode = DataSyncCommandLine.Parse(args);
        }
        catch (ArgumentException)
        {
            return 2;
        }

        var paths = BackgroundPaths.ForCurrentUser();
        if (commandMode == DataSyncCommandMode.Diagnose)
        {
            Console.Out.WriteLine(await DataSyncDiagnosticReader.ReadJsonAsync(
                paths.SyncDatabase,
                CancellationToken.None));
            return 0;
        }
        if (!OperatingSystem.IsWindows()) return 3;
        if (!SingleInstanceGuard.TryAcquire(SingleInstanceGuard.DefaultName, out var instance))
            return DuplicateInstanceExitCode;

        using (instance)
        {
            var protector = new WindowsCurrentUserSecretProtector();
            var outboxProtector = new EncryptedOutboxProtector(protector);
            await using var repository = new DataSyncRepository(
                paths.SyncDatabase,
                TimeProvider.System,
                outboxProtector);
            var settingsPath = Path.Combine(
                paths.DataSyncRoot,
                "server-settings.dpapi");
            var settings = new ServerSettingsVault(settingsPath, protector);
            var legacyConfigPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "monitor_config.json"),
                Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "monitor_config.json")),
                Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "wechat_data",
                    "monitor_config.json")),
            };
            var settingsBootstrapper = new ServerSettingsBootstrapper(
                settings,
                settingsPath,
                legacyConfigPaths,
                Environment.GetEnvironmentVariable,
                new ServerSettings(
                    new Uri("https://wx.junjiee.online/"),
                    "wx_monitor_2026"));
            await settingsBootstrapper.EnsureAsync(CancellationToken.None);
            var identity = await new ClientIdentityStore(
                    Path.Combine(paths.DataSyncRoot, "client-identity.json"),
                    Path.Combine(AppContext.BaseDirectory, "wechat_data", "client_identity.json"),
                    TimeProvider.System)
                .GetAsync(CancellationToken.None);
            var acceptance = new HandoffAcceptancePublisher(
                paths.HandoffAccepted,
                TimeProvider.System);
            var importer = new HandoffManifestImporter(
                repository,
                paths.RecoveryGenerations,
                acceptance,
                TimeProvider.System);
            var jobsRoot = Path.Combine(paths.DataSyncRoot, "Jobs");
            var builder = new ParserJobBuilder(jobsRoot);
            var parserInstall = Path.Combine(
                AppContext.BaseDirectory,
                "Parser",
                "parser-install.json");
            var supervisor = new ParserProcessSupervisor(parserInstall);
            var validator = new ParserResultValidator();
            var writer = new IncrementalOutboxWriter(
                repository,
                outboxProtector,
                identity,
                TimeProvider.System);
            var telemetryWriter = new TelemetryOutboxWriter(
                repository,
                outboxProtector,
                identity,
                TimeProvider.System);
            var telemetryImporter = new TelemetryHandoffImporter(
                new TelemetryEnvelopeValidator(),
                telemetryWriter,
                Path.Combine(paths.HandoffRoot, "Telemetry", "rejected"));
            var statusWriter = new StatusOutboxWriter(
                repository,
                outboxProtector,
                identity,
                TimeProvider.System);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(90),
            };
            var uploader = new OutboxUploader(
                repository,
                outboxProtector,
                settings,
                httpClient,
                TimeProvider.System,
                new FullJitterUploadBackoff());
            var runtime = new DataSyncRuntime(
                paths.HandoffReady,
                jobsRoot,
                $"parser-{Environment.ProcessId}",
                repository,
                importer,
                builder,
                supervisor,
                validator,
                writer,
                uploader,
                statusWriter,
                telemetryImporter,
                telemetryWriter);
            var worker = new DataSyncWorker(
                runtime,
                [
                    new HandoffReadyWatcher(paths.HandoffReady),
                    new HandoffReadyWatcher(
                        Path.Combine(paths.HandoffRoot, "Telemetry", "ready"),
                        DataSyncHintKind.Reconciliation),
                ],
                DataSyncWorkerOptions.Default,
                TimeProvider.System);
            var runMode = commandMode == DataSyncCommandMode.Once
                ? DataSyncRunMode.Once
                : DataSyncRunMode.Continuous;
            var hostBuilder = Host.CreateApplicationBuilder([]);
            hostBuilder.Services.AddSingleton(worker);
            hostBuilder.Services.AddSingleton(new DataSyncHostOptions(runMode));
            hostBuilder.Services.AddHostedService<DataSyncHostedService>();
            using var host = hostBuilder.Build();
            await host.RunAsync();
            return 0;
        }
    }
}
