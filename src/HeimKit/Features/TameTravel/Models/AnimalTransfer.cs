using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using UnityEngine;
namespace HeimKit.Features.TameTravel.Models;
internal sealed class AnimalTransfer : OwnershipLease
{
    public PortalJourney Journey = null!;
    public override ZNetPeer DestinationPeer => Journey.Peer;
    // 0 drains source; 1 waits for placement; 2 drains destination; 3 awaits new-owner state; 4 confirmed.
    public float Started, PhaseTime;
    public int PlacementCursor;
    public Vector3? PlacementAnchor;
    public int PlacementAttempts, PlacementCandidates;
    public Vector3? PlacementLastProbe;
    public float? PlacementSupportHeight;
    public float? PlacementAccessWarmupUntil;
    public double PlacementContextMilliseconds, PlacementSearchMilliseconds;
    // One final reason per rejected candidate; walk-out steps and pass wait reasons are counted separately.
    public readonly System.Collections.Generic.Dictionary<string, int> PlacementRejections = new();
    public readonly System.Collections.Generic.Dictionary<string, int> PlacementWalkoutRejections = new();
    public readonly System.Collections.Generic.Dictionary<string, int> PlacementWaitReasons = new();
    // Up to sixteen named pieces or layers behind unsupported_surface, building_overhead and solid_overlap rejections.
    public readonly System.Collections.Generic.Dictionary<string, int> PlacementSurfaceBlocks = new();
    public readonly System.Collections.Generic.Dictionary<string, int> PlacementOverlapHits = new();
    public Vector3? PlacementTraveller;
    public bool Waiting;
    public bool PlacementStarted;
    public bool FollowContinues;
    // A spacing lease drains through phase 0/1, runs in phase 5 and pauses in phase 6.
    // It never moves an animal or uses the destination-owner phases 2..4.
}
