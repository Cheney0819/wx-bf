using System.Security.Cryptography;
using System.Text;
using Footprint.Receiver.Configuration;
using Footprint.Receiver.Mac;
using Footprint.Receiver.Network;

namespace Footprint.Receiver;

public sealed class EnrollmentCommand(IReceiverEnrollmentClient enrollmentClient, IReceiverTokenStore tokenStore, IReceiverConfigurationStore configurationStore,
    IReceiverDeviceIdentity? deviceIdentity = null)
{
    public async Task ExecuteAsync(string[] args, Stream standardInput, string displayName, CancellationToken cancellationToken = default)
    {
        var serverUri = Parse(args);
        var existingConfig = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (existingConfig is not null)
        {
            if (existingConfig.ServerUri != ReceiverEnrollmentClient.NormalizeBase(serverUri)) throw new InvalidOperationException("现有注册与指定服务器不一致。");
        }

        var requestedDeviceId = existingConfig?.DeviceId ?? deviceIdentity?.GetStableDeviceId() ?? new ReceiverDeviceIdentity().GetStableDeviceId();
        ReceiverEnrollmentClient.ValidateDeviceId(requestedDeviceId);
        var code = await ReadCodeAsync(standardInput, cancellationToken).ConfigureAwait(false);
        ReceiverEnrollmentResult result;
        try { result = await enrollmentClient.EnrollAsync(serverUri, code, requestedDeviceId, displayName, cancellationToken).ConfigureAwait(false); }
        finally { Array.Clear(code); }

        var token = result.Token.ToCharArray();
        await tokenStore.SetTokenAsync(token, cancellationToken).ConfigureAwait(false);
        await configurationStore.SaveAsync(new ReceiverOptions(1, serverUri, result.DeviceId, displayName, TimeSpan.FromSeconds(30)), cancellationToken).ConfigureAwait(false);
    }

    private static Uri Parse(string[] args)
    {
        if (args.Any(value => value.Equals("--registration-code", StringComparison.Ordinal) || value.StartsWith("--registration-code=", StringComparison.Ordinal))) throw new ArgumentException("注册码只能从 stdin 读取。");
        if (args.Length != 3 || args[0] != "--server-uri" || args[2] != "--registration-code-stdin") throw new ArgumentException("用法：enroll --server-uri <https-url> --registration-code-stdin");
        if (!Uri.TryCreate(args[1], UriKind.Absolute, out var uri)) throw new ArgumentException("服务器地址无效。");
        ReceiverEnrollmentClient.ValidateHttps(uri);
        return uri;
    }

    private static async Task<char[]> ReadCodeAsync(Stream input, CancellationToken cancellationToken)
    {
        const int maximum = 4096;
        var bytes = new byte[maximum + 1];
        var length = 0;
        try
        {
            while (length < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > maximum) throw new InvalidDataException("注册码过长。");
            while (length > 0 && bytes[length - 1] is (byte)'\n' or (byte)'\r') length--;
            if (length == 0) throw new InvalidDataException("stdin 中没有注册码。");
            var chars = new char[Encoding.UTF8.GetCharCount(bytes.AsSpan(0, length))];
            Encoding.UTF8.GetChars(bytes.AsSpan(0, length), chars);
            return chars;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
