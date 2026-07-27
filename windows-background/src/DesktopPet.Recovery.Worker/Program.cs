using DesktopPet.Background.Infrastructure;
using DesktopPet.Recovery.Persistence;
using DesktopPet.Recovery.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopPet.Recovery.Worker;

public static class Program
{
    public const int DuplicateInstanceExitCode = 10;

    public static async Task<int> Main(string[] args)
    {
        WorkerCommandMode commandMode;
        try
        {
            commandMode = WorkerCommandLine.Parse(args);
        }
        catch (ArgumentException)
        {
            return 2;
        }

        var paths = BackgroundPaths.ForCurrentUser();
        if (commandMode == WorkerCommandMode.Diagnose)
        {
            Console.Out.WriteLine(await RecoveryDiagnosticReader.ReadJsonAsync(
                paths.RecoveryDatabase,
                CancellationToken.None));
            return 0;
        }

        if (!OperatingSystem.IsWindows()) return 3;
        if (!SingleInstanceGuard.TryAcquire(
                SingleInstanceGuard.DefaultName,
                out var instance))
        {
            return DuplicateInstanceExitCode;
        }

        using (instance)
        {
            var dataRootLocator = new WeChatDataRootLocator();
            var protector = new DpapiSecretProtector();
            var snapshot = new CriticalRecoverySnapshotStore(
                paths.RecoveryCriticalSnapshot,
                protector);
            await using var repository = new RecoveryRepository(
                paths.RecoveryDatabase,
                TimeProvider.System,
                snapshot);
            var startup = new RecoveryBootstrapper(repository, snapshot);
            var validatedKeyVault = new ValidatedKeyVault(
                Path.Combine(paths.RecoveryVault, "ValidatedKeys"),
                protector);
            var telemetryPublisher = new AtomicTelemetryPublisher(
                Path.Combine(paths.HandoffRoot, "Telemetry", "ready"),
                TimeProvider.System);
            var cycle = new RecoveryCycle(
                paths,
                repository,
                new WeChatIdentityProvider(),
                dataRootLocator,
                validatedKeyVault,
                telemetryPublisher);
            var worker = new RecoveryWorker(
                startup,
                cycle,
                [new ProcessStartWatcher(), new SelectedRootDatabaseWatcher(dataRootLocator)],
                RecoveryWorkerOptions.Default,
                TimeProvider.System);
            var runMode = commandMode == WorkerCommandMode.Once
                ? WorkerRunMode.Once
                : WorkerRunMode.Continuous;
            var builder = Host.CreateApplicationBuilder([]);
            builder.Services.AddSingleton(worker);
            builder.Services.AddSingleton(new RecoveryHostOptions(runMode));
            builder.Services.AddHostedService<RecoveryHostedService>();
            using var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
    }
}
