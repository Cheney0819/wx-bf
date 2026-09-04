namespace Footprint.Core;

public sealed record VerificationTrial(int Compatibility, int ExitCode, string? CipherVersion,
    string? CipherIntegrity, string? Integrity, int SchemaObjectCount, int? PageSize = null,
    string? StandardError = null, bool TimedOut = false, bool StreamDrainTimedOut = false)
{
    public bool IsCompleteSuccess(string expectedCipherVersion) =>
        !TimedOut && !StreamDrainTimedOut && ExitCode == 0 &&
        CipherVersion?.Contains(expectedCipherVersion, StringComparison.Ordinal) == true &&
        (string.Equals(CipherIntegrity?.Trim(), "ok", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(CipherIntegrity?.Trim(), "unsupported-by-4.1.0", StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(Integrity?.Trim(), "ok", StringComparison.OrdinalIgnoreCase) && SchemaObjectCount > 0;
}

public sealed record VerificationVerdict(bool Accepted, int? Compatibility, int? PageSize,
    IReadOnlyList<VerificationTrial> Trials, string Reason);

public static class VerificationPolicy
{
    public static VerificationVerdict Evaluate(IEnumerable<VerificationTrial> trials, string expectedCipherVersion)
    {
        var all = trials.ToArray();
        var accepted = all.Where(t => t.IsCompleteSuccess(expectedCipherVersion)).ToArray();
        return accepted.Length == 1
            ? new VerificationVerdict(true, accepted[0].Compatibility, accepted[0].PageSize, all, "Exactly one complete SQLCipher configuration succeeded.")
            : new VerificationVerdict(false, null, null, all,
                accepted.Length == 0 ? "No SQLCipher configuration passed all checks." : "More than one SQLCipher configuration passed; acceptance is ambiguous.");
    }
}
