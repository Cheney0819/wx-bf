using DesktopPet.Wpf.Models;

namespace DesktopPet.Wpf.Services;

internal sealed class SpeechSessionController
{
    private readonly MemoryService _memoryService = new();
    private readonly MiMoApiClient _mimoApiClient = new();

    public async Task<PetChatResult> SendAudioAsync(string audioFilePath, CancellationToken cancellationToken)
    {
        var settings = MiMoSettings.Load(PetAiPaths.GetConfigPath());
        string apiKey = SecretStore.GetMiMoApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new PetChatResult
            {
                Success = false,
                ErrorMessage = "未配置 MiMo API Key",
                ReplyText = $"我还没有拿到 MiMo 的钥匙呢，把 `MIMO_API_KEY` 环境变量或 `{Path.GetFileName(PetAiPaths.GetApiKeyPath())}` 配好就能继续啦。",
                SuggestedAction = "surprised"
            };
        }

        try
        {
            string recognizedText = await _mimoApiClient.TranscribeAsync(settings, apiKey, audioFilePath, cancellationToken);
            var chatResult = await SendTextCoreAsync(recognizedText, settings, apiKey, cancellationToken);
            chatResult.UserText = recognizedText;
            return chatResult;
        }
        catch (OperationCanceledException)
        {
            return new PetChatResult
            {
                Success = false,
                ErrorMessage = "语音识别超时",
                ReplyText = "我刚刚听得有一点久，妈妈可以再试一次。",
                SuggestedAction = "surprised"
            };
        }
        catch (Exception ex)
        {
            return new PetChatResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReplyText = "刚刚那句我没有听清呢，妈妈可以再说一次。",
                SuggestedAction = "surprised"
            };
        }
        finally
        {
            TryDelete(audioFilePath);
        }
    }

    public async Task<PetChatResult> SendTextAsync(string userText, CancellationToken cancellationToken)
    {
        string normalized = userText.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new PetChatResult
            {
                Success = false,
                ErrorMessage = "没有输入内容",
                ReplyText = "妈妈还没有告诉我要说什么呢。",
                SuggestedAction = "shy"
            };
        }

        var settings = MiMoSettings.Load(PetAiPaths.GetConfigPath());
        string apiKey = SecretStore.GetMiMoApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new PetChatResult
            {
                Success = false,
                ErrorMessage = "未配置 MiMo API Key",
                ReplyText = $"我还没有拿到 MiMo 的钥匙呢，把 `MIMO_API_KEY` 环境变量或 `{Path.GetFileName(PetAiPaths.GetApiKeyPath())}` 配好就能继续啦。",
                SuggestedAction = "surprised"
            };
        }

        return await SendTextCoreAsync(normalized, settings, apiKey, cancellationToken);
    }

    private async Task<PetChatResult> SendTextCoreAsync(
        string normalized,
        MiMoSettings settings,
        string apiKey,
        CancellationToken cancellationToken
    )
    {
        var profile = _memoryService.LoadProfile();
        var memories = _memoryService.LoadRelevantMemories(normalized, settings.MemoryRecallLimit);
        var recentTurns = _memoryService.LoadRecentTurns(settings.RecentTurnLimit);
        string systemPrompt = PromptBuilder.BuildSystemPrompt(profile, memories, settings.MaxReplyChars);

        try
        {
            string replyText = await _mimoApiClient.ChatAsync(
                settings,
                apiKey,
                systemPrompt,
                recentTurns,
                normalized,
                cancellationToken
            );

            if (replyText.Length > settings.MaxReplyChars)
                replyText = replyText[..settings.MaxReplyChars];

            _memoryService.RecordConversation(normalized, replyText, settings.RecentTurnLimit);
            string audioFilePath = "";
            if (settings.EnableTts)
            {
                try
                {
                    audioFilePath = await _mimoApiClient.SynthesizeAsync(settings, apiKey, replyText, cancellationToken);
                }
                catch
                {
                    audioFilePath = "";
                }
            }

            return new PetChatResult
            {
                Success = true,
                UserText = normalized,
                ReplyText = replyText,
                AudioFilePath = audioFilePath,
                SuggestedAction = PickAction(replyText)
            };
        }
        catch (OperationCanceledException)
        {
            return new PetChatResult
            {
                Success = false,
                ErrorMessage = "请求超时",
                ReplyText = "我刚刚想得有一点久，妈妈可以再叫我一次。",
                SuggestedAction = "surprised"
            };
        }
        catch (Exception ex)
        {
            return new PetChatResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReplyText = "我想了一下，但现在有一点点卡住啦。",
                SuggestedAction = "surprised"
            };
        }
    }

    private static string PickAction(string replyText)
    {
        if (replyText.Contains("晚安", StringComparison.Ordinal)
            || replyText.Contains("困", StringComparison.Ordinal)
            || replyText.Contains("睡", StringComparison.Ordinal))
        {
            return "sleep";
        }

        if (replyText.Contains("嘿嘿", StringComparison.Ordinal)
            || replyText.Contains("开心", StringComparison.Ordinal)
            || replyText.Contains("喜欢", StringComparison.Ordinal))
        {
            return "happy";
        }

        if (replyText.Contains("听", StringComparison.Ordinal)
            || replyText.Contains("说", StringComparison.Ordinal))
        {
            return "listen";
        }

        if (replyText.Contains("害羞", StringComparison.Ordinal))
            return "shy";

        return "cozy";
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch
        {
        }
    }
}
