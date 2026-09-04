using System.Runtime.InteropServices;

namespace Footprint.ReceiverRuntime;

public static class ReceiverRuntimeVerifier
{
    public static bool IsSupported(OSPlatform operatingSystem, Architecture architecture) =>
        operatingSystem == OSPlatform.OSX && architecture == Architecture.Arm64;

    public static void VerifyCurrent()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("Footprint 接收端仅支持 Apple Silicon macOS (arm64)。");
    }
}
