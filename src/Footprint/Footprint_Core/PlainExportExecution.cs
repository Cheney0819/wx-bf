namespace Footprint.Core;

internal sealed record PlainExportExecution(
    PlainDatabaseBaseline SourceBaseline,
    string PlainJournalMode,
    IReadOnlyList<PlainSchemaObject>? KnownTargetSchema)
{
    public Task<PlainDatabaseBaseline> ReadTargetAsync(IPlainProcessRunner runner, string executable,
        string targetPath, CancellationToken cancellationToken) => KnownTargetSchema is null
        ? PlainDbValidator.ReadAsync(runner, executable, targetPath, null, 0, 0, cancellationToken)
        : PlainDbValidator.ReadAsync(runner, executable, targetPath, null, 0, 0, KnownTargetSchema,
            cancellationToken);
}

internal static class PlainExportExecutor
{
    public static Task<PlainExportExecution> ExecuteAsync(IPlainProcessRunner runner, string executable,
        PlainExportCandidate candidate, byte[] key, PlainDatabaseBaseline sourceBaseline, string countScriptPath,
        CancellationToken cancellationToken) => PlainSql.RequiresDumpRestore(sourceBaseline.Schema)
        ? ExportViaDumpRestoreAsync(runner, executable, candidate, key, sourceBaseline, cancellationToken)
        : ExportViaSqlCipherAsync(runner, executable, candidate, key, countScriptPath, cancellationToken);

    private static async Task<PlainExportExecution> ExportViaSqlCipherAsync(IPlainProcessRunner runner,
        string executable, PlainExportCandidate candidate, byte[] key, string countScriptPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(countScriptPath)) File.Delete(countScriptPath);
        using var result = await RunAsync(runner, executable,
            ["-batch", PlainSql.ReadOnlyUri(candidate.SourcePath)],
            PlainSql.BuildSourceExportScriptBytes(key, candidate.Database.Compatibility, candidate.Database.PageSize,
                candidate.TemporaryPath, countScriptPath), TimeSpan.FromMinutes(10), cancellationToken, key);
        if (File.Exists(countScriptPath)) File.Delete(countScriptPath);
        EnsureSucceeded(result, key);
        var baseline = ParseOutput(result.StandardOutput, key, requireMetadata: true,
            requireRowCounts: true);
        return new PlainExportExecution(baseline, baseline.PlainJournalMode, null);
    }

    private static async Task<PlainExportExecution> ExportViaDumpRestoreAsync(IPlainProcessRunner runner,
        string executable, PlainExportCandidate candidate, byte[] key, PlainDatabaseBaseline sourceBaseline,
        CancellationToken cancellationToken)
    {
        using var result = await RunPipelineAsync(runner, executable,
            ["-batch", PlainSql.ReadOnlyUri(candidate.SourcePath)],
            PlainSql.BuildSourceDumpScriptBytes(key, candidate.Database.Compatibility, candidate.Database.PageSize),
            executable, ["-batch", candidate.TemporaryPath], PlainSql.BuildDumpRestorePrefixBytes(),
            PlainSql.BuildDumpRestoreSuffixBytes(sourceBaseline.UserVersion), TimeSpan.FromMinutes(10), cancellationToken,
            key);
        EnsureSucceeded(result, key);
        var journalMode = ParseOutput(result.StandardOutput, key).PlainJournalMode;
        return new PlainExportExecution(sourceBaseline, journalMode, sourceBaseline.Schema);
    }

    private static void EnsureSucceeded(SecureProcessResult result, byte[] key)
    {
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidDataException("SQLCipher export failed: " + Redactor.Redact(result.StandardError, key));
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

    private static async Task<SecureProcessResult> RunPipelineAsync(IPlainProcessRunner runner,
        string producerExecutable, IReadOnlyList<string> producerArguments, byte[] producerScript,
        string consumerExecutable, IReadOnlyList<string> consumerArguments, byte[] consumerPrefix,
        byte[] consumerSuffix, TimeSpan timeout, CancellationToken cancellationToken,
        ReadOnlyMemory<byte> outputRedactionKey)
    {
        try
        {
            return await runner.RunPipelineBytesAsync(producerExecutable, producerArguments, producerScript,
                consumerExecutable, consumerArguments, consumerPrefix, consumerSuffix, timeout, cancellationToken,
                outputRedactionKey);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(producerScript);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(consumerPrefix);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(consumerSuffix);
        }
    }

    private static PlainDatabaseBaseline ParseOutput(ReadOnlyMemory<byte> output, byte[] key,
        bool requireMetadata = false, bool requireRowCounts = false)
    {
        var redacted = ByteKeyRedactor.Redact(output.Span, key);
        try { return PlainExportOutputParser.Parse(redacted, requireMetadata, requireRowCounts); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(redacted); }
    }
}
