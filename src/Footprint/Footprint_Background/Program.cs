using Footprint.Background;
using Footprint.Core.Runtime;
using Footprint.Core.State;

var options = FootprintBackgroundOptions.Parse(args);
if (!FootprintStartupGate.CanStart(options.Paths)) return 0;
using var instance = FootprintSingleInstance.TryAcquire(options.InstanceName);
if (instance is null) return 0;
var store = new SqliteFootprintStateStore(Path.Combine(options.Paths.StateDirectory, "Footprint_State.db"));
WindowsBackgroundProductionRuntime? production = null;
if (!options.TestMode)
{
    var configuration = new WindowsProductionConfigurationStore(
        options.Paths.StateDirectory, AppContext.BaseDirectory).LoadOrProvision();
    var deviceId = new WindowsDeviceIdentity(Path.Combine(options.Paths.StateDirectory, "device-id.txt"))
        .GetOrCreate();
    var outbox = new SourceEventOutbox(Path.Combine(options.Paths.StateDirectory, "Footprint_EventOutbox"), deviceId);
    var audit = new KeyExtractionAuditLog(Path.Combine(options.Paths.LogsDirectory, "key-extraction.log"), outbox);
    var coordinator = new WindowsRunCoordinator(options.Paths, configuration, deviceId, audit: audit);
    var dispatcher = new BackgroundCommandDispatcher(deviceId, coordinator, store);
    var results = new RemoteCommandExecutionStore(Path.Combine(options.Paths.StateDirectory, "Footprint_CommandResults"));
    var forwarder = new SourceEventForwarder(new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
        new Uri(configuration.ServerBaseUri, UriKind.Absolute), configuration.CommandBearerToken, outbox,
        configuration.CommandPollInterval);
    production = new WindowsBackgroundProductionRuntime(coordinator,
        new RemoteCommandPoller(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, configuration,
            deviceId, dispatcher, results, audit), forwarder, audit);
}
var host = new FootprintBackgroundHost(options.Paths, store, production);
await host.RunAsync(options.PipeName, options.TestMode, CancellationToken.None);
return 0;
