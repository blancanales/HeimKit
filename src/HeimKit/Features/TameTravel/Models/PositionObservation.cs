using UnityEngine;
namespace HeimKit.Features.TameTravel.Models;
internal readonly struct PositionObservation
{
    public readonly Vector3 Position;
    public readonly float Time;
    public readonly uint Revision;
    public PositionObservation(Vector3 position, float time, uint revision) { Position = position; Time = time; Revision = revision; }
}
