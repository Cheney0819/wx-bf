using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Background.Infrastructure;
using DesktopPet.DataSync.Persistence;

namespace DesktopPet.DataSync;

public sealed class ParserJobBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string _jobsRoot;

    public ParserJobBuilder(string jobsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobsRoot);
        _jobsRoot = Path.GetFullPath(jobsRoot);
    }

    public async Task<BuiltParserJob> BuildAsync(
        ParseJob job,
        IReadOnlyList<ParseJobInput> inputs,
        int maximumMessages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(inputs);
        if (job.State != ParseJobState.Leased || string.IsNullOrWhiteSpace(job.LeaseOwner))
            throw new InvalidOperationException("Only a claimed parse job can be materialized.");
        if (inputs.Count == 0 || inputs.Count > 256)
            throw new ArgumentOutOfRangeException(nameof(inputs));
        if (maximumMessages is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(maximumMessages));
        ValidateSegment(job.Id, nameof(job));

        Directory.CreateDirectory(_jobsRoot);
        var finalJobRoot = Path.Combine(_jobsRoot, job.Id);
        if (Directory.Exists(finalJobRoot) || File.Exists(finalJobRoot))
            throw new IOException("Parser job directory already exists.");
        var temporaryRoot = Path.Combine(
            _jobsRoot,
            $".{job.Id}.{Guid.NewGuid():N}.tmp");
        var finalInputRoot = Path.Combine(finalJobRoot, "input");
        var finalOutputRoot = Path.Combine(finalJobRoot, "output");
        try
        {
            var temporaryInputRoot = Path.Combine(temporaryRoot, "input");
            Directory.CreateDirectory(temporaryInputRoot);
            Directory.CreateDirectory(Path.Combine(temporaryRoot, "output"));
            var databases = new List<ParserDatabaseInput>(inputs.Count);
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in inputs.OrderBy(item => item.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(input.JobId, job.Id, StringComparison.Ordinal))
                    throw new InvalidDataException("Parse job input belongs to another job.");
                ValidateSha256(input.GenerationId, nameof(inputs));
                ValidateSha256(input.Sha256, nameof(inputs));
                var relativePath = NormalizeRelativePath(input.RelativePath);
                ValidateRelativePath(relativePath);
                if (!seenPaths.Add(relativePath))
                    throw new InvalidDataException("Parse job has duplicate relative paths.");
                var source = Path.GetFullPath(input.PlaintextPath);
                if (!File.Exists(source))
                    throw new FileNotFoundException("Catalog generation is missing.", source);
                var sourceHash = await FileSha256Async(source, cancellationToken);
                if (!string.Equals(sourceHash, input.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("Catalog generation hash drifted before parse.");

                var relativeParts = relativePath.Split('/');
                var temporaryDestination = Path.Combine(
                    [temporaryInputRoot, .. relativeParts]);
                var destinationDirectory = Path.GetDirectoryName(temporaryDestination) ??
                    throw new InvalidDataException("Parse job input has no destination directory.");
                Directory.CreateDirectory(destinationDirectory);
                await MaterializeAsync(
                    source,
                    temporaryDestination,
                    input.Sha256,
                    cancellationToken);
                var finalDestination = Path.Combine([finalInputRoot, .. relativeParts]);
                databases.Add(new ParserDatabaseInput(
                    input.GenerationId,
                    relativePath,
                    finalDestination,
                    input.Sha256.ToLowerInvariant()));
            }

            var manifest = new ParserJobManifest(
                1,
                job.Id,
                job.SourceSetId,
                finalInputRoot,
                finalOutputRoot,
                Array.AsReadOnly(databases.ToArray()),
                maximumMessages);
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            try
            {
                await AtomicFile.ReplaceAsync(
                    Path.Combine(temporaryRoot, "job.json"),
                    json,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporaryRoot, finalJobRoot);
            return new BuiltParserJob(
                finalJobRoot,
                finalInputRoot,
                finalOutputRoot,
                Path.Combine(finalJobRoot, "job.json"),
                manifest);
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    public async Task<BuiltParserJob> LoadExistingAsync(
        ParseJob job,
        IReadOnlyList<ParseJobInput> inputs,
        int maximumMessages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(inputs);
        var jobRoot = Path.Combine(_jobsRoot, job.Id);
        var jobManifestPath = Path.Combine(jobRoot, "job.json");
        var info = new FileInfo(jobManifestPath);
        if (!info.Exists || info.Length > 1024 * 1024)
            throw new InvalidDataException("Existing parser job manifest is unavailable.");
        var json = await File.ReadAllBytesAsync(jobManifestPath, cancellationToken);
        ParserJobManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ParserJobManifest>(json, JsonOptions) ??
                throw new InvalidDataException("Existing parser job manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Existing parser job manifest is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }

        var inputRoot = Path.Combine(jobRoot, "input");
        var outputRoot = Path.Combine(jobRoot, "output");
        if (manifest.SchemaVersion != 1 ||
            manifest.JobId != job.Id ||
            manifest.SourceSetId != job.SourceSetId ||
            manifest.MaximumMessages != maximumMessages ||
            Path.GetFullPath(manifest.InputRoot) != inputRoot ||
            Path.GetFullPath(manifest.OutputRoot) != outputRoot ||
            manifest.Databases.Count != inputs.Count)
        {
            throw new InvalidDataException("Existing parser job identity changed.");
        }

        var expectedInputs = inputs.OrderBy(item => item.Ordinal).ToArray();
        for (var index = 0; index < expectedInputs.Length; index++)
        {
            var expected = expectedInputs[index];
            var actual = manifest.Databases[index];
            var relative = NormalizeRelativePath(expected.RelativePath);
            var expectedPath = Path.Combine([inputRoot, .. relative.Split('/')]);
            if (actual.GenerationId != expected.GenerationId ||
                actual.RelativePath != relative ||
                Path.GetFullPath(actual.Path) != expectedPath ||
                !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(expectedPath) ||
                !string.Equals(
                    await FileSha256Async(expectedPath, cancellationToken),
                    expected.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Existing parser input no longer matches its catalog generation.");
            }
        }
        Directory.CreateDirectory(outputRoot);
        return new BuiltParserJob(
            jobRoot,
            inputRoot,
            outputRoot,
            jobManifestPath,
            manifest);
    }

    private static async Task MaterializeAsync(
        string source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
            throw new IOException("Parser input destination already exists.");
        if (!TryCreateHardLink(source, destination))
        {
            var temporary = destination + $".{Guid.NewGuid():N}.copying";
            try
            {
                await using (var input = new FileStream(
                    source,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                TryDeleteFile(temporary);
            }
        }

        var materializedHash = await FileSha256Async(destination, cancellationToken);
        if (!string.Equals(materializedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(destination);
            throw new CryptographicException("Materialized parser input hash mismatch.");
        }
    }

    private static bool TryCreateHardLink(string source, string destination)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return CreateHardLinkWindows(destination, source, IntPtr.Zero);
            return CreateHardLinkUnix(source, destination) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static async Task<string> FileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static void ValidateRelativePath(string path)
    {
        var parts = path.Split('/');
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith('/') ||
            path.StartsWith('\\') ||
            path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ||
            parts.Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Parse job relative path is unsafe.");
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 hex string.", parameterName);
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Job ID is not a safe path segment.", parameterName);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException)
        {
            // Best-effort cleanup preserves the primary materialization failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup preserves the primary materialization failure.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup preserves the primary job publication failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup preserves the primary job publication failure.
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingPath, string newPath);
}
