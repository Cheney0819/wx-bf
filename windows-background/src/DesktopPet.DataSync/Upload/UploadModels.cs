namespace DesktopPet.DataSync.Upload;

public sealed record ServerSettings(Uri BaseUri, string Token);

public interface IServerSettingsProvider
{
    Task<ServerSettings?> TryLoadAsync(CancellationToken cancellationToken);
}

public interface IUploadBackoff
{
    TimeSpan GetDelay(int attemptCount, TimeSpan? retryAfter);
}

public enum UploadDisposition
{
    Idle,
    CredentialMissing,
    Offline,
    Acknowledged,
    RetryScheduled,
    Quarantined,
}

public sealed record UploadResult(
    UploadDisposition Disposition,
    string? OutboxId,
    int StatusCode,
    string? Endpoint = null);

public sealed class FullJitterUploadBackoff : IUploadBackoff
{
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(15);

    public TimeSpan GetDelay(int attemptCount, TimeSpan? retryAfter)
    {
        var exponent = Math.Clamp(attemptCount, 0, 30);
        var capSeconds = Math.Min(Math.Pow(2, exponent), MaximumDelay.TotalSeconds);
        var jitter = TimeSpan.FromSeconds(Random.Shared.NextDouble() * capSeconds);
        if (retryAfter is null) return jitter;
        var boundedRetryAfter = retryAfter.Value < TimeSpan.Zero
            ? TimeSpan.Zero
            : retryAfter.Value > MaximumDelay
                ? MaximumDelay
                : retryAfter.Value;
        return boundedRetryAfter > jitter ? boundedRetryAfter : jitter;
    }
}
