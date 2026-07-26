using System.Security.Cryptography;

namespace Wx411.Core;

public sealed record CapturedKeyMaterial(
    string CallpointName,
    int HitRva,
    string RegisterValues,
    int Pid,
    DateTime CapturedAt) : IDisposable
{
    private byte[]? _keyData;

    public byte[]? KeyData
    {
        get => _keyData;
        init => _keyData = value;
    }

    public int? KeyLength { get; init; }
    public string? Error { get; init; }

    public bool IsValid => KeyData is not null && KeyLength > 0;

    public void Dispose()
    {
        var keyData = Interlocked.Exchange(ref _keyData, null);
        if (keyData is not null) CryptographicOperations.ZeroMemory(keyData);
    }
}
