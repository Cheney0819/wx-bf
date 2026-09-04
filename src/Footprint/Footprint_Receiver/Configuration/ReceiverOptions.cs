using Footprint.Receiver.Network;

namespace Footprint.Receiver.Configuration;

public sealed record ReceiverOptions
{
    public ReceiverOptions(int schemaVersion, Uri serverUri, string deviceId, string displayName, TimeSpan pollInterval)
    {
        if (schemaVersion != 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (serverUri.Scheme != Uri.UriSchemeHttps || !serverUri.IsAbsoluteUri || !string.IsNullOrEmpty(serverUri.UserInfo)) throw new ArgumentException("服务器地址必须使用无用户信息的绝对 HTTPS URI。", nameof(serverUri));
        ReceiverEnrollmentClient.ValidateDeviceId(deviceId);
        ReceiverEnrollmentClient.ValidateDisplayName(displayName);
        if (pollInterval < TimeSpan.FromSeconds(1) || pollInterval > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        SchemaVersion = schemaVersion;
        ServerUri = new Uri(serverUri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/", UriKind.Absolute);
        DeviceId = deviceId;
        DisplayName = displayName;
        PollInterval = pollInterval;
    }

    public int SchemaVersion { get; }
    public Uri ServerUri { get; }
    public string DeviceId { get; }
    public string DisplayName { get; }
    public TimeSpan PollInterval { get; }
}
