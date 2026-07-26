using System.Diagnostics;
using System.Security.Cryptography;

namespace Wx411.Core;

public sealed record ModuleFileGeneration(
    string NormalizedPath,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record ModuleInspectionResult(
    ModuleFileGeneration Generation,
    ModuleIdentityValidation Identity,
    IReadOnlyList<CallpointDefinition> VerifiedCallpoints,
    string? Error);

public sealed class ModuleInspectionCache
{
    private readonly Func<string, ModuleFileGeneration> _readGeneration;
    private readonly Func<string, byte[]> _readImage;
    private readonly Func<string, string> _readVersion;
    private readonly Func<string, string, ModuleIdentityValidation> _validateIdentity;
    private readonly object _sync = new();
    private ModuleInspectionResult? _cached;

    public ModuleInspectionCache()
        : this(ReadGeneration, File.ReadAllBytes, ReadVersion, PeCallpointLocator.ValidateIdentity)
    {
    }

    internal ModuleInspectionCache(
        Func<string, ModuleFileGeneration> readGeneration,
        Func<string, byte[]> readImage,
        Func<string, string> readVersion,
        Func<string, string, ModuleIdentityValidation> validateIdentity)
    {
        ArgumentNullException.ThrowIfNull(readGeneration);
        ArgumentNullException.ThrowIfNull(readImage);
        ArgumentNullException.ThrowIfNull(readVersion);
        ArgumentNullException.ThrowIfNull(validateIdentity);
        _readGeneration = readGeneration;
        _readImage = readImage;
        _readVersion = readVersion;
        _validateIdentity = validateIdentity;
    }

    public ModuleInspectionResult Inspect(
        string path,
        IReadOnlyCollection<string> requestedCallpointNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(requestedCallpointNames);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var before = _readGeneration(path);
            if (_cached is not null && _cached.Generation == before)
                return _cached;

            byte[]? image = null;
            try
            {
                image = _readImage(path);
                cancellationToken.ThrowIfCancellationRequested();
                var hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
                var version = _readVersion(path);
                var identity = _validateIdentity(version, hash);
                var verified = identity.Profile is null
                    ? Array.Empty<CallpointDefinition>()
                    : identity.Profile.Callpoints
                        .Where(callpoint => VerifySignature(image, callpoint))
                        .ToArray();
                var after = _readGeneration(path);
                if (before != after)
                {
                    var error = "module file generation changed during inspection";
                    return new ModuleInspectionResult(
                        after,
                        new ModuleIdentityValidation(false, version, hash, null, error),
                        [],
                        error);
                }

                _cached = new ModuleInspectionResult(
                    before,
                    identity,
                    Array.AsReadOnly(verified),
                    identity.Error);
                return _cached;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or CryptographicException)
            {
                var error = $"module inspection failed: {ex.Message}";
                return new ModuleInspectionResult(
                    SafeGeneration(path),
                    new ModuleIdentityValidation(false, string.Empty, string.Empty, null, error),
                    [],
                    error);
            }
            finally
            {
                if (image is not null) CryptographicOperations.ZeroMemory(image);
            }
        }
    }

    private ModuleFileGeneration SafeGeneration(string path)
    {
        try
        {
            return _readGeneration(path);
        }
        catch
        {
            return new ModuleFileGeneration(Path.GetFullPath(path), 0, DateTime.MinValue);
        }
    }

    private static bool VerifySignature(byte[] image, CallpointDefinition callpoint)
    {
        var offset = PeCallpointLocator.RvaToFileOffset(image, callpoint.SignatureRva);
        return offset >= 0 &&
               offset + callpoint.SigLength <= image.Length &&
               image.AsSpan(offset, callpoint.SigLength).SequenceEqual(callpoint.ExpectedSig);
    }

    private static ModuleFileGeneration ReadGeneration(string path)
    {
        var normalized = Path.GetFullPath(path);
        var info = new FileInfo(normalized);
        info.Refresh();
        if (!info.Exists) throw new FileNotFoundException("Module file was not found.", normalized);
        return new ModuleFileGeneration(normalized, info.Length, info.LastWriteTimeUtc);
    }

    private static string ReadVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return string.Join('.',
            info.FileMajorPart,
            info.FileMinorPart,
            info.FileBuildPart,
            info.FilePrivatePart);
    }
}
