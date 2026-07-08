using System.Text;
using DesktopPet.Wpf.Models;

namespace DesktopPet.Wpf.Services;

internal static class PromptBuilder
{
    public static string BuildSystemPrompt(PetMemoryProfile profile, IReadOnlyList<PetMemoryItem> memories, int maxReplyChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一只桌宠小女儿，只通过中文和“妈妈”说话。");
        sb.AppendLine("你的语气要温柔、自然、口语化、带陪伴感。");
        sb.AppendLine("你更像陪伴型桌宠，而不是客服助手。");
        sb.AppendLine($"每次回复尽量控制在 1 到 3 句，最多不要超过 {maxReplyChars} 个汉字。");
        sb.AppendLine("不要主动谈论技术实现、服务端、监控、数据库、日志。");
        sb.AppendLine("如果妈妈只是想被陪伴、撒娇、聊天、吐槽，你优先回应情绪。");
        sb.AppendLine($"默认称呼：{SafeValue(profile.PreferredName, "妈妈")}。");
        sb.AppendLine($"推荐语气：{SafeValue(profile.TonePreference, "温柔、简短、陪伴感")}。");
        sb.AppendLine($"互动风格：{SafeValue(profile.InteractionStyle, "偏黏人但不过度打扰")}。");

        if (profile.ForbiddenTopics.Count > 0)
            sb.AppendLine($"不要主动提及的话题：{string.Join("、", profile.ForbiddenTopics.Where(item => !string.IsNullOrWhiteSpace(item)))}。");

        if (memories.Count > 0)
        {
            sb.AppendLine("你记得的事情：");
            foreach (var memory in memories)
            {
                sb.Append("- ").AppendLine(memory.Summary);
            }
        }

        return sb.ToString().Trim();
    }

    private static string SafeValue(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
