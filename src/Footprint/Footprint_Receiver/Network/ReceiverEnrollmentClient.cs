using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Footprint.Receiver.Mac;

namespace Footprint.Receiver.Network;

public sealed record ReceiverEnrollmentResult(string Token, string DeviceId);

public interface IReceiverEnrollmentClient
{
    Task<ReceiverEnrollmentResult> EnrollAsync(Uri serverUri, char[] registrationCode, string deviceId, string displayName, CancellationToken cancellationToken = default);
}

public sealed class ReceiverEnrollmentClient(HttpClient httpClient, ReceiverNetworkTimeouts? timeouts = null) : IReceiverEnrollmentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan _streamIdleTimeout = (timeouts ?? ReceiverNetworkTimeouts.FromEnvironment()).StreamIdleTimeout;

    public async Task<ReceiverEnrollmentResult> EnrollAsync(Uri serverUri, char[] registrationCode, string deviceId, string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrationCode);
        MemoryStream? buffer = null;
        byte[]? payload = null;
        try
        {
            ValidateHttps(serverUri);
            if (registrationCode.Length == 0) throw new ArgumentException("注册码不能为空。", nameof(registrationCode));
            ValidateDeviceId(deviceId);
            ValidateDisplayName(displayName);

            buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = false }))
            {
                writer.WriteStartObject();
                writer.WriteString("registrationCode", registrationCode);
                writer.WriteString("deviceId", deviceId);
                writer.WriteString("displayName", displayName);
                writer.WriteEndObject();
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(NormalizeBase(serverUri), "api/footprint/receiver/enroll"));
            payload = buffer.ToArray();
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var responseStream = new IdleTimeoutReadStream(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _streamIdleTimeout);
            var result = await JsonSerializer.DeserializeAsync<EnrollmentResponse>(responseStream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("注册响应为空。");
            if (!string.Equals(result.DeviceId, deviceId, StringComparison.Ordinal)) throw new InvalidDataException("注册响应 DeviceId 与请求不一致。");
            ReceiverToken.Validate(result.Token);
            return new ReceiverEnrollmentResult(result.Token, result.DeviceId);
        }
        finally
        {
            Array.Clear(registrationCode);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            if (buffer is not null)
            {
                if (buffer.TryGetBuffer(out var segment)) CryptographicOperations.ZeroMemory(segment.AsSpan());
                buffer.Dispose();
            }
        }
    }

    internal static Uri NormalizeBase(Uri uri)
    {
        ValidateHttps(uri);
        return new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/", UriKind.Absolute);
    }

    internal static void ValidateHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("服务器地址必须使用 HTTPS。", nameof(uri));
    }

    internal static void ValidateDeviceId(string value)
    {
        if (value is not { Length: >= 1 and <= 128 } || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new ArgumentException("DeviceId 无效。", nameof(value));
    }

    internal static void ValidateDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("显示名称无效。", nameof(value));
    }

    private sealed record EnrollmentResponse(string Token, string DeviceId);
}
