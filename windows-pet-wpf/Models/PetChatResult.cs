namespace DesktopPet.Wpf.Models;

public sealed class PetChatResult
{
    public bool Success { get; set; }
    public string UserText { get; set; } = "";
    public string ReplyText { get; set; } = "";
    public string AudioFilePath { get; set; } = "";
    public string SuggestedAction { get; set; } = "listen";
    public string ErrorMessage { get; set; } = "";
}
