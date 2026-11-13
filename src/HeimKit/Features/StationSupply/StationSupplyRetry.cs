using System;
using System.Collections.Generic;
using HeimKit.Core.World;
using HeimKit.Features.StationSupply.Models;

namespace HeimKit.Features.StationSupply;

// Failed handoffs retain no inventory data. Any original/revision/connection or
// transport-readiness change invalidates the delay on the next discovery visit.
internal sealed class StationSupplyRetry
{
    internal const int Capacity = 256;
    internal readonly struct State : IEquatable<State>
    {
        private readonly ZDO _station, _chest;
        private readonly uint _stationData, _chestData;
        private readonly ushort _stationRevision, _chestRevision;
        private readonly long _stationOwner, _chestOwner;
        private readonly ZNetPeer? _stationPeer, _chestPeer;
        private readonly bool _stationReady, _chestReady;
        internal State(ZDO station, ZDO chest, ZNetPeer? stationPeer, ZNetPeer? chestPeer, bool stationReady, bool chestReady)
        {
            _station = station; _chest = chest; _stationData = station.DataRevision; _chestData = chest.DataRevision;
            _stationRevision = station.OwnerRevision; _chestRevision = chest.OwnerRevision;
            _stationOwner = station.GetOwner(); _chestOwner = chest.GetOwner();
            _stationPeer = stationPeer; _chestPeer = chestPeer; _stationReady = stationReady; _chestReady = chestReady;
        }
        public bool Equals(State other) => ReferenceEquals(_station, other._station) && ReferenceEquals(_chest, other._chest) &&
            _stationData == other._stationData && _chestData == other._chestData && _stationRevision == other._stationRevision && _chestRevision == other._chestRevision &&
            _stationOwner == other._stationOwner && _chestOwner == other._chestOwner && ReferenceEquals(_stationPeer, other._stationPeer) && ReferenceEquals(_chestPeer, other._chestPeer) &&
            _stationReady == other._stationReady && _chestReady == other._chestReady;
    }
    private readonly Dictionary<(ZDOID, SupplyIntake, ZDOID), (State State, float Next, int Failures, float FailedAt)> _entries = new();
    private readonly AccessOrder<(ZDOID, SupplyIntake, ZDOID)> _order = new();
    internal int Count => _entries.Count;
    internal bool Ready(ZDOID station, SupplyIntake intake, ZDOID chest, State state, float now, out float remaining)
    {
        remaining = 0; var key = (station, intake, chest);
        if (!_entries.TryGetValue(key, out var previous)) return true;
        _order.Touch(key);
        if (!previous.State.Equals(state) || now < previous.FailedAt)
        { _entries.Remove(key); return true; }
        remaining = Math.Max(0, previous.Next - now); return remaining == 0;
    }
    internal void Failed(ZDOID station, SupplyIntake intake, ZDOID chest, State state, float now)
    {
        var key = (station, intake, chest);
        // Ready already invalidated externally changed state. Our own acquire /
        // release revisions must not reset the escalating failure history.
        int failures = _entries.TryGetValue(key, out var previous) ? Math.Min(4, previous.Failures + 1) : 1;
        if (!_order.Touch(key))
        {
            if (_order.Count >= Capacity) _entries.Remove(_order.RemoveOldest());
            _order.Add(key);
        }
        _entries[key] = (state, now + Math.Min(30, 2 * (1 << failures)), failures, now);
    }
    internal void Clear() { _entries.Clear(); _order.Clear(); }
}
