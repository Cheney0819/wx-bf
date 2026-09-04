using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Footprint.Core;

public sealed record PeVerificationResult(bool IsValid, IReadOnlyList<string> Errors);

public static class PeVerifier
{
    public static PeVerificationResult Verify(Stream stream, TargetProfile profile)
    {
        var errors = new List<string>();
        try
        {
            var image = ReadImage(stream);
            if (image.Machine != 0x8664) errors.Add("pe_machine_not_x64");

            foreach (var name in TargetProfile.RequiredRvas)
            {
                if (profile.Rvas is null || !profile.Rvas.TryGetValue(name, out var entry) || entry is null ||
                    entry.Signature is null || entry.Mask is null || entry.Mask.Length != entry.Signature.Length)
                {
                    errors.Add($"profile_entry_invalid:{name}");
                    continue;
                }

                var offset = RvaToOffset(entry.Rva, entry.Signature.Length, image.Sections);
                if (offset is null)
                {
                    errors.Add($"rva_not_executable:{name}");
                    continue;
                }

                var actual = new byte[entry.Signature.Length];
                stream.Position = offset.Value;
                ReadExactly(stream, actual);
                for (var index = 0; index < actual.Length; index++)
                {
                    if ((actual[index] & entry.Mask[index]) == (entry.Signature[index] & entry.Mask[index])) continue;
                    errors.Add($"signature_mismatch:{name}");
                    break;
                }
            }
        }
        catch (InvalidDataException)
        {
            errors.Add("pe_invalid");
        }
        catch (EndOfStreamException)
        {
            errors.Add("pe_invalid");
        }
        catch (OverflowException)
        {
            errors.Add("pe_invalid");
        }

        return new PeVerificationResult(errors.Count == 0, errors);
    }

    public static async Task<PeVerificationResult> VerifyAsync(string path, TargetProfile profile,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 64, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var errors = new List<string>();
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(actualHash, profile.DllSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add("dll_sha256_mismatch");

        stream.Position = 0;
        errors.AddRange(Verify(stream, profile).Errors);
        return new PeVerificationResult(errors.Count == 0, errors);
    }

    private static PeImage ReadImage(Stream stream)
    {
        if (!stream.CanSeek) throw new InvalidDataException("PE stream must be seekable.");
        if (stream.Length < 0x40) throw new InvalidDataException("PE image is too short.");

        stream.Position = 0;
        var dos = new byte[64];
        ReadExactly(stream, dos);
        if (dos[0] != 'M' || dos[1] != 'Z') throw new InvalidDataException("PE DOS header is invalid.");

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C, 4));
        if (peOffset < dos.Length || peOffset > stream.Length - 24) throw new InvalidDataException("PE offset is invalid.");
        stream.Position = peOffset;

        var header = new byte[24];
        ReadExactly(stream, header);
        if (!header.AsSpan(0, 4).SequenceEqual("PE\0\0"u8)) throw new InvalidDataException("PE signature is invalid.");
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20, 2));
        if (count == 0 || optionalSize < 2 || stream.Position + optionalSize > stream.Length)
            throw new InvalidDataException("PE section table is invalid.");

        var optional = new byte[optionalSize];
        ReadExactly(stream, optional);
        if (BinaryPrimitives.ReadUInt16LittleEndian(optional) != 0x20B)
            throw new InvalidDataException("PE optional header is not PE32+.");
        if (count > (stream.Length - stream.Position) / 40) throw new InvalidDataException("PE sections are truncated.");

        var sections = new Section[count];
        for (var index = 0; index < count; index++)
        {
            var section = new byte[40];
            ReadExactly(stream, section);
            var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(12, 4));
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(8, 4));
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(16, 4));
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(20, 4));
            var characteristics = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(36, 4));
            if ((ulong)rawOffset + rawSize > (ulong)stream.Length) throw new InvalidDataException("PE section data is truncated.");
            sections[index] = new Section(virtualAddress, virtualSize, rawOffset, rawSize, characteristics);
        }

        return new PeImage(machine, sections);
    }

    private static long? RvaToOffset(ulong rva, int length, IEnumerable<Section> sections)
    {
        foreach (var section in sections)
        {
            if ((section.Characteristics & 0x20000000) == 0) continue;
            var sectionStart = (ulong)section.VirtualAddress;
            if (rva < sectionStart) continue;
            var relative = rva - sectionStart;
            if (relative >= section.VirtualSize || relative >= section.RawSize ||
                (ulong)length > (ulong)section.VirtualSize - relative ||
                (ulong)length > (ulong)section.RawSize - relative) continue;
            return checked((long)checked((ulong)section.RawOffset + relative));
        }
        return null;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    private sealed record PeImage(ushort Machine, IReadOnlyList<Section> Sections);
    private sealed record Section(uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize, uint Characteristics);
}
