using System.IO;

namespace DesktopPet.Wpf.Services;

internal static class SecretStore
{
    public static string GetMiMoApiKey()
    {
        string envValue = Environment.GetEnvironmentVariable("MIMO_API_KEY")?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(envValue))
            return envValue;

        string path = PetAiPaths.GetApiKeyPath();
        if (!File.Exists(path))
            return "";

        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return "";
        }
    }
}
