using UnityEngine;
namespace HeimKit.Features.TameTravel.Models;
internal sealed class AnimalTransfer
{
    public ZDO Record = null!;
    public ZDOID Id;
    public long PreviousOwner;
    public ZNetPeer? PreviousPeer;
    public PortalJourney Journey = null!;
    // 0 drains source; 1 waits for placement; 2 drains destination; 3 awaits new-owner state; 4 confirmed.
    public int Phase;
    public float Started, PhaseTime, Barrier;
    public Vector3 Source, Placement;
    public uint PublishedRevision;
    public ushort PublishedOwnerRevision;
    public ushort SourceOwnerRevision;
    public float SourceEchoTime;
    public bool SourceStateObserved;
    public bool Moved;
    public bool OwnerObserved;
    public bool Waiting;
    // A spacing lease drains through phase 0/1, runs in phase 5 and pauses in phase 6.
    // It never moves an animal or uses the destination-owner phases 2..4.
    public bool FollowControl;
}
