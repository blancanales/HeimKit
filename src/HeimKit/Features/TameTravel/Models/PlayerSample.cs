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
}
