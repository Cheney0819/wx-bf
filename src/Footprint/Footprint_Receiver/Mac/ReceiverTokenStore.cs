using System.Security.Cryptography;
using System.Text;

namespace Footprint.Receiver.Mac;

public interface IKeychainGenericPasswordBackend
{
    ValueTask<byte[]?> ReadAsync(string service, string account, CancellationToken cancellationToken);
    ValueTask WriteAsync(string service, string account, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken);
}

public interface IReceiverTokenStore
{
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default);
    ValueTask SetTokenAsync(char[] token, CancellationToken cancellationToken = default);
}

public sealed class ReceiverTokenStore(IKeychainGenericPasswordBackend backend) : IReceiverTokenStore
{
    public const string ServiceName = "com.deskmate.footprint.receiver";
    public const string AccountName = "receiver-token";

    public async ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await backend.ReadAsync(ServiceName, AccountName, cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;
        try { return ReceiverToken.DecodeAndValidate(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async ValueTask SetTokenAsync(char[] token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        byte[] bytes = [];
        try
        {
            ReceiverToken.Validate(token);
            var byteCount = Encoding.UTF8.GetByteCount(token);
            bytes = new byte[byteCount];
            Encoding.UTF8.GetBytes(token, bytes);
            await backend.WriteAsync(ServiceName, AccountName, bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (bytes.Length > 0) CryptographicOperations.ZeroMemory(bytes);
            Array.Clear(token);
        }
    }
}
