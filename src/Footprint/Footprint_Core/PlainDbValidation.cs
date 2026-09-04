using System.Security.Cryptography;
using System.Text;

namespace Footprint.Core;

public sealed record PlainValidationComparison(
    bool Passed,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> SchemaMismatches,
    IReadOnlyList<string> RowCountMismatches);

public static class PlainDbValidator
{
    private static readonly byte[] Header = "SQLite format 3\0"u8.ToArray();

    public static PlainValidationComparison Compare(PlainDatabaseBaseline source, PlainDatabaseBaseline target)
    {
        var errors = new List<string>();
        var schemaMismatches = new List<string>();
        var rowMismatches = new List<string>();
        if (!string.Equals(target.IntegrityCheck, "ok", StringComparison.OrdinalIgnoreCase))
            errors.Add($"integrity_check={target.IntegrityCheck}");
        if (source.UserVersion != target.UserVersion)
            errors.Add($"user_version differs: source={source.UserVersion}, target={target.UserVersion}");

        var sourceSchema = source.Schema.ToDictionary(Key, StringComparer.Ordinal);
        var targetSchema = target.Schema.ToDictionary(Key, StringComparer.Ordinal);
        foreach (var key in sourceSchema.Keys.Union(targetSchema.Keys, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!sourceSchema.TryGetValue(key, out var sourceItem)) schemaMismatches.Add($"target-only:{key}");
            else if (!targetSchema.TryGetValue(key, out var targetItem)) schemaMismatches.Add($"source-only:{key}");
            else if (!string.Equals(PlainSql.NormalizeSchemaSql(sourceItem.Sql), PlainSql.NormalizeSchemaSql(targetItem.Sql),
                         StringComparison.Ordinal)) schemaMismatches.Add($"sql:{key}");
        }

        foreach (var table in source.RowCounts.Keys.Union(target.RowCounts.Keys, StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            source.RowCounts.TryGetValue(table, out var sourceCount);
            target.RowCounts.TryGetValue(table, out var targetCount);
            if (!source.RowCounts.ContainsKey(table) || !target.RowCounts.ContainsKey(table) || sourceCount != targetCount)
                rowMismatches.Add($"{table}:source={sourceCount},target={targetCount}");
        }
        foreach (var table in source.SequenceValues.Keys.Union(target.SequenceValues.Keys, StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            source.SequenceValues.TryGetValue(table, out var sourceSequence);
            target.SequenceValues.TryGetValue(table, out var targetSequence);
            if (!source.SequenceValues.ContainsKey(table) || !target.SequenceValues.ContainsKey(table) ||
                sourceSequence != targetSequence)
                rowMismatches.Add($"sqlite_sequence:{table}:source={sourceSequence},target={targetSequence}");
        }
        if (schemaMismatches.Count > 0) errors.Add("schema mismatch");
        if (rowMismatches.Count > 0) errors.Add("row-count mismatch");
        return new PlainValidationComparison(errors.Count == 0, errors, schemaMismatches, rowMismatches);

        static string Key(PlainSchemaObject item) => $"{item.Type}\0{item.Name}\0{item.TableName}";
    }

    public static async Task<PlainDatabaseBaseline> ReadPlaintextAsync(string executable, string databasePath,
        CancellationToken cancellationToken, IPlainProcessRunner? runner = null)
    {
        await EnsureHeaderAsync(databasePath, cancellationToken);
        runner ??= SystemPlainProcessRunner.Instance;
        return await ReadAsync(runner, executable, databasePath, null, 0, 0, cancellationToken);
    }

    internal static async Task<PlainDatabaseBaseline> ReadAsync(IPlainProcessRunner runner, string executable,
        string databasePath, byte[]? key, int compatibility, int pageSize, CancellationToken cancellationToken)
    {
        using var metadataResult = await RunAsync(runner, executable,
            ["-batch", PlainSql.ReadOnlyUri(databasePath)], PlainSql.BuildMetadataScriptBytes(key, compatibility, pageSize),
            TimeSpan.FromMinutes(2), cancellationToken, key ?? []);
        if (metadataResult.ExitCode != 0 || metadataResult.TimedOut)
            throw new InvalidDataException("Database metadata read failed: " + Redactor.Redact(metadataResult.StandardError, key ?? []));
        var metadata = ParseOutput(metadataResult.StandardOutput, key, requireMetadata: true);
        if (!string.Equals(metadata.IntegrityCheck, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Database integrity check failed.");
        using var countResult = await RunAsync(runner, executable,
            ["-batch", PlainSql.ReadOnlyUri(databasePath)],
            PlainSql.BuildCountAndProbeScriptBytes(key, compatibility, pageSize, metadata.Schema),
            TimeSpan.FromMinutes(5), cancellationToken, key ?? []);
        if (countResult.ExitCode != 0 || countResult.TimedOut)
            throw new InvalidDataException("Database row/view validation failed: " +
                                           Redactor.Redact(countResult.StandardError, key ?? []));
        var expectedCounts = metadata.Schema.Count(item => item.Type == "table" && !PlainSql.IsVirtualTable(item));
        var counts = ParseOutput(countResult.StandardOutput, key, requireRowCounts: expectedCounts > 0);
        if (counts.RowCounts.Count != expectedCounts)
            throw new InvalidDataException("Database row-count output does not cover every table.");
        return PlainExportOutputParser.Merge(metadata, counts);
    }

    internal static async Task<PlainDatabaseBaseline> ReadAsync(IPlainProcessRunner runner, string executable,
        string databasePath, byte[]? key, int compatibility, int pageSize, IEnumerable<PlainSchemaObject> knownSchema,
        CancellationToken cancellationToken)
    {
        var schema = knownSchema.ToArray();
        using var metadataResult = await RunAsync(runner, executable,
            ["-batch", PlainSql.ReadOnlyUri(databasePath)],
            PlainSql.BuildMetadataScriptBytes(key, compatibility, pageSize, schema), TimeSpan.FromMinutes(2),
            cancellationToken, key ?? []);
        if (metadataResult.ExitCode != 0 || metadataResult.TimedOut)
            throw new InvalidDataException("Database metadata read failed: " +
                                           Redactor.Redact(metadataResult.StandardError, key ?? []));
        var metadata = ParseOutput(metadataResult.StandardOutput, key, requireMetadata: true);
        if (!string.Equals(metadata.IntegrityCheck, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Database quick check failed: " + metadata.IntegrityCheck);
        using var countResult = await RunAsync(runner, executable,
            ["-batch", PlainSql.ReadOnlyUri(databasePath)],
            PlainSql.BuildCountAndProbeScriptBytes(key, compatibility, pageSize, schema), TimeSpan.FromMinutes(5),
            cancellationToken, key ?? []);
        if (countResult.ExitCode != 0 || countResult.TimedOut)
            throw new InvalidDataException("Database row/view validation failed: " +
                                           Redactor.Redact(countResult.StandardError, key ?? []));
        var expectedCounts = schema.Count(item => item.Type == "table" && !PlainSql.IsVirtualTable(item));
        var counts = ParseOutput(countResult.StandardOutput, key, requireRowCounts: expectedCounts > 0);
        if (counts.RowCounts.Count != expectedCounts)
            throw new InvalidDataException("Database row-count output does not cover every non-virtual table.");
        return new PlainDatabaseBaseline
        {
            UserVersion = metadata.UserVersion,
            Schema = metadata.Schema,
            RowCounts = counts.RowCounts,
            SequenceValues = counts.SequenceValues,
            IntegrityCheck = metadata.IntegrityCheck
        };
    }

    public static async Task EnsureHeaderAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[Header.Length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(buffer, cancellationToken);
        if (read != Header.Length || !buffer.AsSpan().SequenceEqual(Header))
            throw new InvalidDataException("The exported file is not a standard SQLite database.");
    }

    private static async Task<SecureProcessResult> RunAsync(IPlainProcessRunner runner, string executable,
        IReadOnlyList<string> arguments, byte[] script, TimeSpan timeout, CancellationToken cancellationToken,
        ReadOnlyMemory<byte> outputRedactionKey)
    {
        try
        {
            return await runner.RunBytesAsync(executable, arguments, script, timeout, cancellationToken,
                outputRedactionKey);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(script); }
    }

    private static PlainDatabaseBaseline ParseOutput(ReadOnlyMemory<byte> output, byte[]? key,
        bool requireMetadata = false, bool requireRowCounts = false)
    {
        var redacted = ByteKeyRedactor.Redact(output.Span, key ?? []);
        try { return PlainExportOutputParser.Parse(redacted, requireMetadata, requireRowCounts); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(redacted); }
    }
}

public interface IPlainProcessRunner
{
    // Legacy string contract. Its default adapter keeps temporary UTF-8 buffers zeroable.
    async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string standardInput,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        var input = Encoding.UTF8.GetBytes(standardInput);
        try
        {
            using var result = await RunBytesAsync(fileName, arguments, input, timeout, cancellationToken);
            return new ProcessResult(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput.Span),
                Encoding.UTF8.GetString(result.StandardError.Span), result.TimedOut, result.StreamDrainTimedOut);
        }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    async Task<ProcessResult> RunPipelineAsync(string producerFileName, IReadOnlyList<string> producerArguments,
        string producerStandardInput, string consumerFileName, IReadOnlyList<string> consumerArguments,
        string consumerStandardInputPrefix, string consumerStandardInputSuffix, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(producerStandardInput);
        ArgumentNullException.ThrowIfNull(consumerStandardInputPrefix);
        ArgumentNullException.ThrowIfNull(consumerStandardInputSuffix);
        var producerInput = Encoding.UTF8.GetBytes(producerStandardInput);
        var consumerPrefix = Encoding.UTF8.GetBytes(consumerStandardInputPrefix);
        var consumerSuffix = Encoding.UTF8.GetBytes(consumerStandardInputSuffix);
        try
        {
            using var result = await RunPipelineBytesAsync(producerFileName, producerArguments, producerInput,
                consumerFileName, consumerArguments, consumerPrefix, consumerSuffix, timeout, cancellationToken);
            return new ProcessResult(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput.Span),
                Encoding.UTF8.GetString(result.StandardError.Span), result.TimedOut, result.StreamDrainTimedOut);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(producerInput);
            CryptographicOperations.ZeroMemory(consumerPrefix);
            CryptographicOperations.ZeroMemory(consumerSuffix);
        }
    }

    Task<SecureProcessResult> RunBytesAsync(string fileName, IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte> standardInput, TimeSpan timeout, CancellationToken cancellationToken,
        ReadOnlyMemory<byte> outputRedactionKey = default) =>
        throw new NotSupportedException("Binary process execution is unavailable.");

    Task<SecureProcessResult> RunPipelineBytesAsync(string producerFileName, IReadOnlyList<string> producerArguments,
        ReadOnlyMemory<byte> producerStandardInput, string consumerFileName,
        IReadOnlyList<string> consumerArguments, ReadOnlyMemory<byte> consumerStandardInputPrefix,
        ReadOnlyMemory<byte> consumerStandardInputSuffix, TimeSpan timeout,
        CancellationToken cancellationToken, ReadOnlyMemory<byte> outputRedactionKey = default) =>
        throw new NotSupportedException("Binary process pipelines are unavailable.");
}

public sealed class SystemPlainProcessRunner : IPlainProcessRunner
{
    public static SystemPlainProcessRunner Instance { get; } = new();
    private SystemPlainProcessRunner() { }

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string standardInput,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        var input = Encoding.UTF8.GetBytes(standardInput);
        try
        {
            using var result = await RunBytesAsync(fileName, arguments, input, timeout, cancellationToken);
            return new ProcessResult(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput.Span),
                Encoding.UTF8.GetString(result.StandardError.Span), result.TimedOut, result.StreamDrainTimedOut);
        }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    public async Task<ProcessResult> RunPipelineAsync(string producerFileName, IReadOnlyList<string> producerArguments,
        string producerStandardInput, string consumerFileName, IReadOnlyList<string> consumerArguments,
        string consumerStandardInputPrefix, string consumerStandardInputSuffix, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(producerStandardInput);
        ArgumentNullException.ThrowIfNull(consumerStandardInputPrefix);
        ArgumentNullException.ThrowIfNull(consumerStandardInputSuffix);
        var producerInput = Encoding.UTF8.GetBytes(producerStandardInput);
        var consumerPrefix = Encoding.UTF8.GetBytes(consumerStandardInputPrefix);
        var consumerSuffix = Encoding.UTF8.GetBytes(consumerStandardInputSuffix);
        try
        {
            using var result = await RunPipelineBytesAsync(producerFileName, producerArguments, producerInput,
                consumerFileName, consumerArguments, consumerPrefix, consumerSuffix, timeout, cancellationToken);
            return new ProcessResult(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput.Span),
                Encoding.UTF8.GetString(result.StandardError.Span), result.TimedOut, result.StreamDrainTimedOut);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(producerInput);
            CryptographicOperations.ZeroMemory(consumerPrefix);
            CryptographicOperations.ZeroMemory(consumerSuffix);
        }
    }

    public Task<SecureProcessResult> RunBytesAsync(string fileName, IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte> standardInput, TimeSpan timeout, CancellationToken cancellationToken,
        ReadOnlyMemory<byte> outputRedactionKey = default) =>
        ProcessRunner.RunBytesAsync(fileName, arguments, standardInput, timeout, cancellationToken,
            outputRedactionKey: outputRedactionKey);

    public Task<SecureProcessResult> RunPipelineBytesAsync(string producerFileName,
        IReadOnlyList<string> producerArguments, ReadOnlyMemory<byte> producerStandardInput,
        string consumerFileName, IReadOnlyList<string> consumerArguments, ReadOnlyMemory<byte> consumerStandardInputPrefix,
        ReadOnlyMemory<byte> consumerStandardInputSuffix, TimeSpan timeout, CancellationToken cancellationToken,
        ReadOnlyMemory<byte> outputRedactionKey = default) =>
        ProcessRunner.RunPipelineBytesAsync(producerFileName, producerArguments, producerStandardInput,
            consumerFileName, consumerArguments, consumerStandardInputPrefix, consumerStandardInputSuffix,
            timeout, cancellationToken, outputRedactionKey);
}

public interface IPlainKeyLoader
{
    byte[] Load(string path);
}

public sealed class ProtectedPlainKeyLoader : IPlainKeyLoader
{
    public byte[] Load(string path) => ProtectedKeyStore.UnprotectFromFile(path);
}
