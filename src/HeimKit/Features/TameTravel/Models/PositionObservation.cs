using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using UnityEngine;
namespace HeimKit.Features.TameTravel.Models;
internal readonly struct PositionObservation
{
    public readonly Vector3 Position;
    public readonly float Time;
    public readonly uint Revision;
    public PositionObservation(Vector3 position, float time, uint revision) { Position = position; Time = time; Revision = revision; }
}
