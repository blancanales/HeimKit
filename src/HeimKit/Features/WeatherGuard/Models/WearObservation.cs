namespace HeimKit.Features.WeatherGuard.Models;

public sealed class WearObservation
{
    public string Object { get; set; } = "";
    public bool Protected { get; set; }
    public bool Owner { get; set; }
    public bool Warmup { get; set; }
    public bool GateReached { get; set; }
    public bool Suppressed { get; set; }
    public float Before { get; set; }
    public string Context { get; set; } = "";
}
