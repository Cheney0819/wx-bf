using System.Text.Json;
using System.Text.RegularExpressions;
using DesktopPet.Wpf.Models;

namespace DesktopPet.Wpf.Services;

internal sealed class MemoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();

    public PetMemoryProfile LoadProfile()
    {
        lock (_sync)
        {
            return ReadJson(PetAiPaths.GetMemoryProfilePath(), new PetMemoryProfile());
        }
    }

    public IReadOnlyList<ConversationTurn> LoadRecentTurns(int limit)
    {
        lock (_sync)
        {
            var state = ReadJson(PetAiPaths.GetRecentSessionPath(), new RecentSessionState());
            return state.Turns
                .Where(turn => !string.IsNullOrWhiteSpace(turn.Role) && !string.IsNullOrWhiteSpace(turn.Text))
                .TakeLast(Math.Max(1, limit))
                .ToList();
        }
    }

    public IReadOnlyList<PetMemoryItem> LoadRelevantMemories(string userText, int limit)
    {
        lock (_sync)
        {
            var state = ReadJson(PetAiPaths.GetMemoriesPath(), new MemoryCollectionState());
            var keywords = ExtractKeywords(userText);

            var ranked = state.Items
                .Select(item => new
                {
                    Item = item,
                    Score = ComputeScore(item, keywords)
                })
                .OrderByDescending(entry => entry.Score)
                .ThenByDescending(entry => entry.Item.Importance)
                .ThenByDescending(entry => entry.Item.LastUsedAt)
                .Take(Math.Max(1, limit))
                .Select(entry =>
                {
                    entry.Item.LastUsedAt = DateTime.UtcNow.ToString("O");
                    return entry.Item;
                })
                .ToList();

            WriteJson(PetAiPaths.GetMemoriesPath(), state);
            return ranked;
        }
    }

    public void RecordConversation(string userText, string assistantText, int recentTurnLimit)
    {
        lock (_sync)
        {
            AppendRecentTurns(userText, assistantText, recentTurnLimit);
            ExtractAndPersistMemories(userText);
        }
    }

    private void AppendRecentTurns(string userText, string assistantText, int recentTurnLimit)
    {
        var path = PetAiPaths.GetRecentSessionPath();
        var state = ReadJson(path, new RecentSessionState());
        string now = DateTime.UtcNow.ToString("O");

        state.UpdatedAt = now;
        state.Turns.Add(new ConversationTurn
        {
            Role = "user",
            Text = userText.Trim(),
            CreatedAt = now
        });
        state.Turns.Add(new ConversationTurn
        {
            Role = "assistant",
            Text = assistantText.Trim(),
            CreatedAt = now
        });

        int maxTurnCount = Math.Max(2, recentTurnLimit * 2);
        if (state.Turns.Count > maxTurnCount)
            state.Turns = state.Turns.TakeLast(maxTurnCount).ToList();

        WriteJson(path, state);
    }

    private void ExtractAndPersistMemories(string userText)
    {
        string normalized = userText.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        string now = DateTime.UtcNow.ToString("O");
        var profilePath = PetAiPaths.GetMemoryProfilePath();
        var memoriesPath = PetAiPaths.GetMemoriesPath();
        var profile = ReadJson(profilePath, new PetMemoryProfile());
        var memoryState = ReadJson(memoriesPath, new MemoryCollectionState());
        bool profileChanged = false;

        var nameMatch = Regex.Match(normalized, @"(?:我叫|叫我)(.{1,12})");
        if (nameMatch.Success)
        {
            string preferredName = CleanFactValue(nameMatch.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                profile.PreferredName = preferredName;
                profile.UpdatedAt = now;
                profileChanged = true;
            }
        }

        if (normalized.Contains("别", StringComparison.Ordinal) || normalized.Contains("不要", StringComparison.Ordinal))
        {
            AddUniqueTopic(profile.ForbiddenTopics, TrimSentence(normalized, 28));
            profile.UpdatedAt = now;
            profileChanged = true;
            UpsertMemory(memoryState.Items, "boundary", $"用户不希望桌宠：{TrimSentence(normalized, 40)}", normalized, 0.95, now);
        }
        else if (normalized.Contains("喜欢", StringComparison.Ordinal)
                 || normalized.Contains("想要", StringComparison.Ordinal)
                 || normalized.Contains("希望", StringComparison.Ordinal))
        {
            UpsertMemory(memoryState.Items, "preference", $"用户偏好：{TrimSentence(normalized, 40)}", normalized, 0.8, now);
        }
        else if (normalized.Contains("记住", StringComparison.Ordinal)
                 || normalized.Contains("以后", StringComparison.Ordinal))
        {
            UpsertMemory(memoryState.Items, "fact", $"用户希望记住：{TrimSentence(normalized, 40)}", normalized, 0.88, now);
        }

        if (profileChanged)
            WriteJson(profilePath, profile);

        WriteJson(memoriesPath, memoryState);
    }

    private static void UpsertMemory(List<PetMemoryItem> items, string category, string summary, string sourceText, double importance, string now)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return;

        var existing = items.FirstOrDefault(item => string.Equals(item.Summary, summary, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.Importance = Math.Max(existing.Importance, importance);
            existing.LastUsedAt = now;
            return;
        }

        items.Add(new PetMemoryItem
        {
            Id = $"mem_{Guid.NewGuid():N}"[..16],
            Category = category,
            Summary = summary,
            Keywords = ExtractKeywords(sourceText),
            Importance = importance,
            CreatedAt = now,
            LastUsedAt = now
        });
    }

    private static double ComputeScore(PetMemoryItem item, List<string> keywords)
    {
        double score = item.Importance;
        if (keywords.Count == 0)
            return score;

        int matched = item.Keywords.Count(keyword => keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase));
        if (matched > 0)
            score += matched * 0.5;
        else if (keywords.Any(keyword => item.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            score += 0.25;

        return score;
    }

    private static List<string> ExtractKeywords(string text)
    {
        return Regex.Matches(text ?? "", @"[\p{L}\p{Nd}]{2,}")
            .Select(match => match.Value.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    private static string TrimSentence(string text, int maxLength)
    {
        string normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string CleanFactValue(string value)
    {
        return Regex.Replace(value, @"[，。！？,.!?\s]+$", "").Trim();
    }

    private static void AddUniqueTopic(List<string> topics, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (topics.Any(item => string.Equals(item, value, StringComparison.Ordinal)))
            return;

        topics.Add(value);
        if (topics.Count > 12)
            topics.RemoveRange(0, topics.Count - 12);
    }

    private static T ReadJson<T>(string path, T fallback) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return fallback;

            var parsed = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
            return parsed ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteJson<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    }

    private sealed class RecentSessionState
    {
        public string UpdatedAt { get; set; } = "";
        public List<ConversationTurn> Turns { get; set; } = new();
    }

    private sealed class MemoryCollectionState
    {
        public List<PetMemoryItem> Items { get; set; } = new();
    }
}
