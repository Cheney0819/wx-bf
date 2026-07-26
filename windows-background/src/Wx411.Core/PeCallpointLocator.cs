using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wx411.Core;

public sealed record ModuleIdentityValidation(
    bool IsValid,
    string ActualVersion,
    string ActualSha256,
    ModuleCallpointProfile? Profile,
    string? Error)
{
    public bool IsUnsupported =>
        !IsValid &&
        Profile is null &&
        !string.IsNullOrWhiteSpace(ActualVersion) &&
        !string.IsNullOrWhiteSpace(ActualSha256);
}

public static class PeCallpointLocator
{
    public static string ComputeSha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static ModuleIdentityValidation ValidateModuleIdentity(string dllPath)
    {
        if (!File.Exists(dllPath))
            return new(false, string.Empty, string.Empty, null, "module file not found");

        string actualHash;
        string actualVersion;
        try
        {
            actualHash = ComputeSha256(dllPath);
            var info = FileVersionInfo.GetVersionInfo(dllPath);
            actualVersion = string.Join('.',
                info.FileMajorPart,
                info.FileMinorPart,
                info.FileBuildPart,
                info.FilePrivatePart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, string.Empty, string.Empty, null, $"module identity read failed: {ex.Message}");
        }

        return ValidateIdentity(actualVersion, actualHash);
    }

    public static ModuleIdentityValidation ValidateIdentity(string actualVersion, string actualSha256)
    {
        ArgumentNullException.ThrowIfNull(actualVersion);
        ArgumentNullException.ThrowIfNull(actualSha256);
        var profile = CallpointProfiles.FindByIdentity(actualVersion, actualSha256);
        if (profile is not null)
            return new(true, actualVersion, actualSha256, profile, null);

        var knownVersion = CallpointProfiles.Supported.FirstOrDefault(candidate =>
            string.Equals(candidate.ModuleVersion, actualVersion, StringComparison.Ordinal));
        var detail = knownVersion is null
            ? $"module version mismatch: unsupported {actualVersion}"
            : $"module SHA-256 mismatch for {actualVersion}: expected {knownVersion.ModuleSha256}, actual {actualSha256}";
        var error = $"{UnsupportedModuleException.StableCode}: {detail}";
        return new(false, actualVersion, actualSha256, null, error);
    }

    public static bool VerifySignature(string dllPath, CallpointDefinition def)
    {
        var bytes = File.ReadAllBytes(dllPath);
        var fileOffset = RvaToFileOffset(bytes, def.SignatureRva);
        if (fileOffset < 0) return false;
        if (fileOffset + def.SigLength > bytes.Length) return false;
        for (var i = 0; i < def.SigLength; i++)
            if (bytes[fileOffset + i] != def.ExpectedSig[i])
                return false;
        return true;
    }

    public static bool VerifyAllSignatures(string dllPath, ModuleCallpointProfile? profile = null)
    {
        profile ??= ValidateModuleIdentity(dllPath).Profile;
        if (profile is null) return false;
        foreach (var def in profile.Callpoints)
            if (!VerifySignature(dllPath, def)) return false;
        return true;
    }

    public static int RvaToFileOffset(byte[] peImage, int rva)
    {
        // DOS header
        if (peImage.Length < 64) return -1;
        var eLfanew = BitConverter.ToInt32(peImage, 0x3C);
        if (eLfanew + 4 + 20 > peImage.Length) return -1;

        // NT headers
        var machine = BitConverter.ToUInt16(peImage, eLfanew + 4);
        if (machine != 0x8664) return -1; // AMD64

        var sectionOffset = eLfanew + 24;
        var sizeOfOptionalHeader = BitConverter.ToUInt16(peImage, eLfanew + 20);
        sectionOffset += sizeOfOptionalHeader;

        var sectionCount = BitConverter.ToUInt16(peImage, eLfanew + 6);
        for (var i = 0; i < sectionCount; i++)
        {
            var secStart = sectionOffset + i * 40;
            if (secStart + 40 > peImage.Length) return -1;

            var virtualSize = BitConverter.ToUInt32(peImage, secStart + 8);
            var virtualAddress = BitConverter.ToUInt32(peImage, secStart + 12);
            var sizeOfRawData = BitConverter.ToUInt32(peImage, secStart + 16);
            var pointerToRawData = BitConverter.ToUInt32(peImage, secStart + 20);

            if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, sizeOfRawData))
            {
                return (int)(pointerToRawData + (rva - virtualAddress));
            }
        }
        return -1;
    }
}
