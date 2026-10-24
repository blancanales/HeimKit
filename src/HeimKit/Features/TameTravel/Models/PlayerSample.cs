using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using UnityEngine;
namespace HeimKit.Features.TameTravel.Models;
internal sealed class PlayerSample
{
    public ZNetPeer Peer = null!;
    public ZDOID Character;
    public Vector3 Position;
    public float Time, CommandTime = -10, BlockedUntil;
    public int Emote;
    public bool PlayerDataObserved;
    public readonly TameTravelPositionHistory Positions = new();
    public PortalJourney? Journey;
    public ZDOID ReentryPortal;
    public PortalWitness? PendingPortal;
}
