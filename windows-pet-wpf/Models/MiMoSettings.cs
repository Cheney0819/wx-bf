using System.Text.Json;

namespace DesktopPet.Wpf.Models;

public sealed class MiMoSettings
{
    public string BaseUrl { get; set; } = "https://api.xiaomimimo.com/v1";
    public string ChatModel { get; set; } = "mimo-v2.5-pro";
    public string AsrModel { get; set; } = "mimo-v2.5-asr";
    public string TtsModel { get; set; } = "mimo-v2.5-tts";
    public bool EnableTts { get; set; }
    public string TtsVoice { get; set; } = "冰糖";
    public string TtsStylePrompt { get; set; } = "请用温柔、自然、亲近、带一点陪伴感的中文女声说这句话，语速自然，不要过度夸张。";
    public int MaxRecordSeconds { get; set; } = 8;
    public int ChatTimeoutSeconds { get; set; } = 18;
    public int AsrTimeoutSeconds { get; set; } = 20;
    public int RecentTurnLimit { get; set; } = 8;
    public int MemoryRecallLimit { get; set; } = 5;
    public int MaxReplyChars { get; set; } = 120;

    public static MiMoSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                var defaults = new MiMoSettings();
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
                File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
                return defaults;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MiMoSettings>(
                       json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                   )
                   ?? new MiMoSettings();
        }
        catch
        {
            return new MiMoSettings();
        }
    }
}
