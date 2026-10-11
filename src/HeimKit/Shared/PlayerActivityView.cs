namespace HeimKit.Shared;

public sealed class PlayerActivityView
{
    public long Peer { get; set; }
    public bool Known { get; set; }
    public bool Alive { get; set; }
    public bool InBed { get; set; }
    public bool Idle { get; set; }
    public float IdleSeconds { get; set; }
    public string Reason { get; set; } = "unknown";
    public long Session { get; set; }
    public long StateRevision { get; set; }
    public string LastSignal { get; set; } = "none_observed";
    public string Recovery { get; set; } = "not_observed";
    public float GraceRemaining { get; set; }
    public float ObservedAt { get; set; }
    public float LastActivityAt { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}
