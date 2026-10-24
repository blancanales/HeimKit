using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
namespace HeimKit.Core.Players.Models;

public sealed class PlayerActivityObservation
{
    internal ZDOID NativeCharacter;
    public long Peer { get; set; }
    public string Name { get; set; } = "";
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
