namespace Footprint.Receiver.Network;

public sealed record PendingRun(string RunId, long PackageLength, string PackageSha256, string Status,
    string SourceDeviceId = "legacy-source");
public sealed record ReceiverStatusUpdate(string RunId, string Status);

public sealed class ReceiverPackageResponse(Stream content, long packageLength, string packageSha256,
    string sourceDeviceId = "legacy-source") : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public long PackageLength { get; } = packageLength;
    public string PackageSha256 { get; } = packageSha256;
    public string SourceDeviceId { get; } = sourceDeviceId;
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IReceiverApiClient
{
    Task<IReadOnlyList<PendingRun>> GetPendingAsync(string token, CancellationToken cancellationToken);
    Task<ReceiverPackageResponse> DownloadAsync(string runId, string token, CancellationToken cancellationToken);
    Task ReportStatusAsync(ReceiverStatusUpdate update, string token, CancellationToken cancellationToken);
}
