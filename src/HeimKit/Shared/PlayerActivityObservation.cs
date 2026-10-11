namespace HeimKit.Shared;

public sealed class PlayerActivityObservation
{
    public long Peer { get; set; }
    public string Character { get; set; } = "";
    public bool Known { get; set; }
    public bool Alive { get; set; }
    public bool InBed { get; set; }
    public bool Busy { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Heading { get; set; }
    public float Health { get; set; }
    public string Equipment { get; set; } = "";
    public int Emote { get; set; }
}
