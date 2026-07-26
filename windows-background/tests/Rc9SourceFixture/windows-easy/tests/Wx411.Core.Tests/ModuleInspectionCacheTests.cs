using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Wx411.Core.Tests;

public sealed class ModuleInspectionCacheTests
{
    [Fact]
    public void UnchangedGenerationIsReadOnceAndAllSignaturesUseThatRead()
    {
        var signature = new byte[] { 1, 2, 3, 4 };
        var callpoint = new CallpointDefinition(
            "sample", 0x1010, 0x1010, signature,
            CallpointRegisterSemantics.Sqlite3KeySink, string.Empty);
        var profile = new ModuleCallpointProfile(
            "fixture", "1.0.0.0", "ignored", CodecHolderStrategy.StructureOnly, [callpoint]);
        var image = BuildPe(signature);
        var generation = new ModuleFileGeneration("fixture.dll", image.Length, DateTime.UnixEpoch);
        var reads = 0;
        var cache = new ModuleInspectionCache(
            _ => generation,
            _ => { reads++; return image.ToArray(); },
            _ => "1.0.0.0",
            (version, hash) => new ModuleIdentityValidation(true, version, hash, profile, null));

        var first = cache.Inspect("fixture.dll", ["sample"]);
        var second = cache.Inspect("fixture.dll", ["sample"]);

        Assert.True(first.Identity.IsValid);
        Assert.Equal(["sample"], first.VerifiedCallpoints.Select(item => item.Name));
        Assert.Same(first, second);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void ChangedGenerationInvalidatesCacheAndMidReadChangeIsRejected()
    {
        var image = BuildPe([1, 2, 3, 4]);
        var generationIndex = 0;
        var generations = new[]
        {
            new ModuleFileGeneration("fixture.dll", image.Length, DateTime.UnixEpoch),
            new ModuleFileGeneration("fixture.dll", image.Length + 1, DateTime.UnixEpoch.AddSeconds(1)),
        };
        var cache = new ModuleInspectionCache(
            _ => generations[Math.Min(generationIndex++, 1)],
            _ => image.ToArray(),
            _ => "1.0.0.0",
            (version, hash) => new ModuleIdentityValidation(true, version, hash, CallpointProfiles.Preferred, null));

        var result = cache.Inspect("fixture.dll", [CallpointProfiles.Preferred.Callpoints[0].Name]);

        Assert.False(result.Identity.IsValid);
        Assert.Contains("changed during inspection", result.Error, StringComparison.Ordinal);
    }

    private static byte[] BuildPe(byte[] signature)
    {
        var image = new byte[0x400];
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94), 0xF0);
        var section = 0x188;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 8), 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 12), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 16), 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 20), 0x200);
        signature.CopyTo(image, 0x210);
        return image;
    }
}
