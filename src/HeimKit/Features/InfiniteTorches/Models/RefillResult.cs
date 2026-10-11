namespace HeimKit.Features.InfiniteTorches.Models;

public sealed class RefillResult
{
    public string Object { get; set; } = "";
    public string Outcome { get; set; } = "";
    public long RequestedOwner { get; set; }
    public long? ObservedOwner { get; set; }
    public float Before { get; set; }
    public float Target { get; set; }
    public float? Fuel { get; set; }
    public float WaitSeconds { get; set; }
}
