using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.WeatherGuard.Models;

public sealed class WearObservation
{
    public System.Collections.Generic.Dictionary<string, object?> Fields { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, object?> Identity { get; set; } = new();
    public bool Protected { get; set; }
    public bool Owner { get; set; }
    public bool Warmup { get; set; }
    public bool GateReached { get; set; }
    public bool Suppressed { get; set; }
    public float Before { get; set; }
}
