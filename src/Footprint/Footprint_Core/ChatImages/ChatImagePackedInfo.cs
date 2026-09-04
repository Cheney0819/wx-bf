using System.Text;

namespace Footprint.Core;

public static class ChatImagePackedInfo
{
    private static readonly byte[] Prefix = [0x12, 0x22, 0x0A, 0x20];

    public static bool TryReadStem(ReadOnlySpan<byte> packedInfo, out string? stem)
    {
        stem = null;
        if (packedInfo.Length != Prefix.Length + 32 || !packedInfo[..Prefix.Length].SequenceEqual(Prefix))
            return false;

        var candidate = Encoding.ASCII.GetString(packedInfo[Prefix.Length..]);
        if (candidate.Length != 32 || candidate.Any(ch => !Uri.IsHexDigit(ch))) return false;
        stem = candidate.ToLowerInvariant();
        return true;
    }
}
