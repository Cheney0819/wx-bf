using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.DataSync.Identity;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;

namespace DesktopPet.DataSync.Telemetry;

/// <summary>Builds encrypted status heartbeats and atomically coalesces pending status rows.</summary>
public sealed class StatusOutboxWriter
{
    private readonly DataSyncRepository _repository;
    private readonly IOutboxProtector _protector;
    private readonly ClientIdentityDocument _identity;
    private readonly TimeProvider _timeProvider;

    public StatusOutboxWriter(
        DataSyncRepository repository,
        IOutboxProtector protector,
        ClientIdentityDocument identity,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _repository = repository;
        _protector = protector;
        _identity = identity;
        _timeProvider = timeProvider;
    }

    public Task EnqueueHeartbeatAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(cancellationToken);

    public Task WriteHeartbeatAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(cancellationToken);

    public async Task EnqueueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var heartbeatSequence = await _repository.AllocateHeartbeatSequenceAsync(
            _identity.SessionId,
            cancellationToken);
        var requestId = CreateRequestId(heartbeatSequence);
        var payload = new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["session_id"] = _identity.SessionId,
            ["source"] = _identity.Source,
            ["heartbeat_sequence"] = heartbeatSequence,
        };
        foreach (var key in new[] { "decrypt_ok", "wechat_logged_in" })
        {
            var state = await _repository.GetOperationalStateAsync(key, cancellationToken);
            if (state is not null)
                payload[key] = ParseState(state.ValueJson);
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[]? ciphertext = null;
        try
        {
            ciphertext = _protector.Protect(requestId, "status", plaintext);
            await _repository.EnqueueStatusOutboxAsync(
                new OutboxDraft(
                    requestId,
                    $"status:{_identity.SessionId}:{requestId}",
                    "status",
                    plaintext),
                _identity.SessionId,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public Task WriteAsync(CancellationToken cancellationToken) => EnqueueAsync(cancellationToken);

    private string CreateRequestId(long heartbeatSequence)
    {
        var material = JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            "desktop-pet-datasync-status-v2",
            _identity.SessionId,
            heartbeatSequence,
        });
        try { return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(material); }
    }

    private static object ParseState(string value) =>
        bool.TryParse(value, out var boolean) ? boolean : value;
}
