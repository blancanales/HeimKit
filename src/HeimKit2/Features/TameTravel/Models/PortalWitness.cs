using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using UnityEngine;

namespace HeimKit.Features.TameTravel.Models;

internal sealed class PortalWitness
{
    public ZDO Source = null!;
    public ZDOID SourceId;
    public ZDOID Destination;
    public string Tag = "";
    public Vector3 Position;
    public Quaternion Rotation;
    public PositionObservation Observation;
}
