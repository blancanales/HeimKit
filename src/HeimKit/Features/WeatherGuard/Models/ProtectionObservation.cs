using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
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
