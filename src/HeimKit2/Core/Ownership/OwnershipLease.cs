using UnityEngine;

namespace HeimKit.Core.Ownership;

// Shared native transaction state; feature journey/selection state stays with its owner.
internal abstract class OwnershipLease
{
    public ZDO Record = null!;
    public ZDOID Id;
    public long PreviousOwner;
    public ZNetPeer? PreviousPeer;
    public abstract ZNetPeer DestinationPeer { get; }
    public int Phase;
    public float Barrier;
    public Vector3 Source, Placement;
    public uint PublishedRevision;
    public ushort PublishedOwnerRevision, SourceOwnerRevision;
    public float SourceEchoTime;
    public bool SourceStateObserved, Moved, OwnerObserved;
    public float NextDestinationSend;
    public int DestinationRetries;
    public bool FollowControl;
}
