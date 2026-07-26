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
            var knownRoots = KnownDataRoots();
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
            var cycle = new RecoveryCycle(
                paths,
                repository,
                new WeChatIdentityProvider(),
                knownRoots,
                validatedKeyVault);
            var worker = new RecoveryWorker(
                startup,
                cycle,
                [new ProcessStartWatcher(), new KnownRootDatabaseWatcher(knownRoots)],
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

    private static IReadOnlyList<string> KnownDataRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            roots.Add(Path.Combine(profile, "Documents", "xwechat_files"));
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
            roots.Add(Path.Combine(documents, "xwechat_files"));
        return Array.AsReadOnly(roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
