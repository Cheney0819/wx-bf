using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.DataSync.Persistence;

namespace DesktopPet.DataSync.Telemetry;

public sealed class TelemetryHandoffImporter
{
    private readonly TelemetryEnvelopeValidator _validator;
    private readonly TelemetryOutboxWriter _writer;
    private readonly string _readyRoot;
    private readonly string _rejectedRoot;

    public TelemetryHandoffImporter(
        TelemetryEnvelopeValidator validator,
        TelemetryOutboxWriter writer,
        string rejectedRoot)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedRoot);
        _validator = validator;
        _writer = writer;
        _rejectedRoot = Path.GetFullPath(rejectedRoot);
        _readyRoot = Path.Combine(
            Path.GetDirectoryName(_rejectedRoot) ??
                throw new ArgumentException("Rejected root has no parent directory.", nameof(rejectedRoot)),
            "ready");
    }

    public async Task<TelemetryImportResult> ImportAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = ValidateReadyPath(path);

        OperationalTelemetryEnvelope envelope;
        try
        {
            envelope = await _validator.ValidateAsync(fullPath, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return await RejectAsync(fullPath, cancellationToken);
        }

        var result = await _writer.CommitAsync(envelope, cancellationToken);
        File.Delete(fullPath);
        return result;
    }

    public async Task<(DateTimeOffset OccurredAtUtc, string EventId)> ReadOccurrenceOrderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var envelope = await _validator.ValidateAsync(
            ValidateReadyPath(path),
            cancellationToken);
        return (envelope.OccurredAtUtc, envelope.EventId);
    }

    private string ValidateReadyPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(
                Path.GetDirectoryName(fullPath),
                _readyRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Telemetry handoff is outside the ready directory.");
        }
        return fullPath;
    }

    private async Task<TelemetryImportResult> RejectAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_rejectedRoot);
        var fileName = Path.GetFileName(sourcePath);
        var destination = Path.Combine(_rejectedRoot, fileName);

        var diagnosticFileName = fileName.Length <= 255 ? fileName : fileName[..255];
        var payload = JsonSerializer.Serialize(new
        {
            code = "telemetry_envelope_invalid",
            fileName = diagnosticFileName,
        });
        await _writer.RecordRejectedAsync(payload, cancellationToken);
        File.Move(sourcePath, destination, overwrite: true);
        return new TelemetryImportResult(
            EventId: null,
            RequestId: null,
            WasAlreadyImported: false,
            WasRejected: true);
    }
}
