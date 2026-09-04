using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Footprint.Receiver.Mac;

namespace Footprint.Receiver.Network;

public sealed class HttpReceiverApiClient : IReceiverApiClient
{
    private readonly Uri _serverUri;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _streamIdleTimeout;

    public HttpReceiverApiClient(Uri serverUri, HttpClient httpClient, ReceiverNetworkTimeouts? timeouts = null)
    {
        _serverUri = ReceiverEnrollmentClient.NormalizeBase(serverUri);
        _httpClient = httpClient;
        _streamIdleTimeout = (timeouts ?? ReceiverNetworkTimeouts.FromEnvironment()).StreamIdleTimeout;
    }

    public async Task<IReadOnlyList<PendingRun>> GetPendingAsync(string token, CancellationToken cancellationToken)
    {
        using var request = Create(HttpMethod.Get, "api/footprint/receiver/pending", token);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        EnsureReceiverSuccess(response);
        await using var stream = new IdleTimeoutReadStream(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _streamIdleTimeout);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var array = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement : document.RootElement.TryGetProperty("runs", out var runs) ? runs : throw new InvalidDataException("pending 响应格式无效。");
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("pending 响应格式无效。");
        var result = new List<PendingRun>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("runId", out var runIdElement) || runIdElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(runIdElement.GetString())) continue;
            var runId = runIdElement.GetString()!;
            try { PackageIdentity.ValidateRunId(runId); }
            catch (InvalidDataException) { continue; }

            var length = item.TryGetProperty("packageLength", out var lengthElement) && lengthElement.ValueKind == JsonValueKind.Number && lengthElement.TryGetInt64(out var parsedLength)
                ? parsedLength
                : -1;
            var sha256 = item.TryGetProperty("packageSha256", out var hashElement) && hashElement.ValueKind == JsonValueKind.String
                ? hashElement.GetString() ?? ""
                : "";
            var status = item.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString() ?? "Pending"
                : "Pending";
            var sourceDeviceId = item.TryGetProperty("sourceDeviceId", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.String
                ? sourceElement.GetString() ?? ""
                : "";
            PackageIdentity.ValidateDeviceId(sourceDeviceId);
            result.Add(new PendingRun(runId, length, sha256, status, sourceDeviceId));
        }
        return result;
    }

    public async Task<ReceiverPackageResponse> DownloadAsync(string runId, string token, CancellationToken cancellationToken)
    {
        PackageIdentity.ValidateRunId(runId);
        using var request = Create(HttpMethod.Get, $"api/footprint/receiver/runs/{Uri.EscapeDataString(runId)}/package", token);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReceiverSuccess(response);
            var length = response.Content.Headers.ContentLength ?? throw new InvalidDataException("下载响应缺少 Content-Length。");
            if (!response.Headers.TryGetValues("X-Footprint-Package-Sha256", out var hashes)) throw new InvalidDataException("下载响应缺少包摘要。");
            var hash = hashes.Single();
            if (!response.Headers.TryGetValues("X-Footprint-Source-Device-Id", out var sources)) throw new InvalidDataException("下载响应缺少来源 DeviceId。");
            var sourceDeviceId = sources.Single();
            PackageIdentity.ValidateDeviceId(sourceDeviceId);
            PackageIdentity.Validate(runId, length, hash);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ReceiverPackageResponse(new OwnedResponseStream(new IdleTimeoutReadStream(stream, _streamIdleTimeout), response), length, hash, sourceDeviceId);
        }
        catch { response.Dispose(); throw; }
    }

    public async Task ReportStatusAsync(ReceiverStatusUpdate update, string token, CancellationToken cancellationToken)
    {
        PackageIdentity.ValidateRunId(update.RunId);
        if (update.Status is not ("已接收" or "已完成" or "失败")) throw new ArgumentException("接收状态无效。", nameof(update));
        using var request = Create(HttpMethod.Post, $"api/footprint/receiver/runs/{Uri.EscapeDataString(update.RunId)}/status", token);
        request.Content = JsonContent.Create(new { status = update.Status });
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        EnsureReceiverSuccess(response);
    }

    private HttpRequestMessage Create(HttpMethod method, string relative, string token)
    {
        ReceiverToken.Validate(token);
        var request = new HttpRequestMessage(method, new Uri(_serverUri, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void EnsureReceiverSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("接收端凭据已失效，请重新执行 enroll 注册。", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    private sealed class OwnedResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false; public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count); public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); response.Dispose(); GC.SuppressFinalize(this); }
    }
}

internal static class PackageIdentity
{
    public static void Validate(string runId, long length, string sha256) { ValidateRunId(runId); if (length < 0) throw new InvalidDataException("包长度无效。"); if (sha256.Length != 64 || sha256.Any(value => !((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f')))) throw new InvalidDataException("包摘要必须为 lowercase SHA-256。"); }
    public static void ValidateDeviceId(string value) { if (value is not { Length: >= 1 and <= 128 } || !char.IsAsciiLetterOrDigit(value[0]) || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new InvalidDataException("来源 DeviceId 无效。"); }
    public static void ValidateRunId(string runId) { if (string.IsNullOrWhiteSpace(runId) || runId.Length > 128 || runId.StartsWith("Footprint_Run_", StringComparison.OrdinalIgnoreCase) || !IsLowercaseAsciiLetterOrDigit(runId[0]) || runId.Any(value => !(IsLowercaseAsciiLetterOrDigit(value) || value is '-' or '_'))) throw new InvalidDataException("RunId 无效。"); }
    private static bool IsLowercaseAsciiLetterOrDigit(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
