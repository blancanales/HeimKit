using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Collections.Generic;
using UnityEngine;

namespace HeimKit.Features.TameTravel.Models;

internal sealed class PortalJourney
{
    public ZNetPeer Peer = null!;
    public ZDOID Character;
    public string Name = "";
    public ZDOID Source, Destination;
    public Vector3 SourcePosition, DestinationPosition, Exit;
    public Quaternion SourceRotation, DestinationRotation;
    public string Tag = "";
    public float Entered, Arrived = -1, Barrier;
    public bool Ready;
    public string? EndReason;
    public readonly Queue<ZDOID> Followers = new();
    public readonly HashSet<ZDOID> Waiting = new();
    public int Moved, Confirmed;
}
