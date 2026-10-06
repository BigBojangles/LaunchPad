namespace LaunchPad.Models;

public sealed class KnownProject
{
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalFields { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? DisplayName { get; set; }
    public Dictionary<string, string> SessionNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

