using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Wx411.Export;

public sealed class SemanticExportService
{
    private const string MissingSource = "<missing>";
    private const string MessageFile = "message_0.readable.sqlite";
    private const string BusinessMessageFile = "biz_message_0.readable.sqlite";
    private const string ContactFile = "contact.readable.sqlite";
    private const string SessionFile = "session.readable.sqlite";

    public async Task<SemanticExportResult> ExportAsync(
        SemanticExportRequest request,
        IProgress<SemanticExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inputDirectory = Path.GetFullPath(request.InputDirectory);
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException($"Input directory was not found: {inputDirectory}");

        var messagePath = Path.Combine(inputDirectory, MessageFile);
        var businessMessagePath = Path.Combine(inputDirectory, BusinessMessageFile);
        var contactPath = Path.Combine(inputDirectory, ContactFile);
        var sessionPath = Path.Combine(inputDirectory, SessionFile);
        if (!File.Exists(messagePath))
            throw new FileNotFoundException("Required recovered message database was not found.", messagePath);

        var outputPath = Path.GetFullPath(request.OutputPath);
        var summaryPath = Path.GetFullPath(request.SummaryPath ?? outputPath + ".summary.json");
        if (string.Equals(outputPath, summaryPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Summary path must differ from the SQLite output path.", nameof(request));
        ValidateOutputPath(outputPath, messagePath, businessMessagePath, contactPath, sessionPath);
        ValidateOutputPath(summaryPath, messagePath, businessMessagePath, contactPath, sessionPath);
        if (!request.Overwrite && (File.Exists(outputPath) || File.Exists(summaryPath)))
            throw new IOException("Output already exists. Set Overwrite to replace it.");

        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var summaryDirectory = Path.GetDirectoryName(summaryPath)!;
        Directory.CreateDirectory(summaryDirectory);
        var token = Guid.NewGuid().ToString("N");
        var temporaryDatabase = Path.Combine(outputDirectory, $".wx411-export.{token}.tmp.sqlite");
        var temporarySummary = Path.Combine(summaryDirectory, $".wx411-export.{token}.tmp.json");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePaths = new[] { messagePath, businessMessagePath, contactPath, sessionPath };
            var sourceGenerationPaths = sourcePaths
                .SelectMany(path => new[] { path, path + "-wal" })
                .ToArray();
            var sourceGeneration = ComputeGenerationHashes(sourceGenerationPaths);
            var hasBusinessMessages = IsSourcePresent(sourceGeneration, businessMessagePath);
            var hasContacts = IsSourcePresent(sourceGeneration, contactPath);
            var hasSessions = IsSourcePresent(sourceGeneration, sessionPath);
            var sourceHashes = sourceGeneration
                .Where(pair => pair.Value != MissingSource)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            progress?.Report(new SemanticExportProgress(10, "读取消息数据库", MessageFile));
            var messageSnapshots = new List<MessageDatabaseSnapshot>
            {
                await MessageDatabaseReader.ReadAsync(messagePath, cancellationToken).ConfigureAwait(false),
            };
            if (hasBusinessMessages)
            {
                progress?.Report(new SemanticExportProgress(25, "读取业务消息数据库", BusinessMessageFile));
                messageSnapshots.Add(await MessageDatabaseReader.ReadAsync(businessMessagePath, cancellationToken)
                    .ConfigureAwait(false));
            }
            var messages = MergeMessages(messageSnapshots);
            progress?.Report(new SemanticExportProgress(35, "读取联系人数据库", hasContacts ? ContactFile : "未提供"));
            var contacts = hasContacts
                ? await ContactDatabaseReader.ReadOptionalAsync(contactPath, cancellationToken).ConfigureAwait(false)
                : ContactDatabaseSnapshot.Empty;
            contacts = MergeIdentities(contacts, messages.Names);
            progress?.Report(new SemanticExportProgress(50, "读取会话数据库", hasSessions ? SessionFile : "未提供"));
            var sessions = hasSessions
                ? await SessionDatabaseReader.ReadOptionalAsync(sessionPath, cancellationToken).ConfigureAwait(false)
                : SessionDatabaseSnapshot.Empty;

            progress?.Report(new SemanticExportProgress(65, "写入标准化数据库"));
            await NormalizedDatabaseWriter.WriteAsync(
                temporaryDatabase,
                messages,
                contacts,
                sessions,
                cancellationToken).ConfigureAwait(false);
            await VerifyIntegrityAsync(temporaryDatabase, cancellationToken).ConfigureAwait(false);

            var finalSourceGeneration = ComputeGenerationHashes(sourceGenerationPaths);
            if (!sourceGeneration.OrderBy(pair => pair.Key).SequenceEqual(
                    finalSourceGeneration.OrderBy(pair => pair.Key)))
                throw new IOException("A source database changed during export.");

            var warnings = new List<string>();
            if (!hasContacts) warnings.Add($"Optional source missing: {ContactFile}");
            if (!hasSessions) warnings.Add($"Optional source missing: {SessionFile}");
            if (contacts.UnresolvedMemberEdges > 0)
                warnings.Add($"Skipped unresolved chat-room membership edges: {contacts.UnresolvedMemberEdges}");

            var outputSha256 = ComputeSha256(temporaryDatabase);
            var result = new SemanticExportResult(
                outputPath,
                summaryPath,
                outputSha256,
                messages.Messages.Count,
                messages.Conversations.Count,
                contacts.Identities.Count,
                contacts.ChatRooms.Count,
                contacts.Members.Count,
                contacts.UnresolvedMemberEdges,
                sourceHashes,
                warnings.AsReadOnly());

            progress?.Report(new SemanticExportProgress(90, "写入摘要"));
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            });
            await File.WriteAllTextAsync(temporarySummary, json + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
            PublishPair(
                temporaryDatabase,
                outputPath,
                temporarySummary,
                summaryPath,
                request.Overwrite,
                token);
            progress?.Report(new SemanticExportProgress(100, "完成", outputPath));
            return result;
        }
        finally
        {
            DeleteIfExists(temporaryDatabase);
            DeleteIfExists(temporaryDatabase + "-journal");
            DeleteIfExists(temporaryDatabase + "-wal");
            DeleteIfExists(temporaryDatabase + "-shm");
            DeleteIfExists(temporarySummary);
        }
    }

    private static void ValidateOutputPath(string outputPath, params string[] sourcePaths)
    {
        if (sourcePaths.Any(path => string.Equals(outputPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Output path must not replace a source database.", nameof(outputPath));
    }

    private static MessageDatabaseSnapshot MergeMessages(IEnumerable<MessageDatabaseSnapshot> snapshots)
    {
        var values = snapshots.ToArray();
        return new MessageDatabaseSnapshot(
            values.SelectMany(value => value.Conversations).ToArray(),
            values.SelectMany(value => value.Messages).ToArray(),
            values.SelectMany(value => value.Names).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static ContactDatabaseSnapshot MergeIdentities(
        ContactDatabaseSnapshot contacts,
        IEnumerable<string> messageNames)
    {
        var identities = new Dictionary<string, SourceIdentity>(StringComparer.Ordinal);
        foreach (var identity in contacts.Identities)
            identities.TryAdd(identity.Username, identity);
        foreach (var username in messageNames)
            identities.TryAdd(username, new SourceIdentity(
                0,
                username,
                username,
                "message_name",
                null,
                null,
                null,
                null,
                null,
                null));

        return new ContactDatabaseSnapshot(
            identities.Values.ToArray(),
            contacts.ChatRooms,
            contacts.Members,
            contacts.UnresolvedMemberEdges);
    }

    private static Dictionary<string, string> ComputeGenerationHashes(IEnumerable<string> paths) =>
        paths.ToDictionary(
            path => Path.GetFileName(path),
            path => File.Exists(path) ? ComputeSha256(path) : MissingSource,
            StringComparer.Ordinal);

    private static bool IsSourcePresent(IReadOnlyDictionary<string, string> generation, string path) =>
        generation[Path.GetFileName(path)] != MissingSource;

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task VerifyIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Export integrity check failed: {result ?? "no result"}");
    }

    private static void PublishPair(
        string temporaryDatabase,
        string outputPath,
        string temporarySummary,
        string summaryPath,
        bool overwrite,
        string token)
    {
        var databaseBackup = outputPath + $".wx411-backup.{token}";
        var summaryBackup = summaryPath + $".wx411-backup.{token}";
        var databaseBackedUp = false;
        var summaryBackedUp = false;
        var databasePublished = false;
        var summaryPublished = false;
        var publicationCompleted = false;
        var rollbackCompleted = false;

        try
        {
            if (overwrite && File.Exists(outputPath))
            {
                File.Move(outputPath, databaseBackup);
                databaseBackedUp = true;
            }
            if (overwrite && File.Exists(summaryPath))
            {
                File.Move(summaryPath, summaryBackup);
                summaryBackedUp = true;
            }

            File.Move(temporaryDatabase, outputPath);
            databasePublished = true;
            File.Move(temporarySummary, summaryPath);
            summaryPublished = true;
            publicationCompleted = true;
        }
        catch (Exception publishError)
        {
            try
            {
                if (summaryPublished) File.Delete(summaryPath);
                if (databasePublished) File.Delete(outputPath);
                if (summaryBackedUp) File.Move(summaryBackup, summaryPath);
                if (databaseBackedUp) File.Move(databaseBackup, outputPath);
                rollbackCompleted = true;
            }
            catch (Exception rollbackError)
            {
                var retainedBackups = new[] { databaseBackup, summaryBackup }
                    .Where(File.Exists)
                    .ToArray();
                var retainedText = retainedBackups.Length == 0
                    ? "No backup file remains."
                    : $"Retained backup paths: {string.Join(", ", retainedBackups)}";
                throw new IOException(
                    $"Export publication failed and the previous output could not be fully restored. {retainedText}",
                    new AggregateException(publishError, rollbackError));
            }
            throw;
        }
        finally
        {
            if (publicationCompleted || rollbackCompleted)
            {
                DeleteIfExists(databaseBackup);
                DeleteIfExists(summaryBackup);
            }
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
