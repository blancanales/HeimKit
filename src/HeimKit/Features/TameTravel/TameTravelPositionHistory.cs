using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Collections.Generic;
using HeimKit.Features.TameTravel.Models;
namespace HeimKit.Features.TameTravel;

// Retain accepted owner updates between controller passes; never scan the world
// or perform ownership/placement work inside the native networking callback.
internal sealed class TameTravelPositionHistory
{
    public const int Capacity = 32;
    private readonly Queue<PositionObservation> _positions = new();
    private uint _revision;
    public int Count => _positions.Count;
    public bool Overflowed { get; private set; }
    public void Add(PositionObservation observation)
    {
        if (observation.Revision <= _revision) return;
        _revision = observation.Revision;
        if (_positions.Count == Capacity) { _positions.Clear(); Overflowed = true; }
        _positions.Enqueue(observation);
    }
    public bool Take(out PositionObservation observation)
    {
        if (_positions.Count == 0) { observation = default; return false; }
        observation = _positions.Dequeue(); return true;
    }
    public void AcknowledgeOverflow() => Overflowed = false;
}
