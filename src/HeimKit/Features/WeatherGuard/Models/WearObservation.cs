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
