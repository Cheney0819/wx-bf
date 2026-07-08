using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DesktopPet.Wpf.Models;

namespace DesktopPet.Wpf.Services;

internal sealed class MiMoApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient = new();

    public async Task<string> TranscribeAsync(
        MiMoSettings settings,
        string apiKey,
        string audioFilePath,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("未配置 MiMo API Key");

        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException("没有找到录音文件", audioFilePath);

        string base64Audio = Convert.ToBase64String(await File.ReadAllBytesAsync(audioFilePath, cancellationToken));
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatUrl(settings.BaseUrl));
        ApplyAuthentication(request, apiKey);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.AsrModel,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = "请把这段中文语音识别成简体中文文本，只返回识别结果。"
                        },
                        new Dictionary<string, object?>
                        {
                            ["type"] = "input_audio",
                            ["input_audio"] = new Dictionary<string, object?>
                            {
                                ["data"] = base64Audio,
                                ["format"] = "wav"
                            }
                        }
                    }
                }
            }
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.AsrTimeoutSeconds)));

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        string responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiMo ASR 请求失败: {(int)response.StatusCode} {responseText}");

        return ExtractMessageContent(responseText, "MiMo ASR 没有返回有效识别结果");
    }

    public async Task<string> ChatAsync(
        MiMoSettings settings,
        string apiKey,
        string systemPrompt,
        IReadOnlyList<ConversationTurn> recentTurns,
        string userInput,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("未配置 MiMo API Key");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatUrl(settings.BaseUrl));
        ApplyAuthentication(request, apiKey);

        var messages = new List<ChatMessagePayload>
        {
            new("system", systemPrompt)
        };
        messages.AddRange(
            recentTurns
                .Where(turn => !string.IsNullOrWhiteSpace(turn.Role) && !string.IsNullOrWhiteSpace(turn.Text))
                .Select(turn => new ChatMessagePayload(turn.Role.Trim(), turn.Text.Trim()))
        );
        messages.Add(new ChatMessagePayload("user", userInput.Trim()));

        var payload = new ChatRequestPayload(settings.ChatModel, messages);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json"
        );

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.ChatTimeoutSeconds)));

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        string responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiMo 请求失败: {(int)response.StatusCode} {responseText}");

        return ExtractMessageContent(responseText, "MiMo 没有返回有效回复");
    }

    public async Task<string> SynthesizeAsync(
        MiMoSettings settings,
        string apiKey,
        string text,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("未配置 MiMo API Key");

        string normalized = text.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("没有可合成的文本");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatUrl(settings.BaseUrl));
        ApplyAuthentication(request, apiKey);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.TtsModel,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = settings.TtsStylePrompt
                },
                new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = normalized
                }
            },
            ["audio"] = new Dictionary<string, object?>
            {
                ["format"] = "wav",
                ["voice"] = string.IsNullOrWhiteSpace(settings.TtsVoice) ? "冰糖" : settings.TtsVoice
            }
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.AsrTimeoutSeconds)));

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        string responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiMo TTS 请求失败: {(int)response.StatusCode} {responseText}");

        using var document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("choices", out var choicesElement)
            || choicesElement.ValueKind != JsonValueKind.Array
            || choicesElement.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("MiMo TTS 没有返回有效音频");
        }

        var messageElement = choicesElement[0].GetProperty("message");
        if (!messageElement.TryGetProperty("audio", out var audioElement)
            || !audioElement.TryGetProperty("data", out var audioDataElement)
            || audioDataElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("MiMo TTS 没有返回有效音频数据");
        }

        string base64Data = audioDataElement.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(base64Data))
            throw new InvalidOperationException("MiMo TTS 音频数据为空");

        byte[] audioBytes = Convert.FromBase64String(base64Data);
        string audioDir = Path.Combine(PetAiPaths.GetPetAiDir(), "audio_cache");
        Directory.CreateDirectory(audioDir);
        string audioFilePath = Path.Combine(audioDir, $"tts_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(audioFilePath, audioBytes, timeoutCts.Token);
        return audioFilePath;
    }

    private static string BuildChatUrl(string baseUrl)
    {
        return $"{baseUrl.TrimEnd('/')}/chat/completions";
    }

    private static void ApplyAuthentication(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Remove("api-key");
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
    }

    private static string ExtractMessageContent(string responseText, string fallbackError)
    {
        using var document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("choices", out var choicesElement)
            || choicesElement.ValueKind != JsonValueKind.Array
            || choicesElement.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(fallbackError);
        }

        var messageElement = choicesElement[0].GetProperty("message");
        if (!messageElement.TryGetProperty("content", out var contentElement))
            throw new InvalidOperationException(fallbackError);

        string? content = contentElement.ValueKind switch
        {
            JsonValueKind.String => contentElement.GetString(),
            JsonValueKind.Array => ExtractArrayContent(contentElement),
            _ => contentElement.ToString()
        };

        content = content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(fallbackError);

        return content;
    }

    private static string ExtractArrayContent(JsonElement contentElement)
    {
        var textParts = new List<string>();
        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                textParts.Add(item.GetString() ?? "");
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String)
            {
                textParts.Add(textElement.GetString() ?? "");
            }
        }

        return string.Join("", textParts);
    }

    private sealed record ChatRequestPayload(string Model, List<ChatMessagePayload> Messages);

    private sealed record ChatMessagePayload(string Role, string Content);
}
