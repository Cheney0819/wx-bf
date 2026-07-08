namespace DesktopPet.Wpf.Models;

public sealed class PetMemoryProfile
{
    public string PreferredName { get; set; } = "妈妈";
    public string TonePreference { get; set; } = "温柔、简短、陪伴感";
    public string InteractionStyle { get; set; } = "偏黏人但不过度打扰";
    public List<string> ForbiddenTopics { get; set; } = new();
    public string UpdatedAt { get; set; } = "";
}
