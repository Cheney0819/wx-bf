using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;

namespace DesktopPet.DataSync.Upload;

public sealed class OutboxUploader
{
    private static readonly IReadOnlyDictionary<string, string> EndpointPaths =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["messages"] = "api/messages",
            ["contacts"] = "api/contacts",
            ["favorites"] = "api/favorites",
            ["status"] = "api/status",
            ["events"] = "api/events",
        };

    private readonly DataSyncRepository _repository;
    private readonly IOutboxProtector _protector;
    private readonly IServerSettingsProvider _settingsProvider;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly IUploadBackoff _backoff;
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private string? _credentialFingerprint;

    public OutboxUploader(
        DataSyncRepository repository,
        IOutboxProtector protector,
        IServerSettingsProvider settingsProvider,
        HttpClient httpClient,
        TimeProvider timeProvider,
        IUploadBackoff backoff)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(settingsProvider);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(backoff);
        _repository = repository;
        _protector = protector;
        _settingsProvider = settingsProvider;
        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _backoff = backoff;
    }

    public async Task<UploadResult> UploadOneAsync(
        string workerId,
        CancellationToken cancellationToken)
    {
        var settings = await _settingsProvider.TryLoadAsync(cancellationToken);
        if (settings is null)
        {
            return new UploadResult(UploadDisposition.CredentialMissing, null, 0, null);
        }
        await RequeueAuthenticationFailuresIfCredentialChangedAsync(settings, cancellationToken);
        var row = await _repository.TryClaimOutboxAsync(
            workerId,
            TimeSpan.FromMinutes(3),
            cancellationToken);
        if (row is null) return new UploadResult(UploadDisposition.Idle, null, 0, null);
        if (!EndpointPaths.TryGetValue(row.Endpoint, out var endpointPath))
        {
            await _repository.QuarantineOutboxAsync(
                row.Id, workerId, 0, "unknown_endpoint", cancellationToken);
            return new UploadResult(UploadDisposition.Quarantined, row.Id, 0, row.Endpoint);
        }

        byte[]? plaintext = null;
        byte[]? authenticated = null;
        try
        {
            try
            {
                plaintext = _protector.Unprotect(row.Id, row.Endpoint, row.Ciphertext);
                authenticated = AddToken(plaintext, settings.Token);
            }
            catch (CryptographicException)
            {
                await _repository.QuarantineOutboxAsync(
                    row.Id, workerId, 0, "ciphertext_invalid", cancellationToken);
                return new UploadResult(UploadDisposition.Quarantined, row.Id, 0, row.Endpoint);
            }
            catch (JsonException)
            {
                await _repository.QuarantineOutboxAsync(
                    row.Id, workerId, 0, "payload_invalid", cancellationToken);
                return new UploadResult(UploadDisposition.Quarantined, row.Id, 0, row.Endpoint);
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(settings.BaseUri, endpointPath));
            request.Content = new ByteArrayContent(authenticated);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return await ScheduleRetryAsync(
                    row, workerId, 0, "request_timeout", retryAfter: null, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return await ScheduleRetryAsync(
                    row, workerId, 0, "network_error", retryAfter: null, cancellationToken);
            }

            using (response)
            {
                byte[] responseBytes;
                try
                {
                    responseBytes = await ReadResponseAsync(response, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return await ScheduleRetryAsync(
                        row, workerId, 0, "response_timeout", retryAfter: null, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    return await ScheduleRetryAsync(
                        row, workerId, 0, "response_network_error", retryAfter: null, cancellationToken);
                }
                catch (IOException)
                {
                    return await ScheduleRetryAsync(
                        row, workerId, 0, "response_io_error", retryAfter: null, cancellationToken);
                }
                try
                {
                    var statusCode = (int)response.StatusCode;
                    var summary = SanitizeSummary(responseBytes, settings.Token);
                    if (response.IsSuccessStatusCode &&
                        IsValidSuccessResponse(row.Endpoint, responseBytes))
                    {
                        await _repository.AcknowledgeOutboxAsync(
                            row.Id, workerId, statusCode, cancellationToken);
                        return new UploadResult(
                            UploadDisposition.Acknowledged,
                            row.Id,
                            statusCode,
                            row.Endpoint);
                    }
                    if (response.IsSuccessStatusCode)
                    {
                        return await ScheduleRetryAsync(
                            row,
                            workerId,
                            statusCode,
                            "invalid_success_response",
                            retryAfter: null,
                            cancellationToken);
                    }
                    if (IsTransient(response.StatusCode))
                    {
                        return await ScheduleRetryAsync(
                            row,
                            workerId,
                            statusCode,
                            summary,
                            RetryAfter(response),
                            cancellationToken);
                    }
                    await _repository.QuarantineOutboxAsync(
                        row.Id,
                        workerId,
                        statusCode,
                        summary,
                        cancellationToken);
                    return new UploadResult(
                        UploadDisposition.Quarantined,
                        row.Id,
                        statusCode,
                        row.Endpoint);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(responseBytes);
                }
            }
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (authenticated is not null) CryptographicOperations.ZeroMemory(authenticated);
        }
    }

    private async Task<UploadResult> ScheduleRetryAsync(
        OutboxRecord row,
        string workerId,
        int statusCode,
        string summary,
        TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        var delay = _backoff.GetDelay(row.AttemptCount, retryAfter);
        await _repository.ScheduleOutboxRetryAsync(
            row.Id,
            workerId,
            _timeProvider.GetUtcNow() + delay,
            statusCode,
            summary,
            cancellationToken);
        return new UploadResult(UploadDisposition.RetryScheduled, row.Id, statusCode, row.Endpoint);
    }

    private static byte[] AddToken(byte[] payload, string token)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.TryGetProperty("token", out _))
        {
            throw new JsonException("Outbox payload root is invalid.");
        }
        var buffer = new ArrayBufferWriter<byte>(payload.Length + token.Length + 32);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
                property.WriteTo(writer);
            writer.WriteString("token", token);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private async Task RequeueAuthenticationFailuresIfCredentialChangedAsync(
        ServerSettings settings,
        CancellationToken cancellationToken)
    {
        var fingerprint = ComputeCredentialFingerprint(settings);
        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(_credentialFingerprint, fingerprint, StringComparison.Ordinal))
                return;
            await _repository.RequeueQuarantinedOutboxAsync([401, 403], cancellationToken);
            _credentialFingerprint = fingerprint;
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    private static string ComputeCredentialFingerprint(ServerSettings settings)
    {
        var bytes = Encoding.UTF8.GetBytes($"{settings.BaseUri}\n{settings.Token}");
        var digest = SHA256.HashData(bytes);
        try
        {
            return Convert.ToHexString(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static async Task<byte[]> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int limit = 64 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (output.Length < limit)
        {
            var remaining = limit - checked((int)output.Length);
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool IsValidSuccessResponse(string endpoint, byte[] response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind != JsonValueKind.True)
            {
                return false;
            }
            return endpoint switch
            {
                "messages" => HasNumber(root, "added") || HasNumber(root, "count"),
                "contacts" or "favorites" =>
                    HasNumber(root, "changed") || HasNumber(root, "count"),
                "status" or "events" => true,
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasNumber(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out _);

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500 ||
        (int)statusCode is >= 300 and < 400;

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is not null) return header.Delta;
        if (header?.Date is not null)
        {
            var delta = header.Date.Value - _timeProvider.GetUtcNow();
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }
        return null;
    }

    private static string SanitizeSummary(ReadOnlySpan<byte> response, string token)
    {
        var text = Encoding.UTF8.GetString(response);
        if (!string.IsNullOrEmpty(token))
            text = text.Replace(token, "[redacted]", StringComparison.Ordinal);
        var sanitized = new string(text
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray())
            .Trim();
        return sanitized.Length <= 256 ? sanitized : sanitized[..256];
    }
}
