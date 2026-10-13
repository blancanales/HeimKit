namespace HeimKit.Features.WeatherGuard.Models;

public sealed class ProtectionObservation
{
    public System.Collections.Generic.Dictionary<string, object?> Identity { get; set; } = new();
    public bool Protected { get; set; }
    public bool RainProtection { get; set; } = true;
    public bool UnderwaterProtection { get; set; } = true;
    public string Reason { get; set; } = "";
    public string Origin { get; set; } = "player_built";
}
