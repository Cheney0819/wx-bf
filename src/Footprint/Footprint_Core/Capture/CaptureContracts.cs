namespace Footprint.Core.Capture;

public sealed record CaptureGenerationId(string DeviceId, string AccountHash, string WeixinDllSha256, long CaptureGeneration)
{
    public string BudgetKey => $"{DeviceId}|{AccountHash}|{WeixinDllSha256.ToLowerInvariant()}|{CaptureGeneration}";
}
