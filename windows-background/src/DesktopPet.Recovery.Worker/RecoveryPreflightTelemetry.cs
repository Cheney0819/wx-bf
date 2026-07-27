using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.Recovery;

namespace DesktopPet.Recovery.Worker;

public sealed class RecoveryPreflightTelemetry
{
    private readonly IOperationalTelemetryPublisher _publisher;

    public RecoveryPreflightTelemetry(
        IOperationalTelemetryPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    public async Task PublishDataRootResultAsync(
        WeChatDataRootResolution resolution,
        bool wechatLoggedIn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (cancellationToken.IsCancellationRequested) return;
        try
        {
            await _publisher.PublishAsync(
                new OperationalTelemetryDraft(
                    "recovery",
                    "client_v4_data_dir_result",
                    resolution.Found ? "info" : "warning",
                    resolution.Code,
                    JsonSerializer.SerializeToElement(new
                    {
                        candidateCount = resolution.CandidateCount,
                        databaseCount = resolution.DatabaseCount,
                        wechatLoggedIn,
                    })),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Preflight visibility remains best effort relative to recovery work.
        }
    }
}

