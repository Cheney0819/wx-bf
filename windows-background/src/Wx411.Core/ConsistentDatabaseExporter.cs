using System.Security.Cryptography;

namespace Wx411.Core;

public enum DatabaseExportStatus
{
    Completed,
    GenerationChanged,
    AuthenticationFailed,
    InsufficientSpace,
    OutputFailed,
    Cancelled,
}

public sealed record DatabaseExportRequest(
    string DatabasePath,
    DatabaseFileGeneration Generation,
    byte[] RawKey,
    CipherProfile Profile,
    string OutputDirectory);

public sealed record DatabaseExportResult(
    DatabaseExportStatus Status,
    string? OutputPath,
    string? Error,
    bool WalApplied = false);

public sealed class ConsistentDatabaseExporter
{
    private const int StreamBufferSize = 1024 * 1024;
    private readonly Action<string, CancellationToken> _integrityChecker;

    public ConsistentDatabaseExporter()
        : this(SqliteIntegrityChecker.VerifyFile)
    {
    }

    internal ConsistentDatabaseExporter(Action<string, CancellationToken> integrityChecker)
    {
        ArgumentNullException.ThrowIfNull(integrityChecker);
        _integrityChecker = integrityChecker;
    }

    public Task<DatabaseExportResult> ExportAsync(
        DatabaseExportRequest request,
        IProgress<RecoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentNullException.ThrowIfNull(request.RawKey);
        string? encryptedTemporaryPath = null;
        string? outputTemporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DatabaseProbeDescriptor.GetGeneration(request.DatabasePath) != request.Generation)
                return Task.FromResult(new DatabaseExportResult(
                    DatabaseExportStatus.GenerationChanged, null, "Database generation changed before export."));
            if (request.RawKey.Length != 32)
                return Task.FromResult(new DatabaseExportResult(
                    DatabaseExportStatus.AuthenticationFailed, null, "Raw key must be 32 bytes."));

            Directory.CreateDirectory(request.OutputDirectory);
            if (!HasAvailableSpace(request.DatabasePath, request.OutputDirectory))
                return Task.FromResult(new DatabaseExportResult(
                    DatabaseExportStatus.InsufficientSpace, null, "Not enough free space for encrypted and plaintext temporary files."));

            progress?.Report(new RecoveryProgress(68,
                $"正在构造 {Path.GetFileName(request.DatabasePath)} 的 WAL 一致快照…", null));
            var snapshot = StreamingWalSnapshot.Build(
                request.DatabasePath,
                request.OutputDirectory,
                request.Generation,
                cancellationToken);
            encryptedTemporaryPath = snapshot.Path;
            if (snapshot.Length < request.Profile.PageSize ||
                snapshot.Length % request.Profile.PageSize != 0)
            {
                return Task.FromResult(new DatabaseExportResult(
                    DatabaseExportStatus.AuthenticationFailed, null, "Encrypted snapshot length is not page aligned.", snapshot.WalApplied));
            }

            var outputPath = ResolveOutputPath(request.DatabasePath, request.OutputDirectory);
            outputTemporaryPath = Path.Combine(
                request.OutputDirectory,
                $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
            StreamDecrypt(
                encryptedTemporaryPath,
                outputTemporaryPath,
                request.RawKey,
                request.Profile,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _integrityChecker(outputTemporaryPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SqliteSidecarCleaner.DeleteForTemporaryDatabase(outputTemporaryPath);
            File.Move(outputTemporaryPath, outputPath);
            outputTemporaryPath = null;
            return Task.FromResult(new DatabaseExportResult(
                DatabaseExportStatus.Completed, outputPath, null, snapshot.WalApplied));
        }
        catch (DatabaseGenerationChangedException ex)
        {
            return Task.FromResult(new DatabaseExportResult(DatabaseExportStatus.GenerationChanged, null, ex.Message));
        }
        catch (PageAuthenticationException ex)
        {
            return Task.FromResult(new DatabaseExportResult(DatabaseExportStatus.AuthenticationFailed, null, ex.Message));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new DatabaseExportResult(DatabaseExportStatus.Cancelled, null, "Export cancelled."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   IntegrityException or CryptographicException or ArgumentException)
        {
            return Task.FromResult(new DatabaseExportResult(DatabaseExportStatus.OutputFailed, null, ex.Message));
        }
        finally
        {
            if (outputTemporaryPath is not null)
            {
                StreamingWalSnapshot.TryDelete(outputTemporaryPath);
                SqliteSidecarCleaner.DeleteForTemporaryDatabase(outputTemporaryPath);
            }
            if (encryptedTemporaryPath is not null)
                StreamingWalSnapshot.TryDelete(encryptedTemporaryPath);
            CryptographicOperations.ZeroMemory(request.RawKey);
        }
    }

    private static void StreamDecrypt(
        string encryptedPath,
        string plaintextPath,
        byte[] rawKey,
        CipherProfile profile,
        CancellationToken cancellationToken)
    {
        var encryptedPage = new byte[profile.PageSize];
        var plaintextPage = new byte[profile.PageSize];
        byte[]? salt = null;
        byte[]? macKey = null;
        try
        {
            using var encrypted = new FileStream(
                encryptedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.SequentialScan);
            using var plaintext = new FileStream(
                plaintextPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                StreamBufferSize,
                FileOptions.SequentialScan);
            ReadExactly(encrypted, encryptedPage, cancellationToken);
            salt = encryptedPage.AsSpan(0, 16).ToArray();
            macKey = SqlCipher4.MakeMacKey(rawKey, salt, profile);
            encrypted.Position = 0;
            var pageCount = checked((int)(encrypted.Length / profile.PageSize));
            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadExactly(encrypted, encryptedPage, cancellationToken);
                var zeroPage = pageNumber > 1 && StreamingSqlCipherDecryptor.IsZeroPage(encryptedPage);
                if (!zeroPage && !SqlCipher4.VerifyEncryptedPageWithMacKey(
                        encryptedPage, macKey, pageNumber, profile))
                {
                    throw new PageAuthenticationException(new PageAuthenticationReport(
                        pageCount, 1, [pageNumber]));
                }
                StreamingSqlCipherDecryptor.DecryptPage(
                    encryptedPage, plaintextPage, rawKey, pageNumber, profile);
                plaintext.Write(plaintextPage, 0, plaintextPage.Length);
                CryptographicOperations.ZeroMemory(encryptedPage);
                CryptographicOperations.ZeroMemory(plaintextPage);
            }
            plaintext.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedPage);
            CryptographicOperations.ZeroMemory(plaintextPage);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            if (macKey is not null) CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) throw new EndOfStreamException("Encrypted snapshot ended mid-page.");
            offset += read;
        }
    }

    private static bool HasAvailableSpace(string databasePath, string outputDirectory)
    {
        var length = new FileInfo(databasePath).Length;
        var root = Path.GetPathRoot(Path.GetFullPath(outputDirectory));
        if (string.IsNullOrWhiteSpace(root)) return true;
        try
        {
            var required = checked(length * 2 + 64L * 1024 * 1024);
            return new DriveInfo(root).AvailableFreeSpace >= required;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static string ResolveOutputPath(string inputPath, string outputDirectory)
    {
        var inputName = Path.GetFileNameWithoutExtension(inputPath);
        if (string.IsNullOrWhiteSpace(inputName)) inputName = "wechat_database";
        var outputPath = Path.Combine(outputDirectory, inputName + ".readable.sqlite");
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("输出路径不可覆盖源数据库。");
        if (!File.Exists(outputPath)) return outputPath;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        outputPath = Path.Combine(outputDirectory, $"{inputName}.readable.{stamp}.sqlite");
        var suffix = 2;
        while (File.Exists(outputPath))
            outputPath = Path.Combine(outputDirectory, $"{inputName}.readable.{stamp}-{suffix++}.sqlite");
        return outputPath;
    }
}
