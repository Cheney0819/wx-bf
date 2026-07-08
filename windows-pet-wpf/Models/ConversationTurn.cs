namespace DesktopPet.Wpf.Models;

public sealed class ConversationTurn
{
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}
