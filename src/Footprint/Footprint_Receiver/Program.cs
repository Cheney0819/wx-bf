using Footprint.Receiver;
using Footprint.Receiver.Configuration;
using Footprint.Receiver.Internal;
using Footprint.Receiver.Mac;
using Footprint.Receiver.Network;
using Footprint.ReceiverRuntime;

ReceiverRuntimeVerifier.VerifyCurrent();
var configuration = new ReceiverConfigurationStore(ReceiverConfigurationStore.DefaultPath);
var tokens = new ReceiverTokenStore(new KeychainNative());
var timeouts = ReceiverNetworkTimeouts.FromEnvironment();
using var lifetime = ReceiverProcessLifetime.Create();

if (args.Length > 0 && args[0] == "enroll")
{
    using var http = ReceiverHttpClientFactory.Create(timeouts);
    var command = new EnrollmentCommand(new ReceiverEnrollmentClient(http, timeouts), tokens, configuration);
    var displayName = Environment.MachineName;
    await command.ExecuteAsync(args[1..], Console.OpenStandardInput(), displayName, lifetime.Token);
    Console.WriteLine("接收端注册完成。");
    return;
}

if (args.Length > 0 && args[0] == "viewer")
{
    var support = Path.GetDirectoryName(configuration.Path)!;
    Environment.ExitCode = await new ReceiverViewerCommand(Path.Combine(support, "Archives"))
        .ExecuteAsync(args[1..], Console.Out, Console.Error, lifetime.Token);
    return;
}

if (args.Length == 1 && args[0] == "run")
{
    using var http = ReceiverHttpClientFactory.Create(timeouts);
    try
    {
        await ReceiverApplication.RunAsync(configuration, tokens, options => new HttpReceiverApiClient(options.ServerUri, http, timeouts), Console.Out, lifetime.Token);
    }
    catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
    {
        Console.Error.WriteLine(exception.Message);
        Environment.ExitCode = 3;
    }
    return;
}

Console.Error.WriteLine("用法：Footprint_Receiver enroll --server-uri <https-url> --registration-code-stdin | run | viewer <run-id> sessions | viewer <run-id> search <query>");
Environment.ExitCode = 2;
