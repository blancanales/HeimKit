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
