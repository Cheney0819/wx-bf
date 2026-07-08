namespace DesktopPet.Wpf.Services;

internal static class PetAiPaths
{
    public static string GetPetAiDir()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "wechat_data", "pet_ai");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string GetConfigPath() => Path.Combine(GetPetAiDir(), "mimo_config.json");

    public static string GetApiKeyPath() => Path.Combine(GetPetAiDir(), "mimo_api_key.txt");

    public static string GetRecentSessionPath() => Path.Combine(GetPetAiDir(), "recent_session.json");

    public static string GetMemoryProfilePath() => Path.Combine(GetPetAiDir(), "memory_profile.json");

    public static string GetMemoriesPath() => Path.Combine(GetPetAiDir(), "memories.json");
}
