using Footprint.Receiver.Configuration;
using Footprint.Receiver.Mac;
using Footprint.Receiver.Network;
using Footprint.Receiver.State;
using System.Text.Json;

namespace Footprint.Receiver;

public static class ReceiverApplication
{
    public static async Task<int> RunAsync(
        IReceiverConfigurationStore configurationStore,
        IReceiverTokenStore tokenStore,
        Func<ReceiverOptions, IReceiverApiClient> apiFactory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var registration = await LoadRegistrationAsync(configurationStore, tokenStore, output, cancellationToken).ConfigureAwait(false);
        if (registration is null) return 0;
        var (options, token) = registration.Value;
        await using var service = CreateService(configurationStore, apiFactory(options));
        await service.RunAsync(options, token, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public static async Task<int> RunOnceAsync(
        IReceiverConfigurationStore configurationStore,
        IReceiverTokenStore tokenStore,
        Func<ReceiverOptions, IReceiverApiClient> apiFactory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var registration = await LoadRegistrationAsync(configurationStore, tokenStore, output, cancellationToken).ConfigureAwait(false);
        if (registration is null) return 0;
        var (options, token) = registration.Value;
        await using var service = CreateService(configurationStore, apiFactory(options));
        await service.PollOnceAsync(options, token, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static ReceiverPollingService CreateService(IReceiverConfigurationStore configurationStore, IReceiverApiClient api)
    {
        var support = Path.GetDirectoryName((configurationStore as ReceiverConfigurationStore)?.Path ?? ReceiverConfigurationStore.DefaultPath)!;
        var state = new ReceiverStateStore(Path.Combine(support, "receiver-state.db"));
        return ReceiverPollingService.CreateDefault(api, state, Path.Combine(support, "Downloads"),
            Path.Combine(support, "Footprint_Packages"), archiveRoot: Path.Combine(support, "Archives"),
            archiveScratchRoot: Path.Combine(support, "ArchiveScratch"));
    }

    private static async Task<(ReceiverOptions Options, string Token)?> LoadRegistrationAsync(IReceiverConfigurationStore configurationStore, IReceiverTokenStore tokenStore, TextWriter output, CancellationToken cancellationToken)
    {
        ReceiverOptions? options;
        string? token;
        try
        {
            options = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            token = await tokenStore.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (token is not null) ReceiverToken.Validate(token);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException or FormatException)
        {
            await output.WriteLineAsync("尚未完成接收端注册。").ConfigureAwait(false);
            return null;
        }
        if (options is not null && !string.IsNullOrWhiteSpace(token)) return (options, token);
        await output.WriteLineAsync("尚未完成接收端注册。").ConfigureAwait(false);
        return null;
    }
}
