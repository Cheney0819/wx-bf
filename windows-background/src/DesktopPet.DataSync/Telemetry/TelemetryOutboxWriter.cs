using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.DataSync.Identity;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;

namespace DesktopPet.DataSync.Telemetry;

public interface ITelemetryCommitObserver
{
    Task BeforeCommitAsync(CancellationToken cancellationToken);
}

public sealed class TelemetryOutboxWriter
{
    private readonly DataSyncRepository _repository;
    private readonly IOutboxProtector _protector;
    private readonly ClientIdentityDocument _identity;
    private readonly ITelemetryCommitObserver _observer;

    public TelemetryOutboxWriter(
        DataSyncRepository repository,
        IOutboxProtector protector,
        ClientIdentityDocument identity,
        TimeProvider timeProvider,
        ITelemetryCommitObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _repository = repository;
        _protector = protector;
        _identity = identity;
        _observer = observer ?? NoOpTelemetryCommitObserver.Instance;
    }

    public async Task<TelemetryImportResult> CommitAsync(
        OperationalTelemetryEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        TelemetryEnvelopeValidator.Validate(envelope);
        var requestId = Sha256(
            $"desktop-pet-datasync-event-v1|{_identity.SessionId}|{envelope.EventId}");
        var plaintext = SerializePayload(envelope, requestId);
        byte[]? ciphertext = null;
        try
        {
            ciphertext = _protector.Protect(requestId, "events", plaintext);
            var draft = new TelemetryCommitDraft(
                envelope,
                requestId,
                ciphertext,
                ReadOperationalState(envelope));
            return await _repository.ImportTelemetryAsync(
                draft,
                _observer.BeforeCommitAsync,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    internal Task RecordRejectedAsync(
        string payloadJson,
        CancellationToken cancellationToken) =>
        _repository.RecordRuntimeEventAsync(
            "datasync_handoff_rejected",
            payloadJson,
            cancellationToken);

    private byte[] SerializePayload(OperationalTelemetryEnvelope envelope, string requestId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["session_id"] = _identity.SessionId,
            ["source"] = _identity.Source,
            ["event_name"] = envelope.EventName,
            ["payload"] = new Dictionary<string, object?>
            {
                ["event_id"] = envelope.EventId,
                ["component"] = envelope.Component,
                ["severity"] = envelope.Severity,
                ["code"] = envelope.Code,
                ["occurred_at_utc"] = envelope.OccurredAtUtc,
                ["metrics"] = envelope.Metrics,
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static IReadOnlyDictionary<string, string> ReadOperationalState(
        OperationalTelemetryEnvelope envelope)
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.Equals(envelope.Component, "recovery", StringComparison.Ordinal))
        {
            if (envelope.Severity is "warning" or "error")
            {
                state["error"] = envelope.Code;
            }
            else if (envelope.EventName is
                "client_v4_data_dir_result" or
                "recovery_capture_succeeded" or
                "recovery_handoff_published")
            {
                state["error"] = "";
            }

            if (string.Equals(envelope.EventName, "recovery_capture_failed", StringComparison.Ordinal))
                state["decrypt_ok"] = "false";
            else if (string.Equals(envelope.EventName, "recovery_capture_succeeded", StringComparison.Ordinal) &&
                     string.Equals(envelope.Code, "key_validated", StringComparison.Ordinal))
                state["decrypt_ok"] = "true";
            else if (string.Equals(
                         envelope.EventName,
                         "client_wechat_decrypt_export_result",
                         StringComparison.Ordinal))
            {
                var decryptSucceeded = string.Equals(
                    envelope.Code,
                    "success",
                    StringComparison.Ordinal);
                state["decrypt_ok"] = decryptSucceeded ? "true" : "false";
                if (decryptSucceeded) state["error"] = "";
            }

            if (envelope.Metrics.ValueKind == JsonValueKind.Object &&
                envelope.Metrics.TryGetProperty("wechatLoggedIn", out var loggedIn) &&
                loggedIn.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                state["wechat_logged_in"] = loggedIn.GetBoolean() ? "true" : "false";
            }
        }
        return state;
    }

    private static string Sha256(string material)
    {
        var bytes = Encoding.UTF8.GetBytes(material);
        var digest = SHA256.HashData(bytes);
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private sealed class NoOpTelemetryCommitObserver : ITelemetryCommitObserver
    {
        internal static NoOpTelemetryCommitObserver Instance { get; } = new();

        public Task BeforeCommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
