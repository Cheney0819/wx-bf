namespace DesktopPet.Wpf.Models;

public sealed class PetMemoryItem
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
    public double Importance { get; set; }
    public string CreatedAt { get; set; } = "";
    public string LastUsedAt { get; set; } = "";
}
