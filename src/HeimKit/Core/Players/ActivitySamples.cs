using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players.Models;
using System;
using System.Collections.Generic;
using System.Linq;
namespace HeimKit.Core.Players;
// Bounded transition selection only. Shared activity diagnostics owns formatting and gating.
public sealed class ActivitySamples
{
    private readonly Dictionary<long, PlayerActivityView> _seen = new(), _pending = new();
    private readonly HashSet<long> _peers = new();
    private readonly List<long> _departed = new();
    private readonly List<PlayerActivityView> _chosen = new();
    private readonly List<PlayerActivityView> _ordered = new();
    private float _last = float.NegativeInfinity;
    public long TransitionsInterval { get; private set; }
    public long ReplacedPendingInterval { get; private set; }
    public int Pending => _pending.Count;
    public void Capture(float now, IEnumerable<PlayerActivityView> players)
    {
        if (now >= _last && now - _last < 0.5f) return;
        if (now < _last) Reset();
        _last = now;
        var peers = _peers; peers.Clear(); int visited = 0;
        foreach (var current in players)
        {
            if (visited++ >= 4096) break;
            peers.Add(current.Peer);
            bool changed = !_seen.TryGetValue(current.Peer, out var previous) || previous.Session != current.Session || previous.StateRevision != current.StateRevision;
            if (changed)
            {
                if (previous != null) TransitionsInterval += previous.Session != current.Session ? 1 : Math.Max(1, current.StateRevision - previous.StateRevision);
                Queue(current);
            }
            _seen[current.Peer] = Copy(current, previous);
        }
        _departed.Clear(); foreach (long peer in _seen.Keys) if (!peers.Contains(peer)) _departed.Add(peer);
        foreach (long peer in _departed)
        {
            var departed = _seen[peer]; departed.ObservedAt = now; departed.Known = false; departed.Alive = false; departed.Idle = false; departed.Reason = "disconnected_or_outside_observed_scope";
            TransitionsInterval++; Queue(departed); _seen.Remove(peer);
        }
    }
    private void Queue(PlayerActivityView current)
    {
        if (_pending.ContainsKey(current.Peer)) ReplacedPendingInterval++;
        if (_pending.Count < 4096 || _pending.ContainsKey(current.Peer)) _pending[current.Peer] = Copy(current);
        else ReplacedPendingInterval++;
    }
    public IReadOnlyList<PlayerActivityView> Drain(int maximum)
    {
        var chosen = _chosen; chosen.Clear(); _ordered.Clear();
        foreach (var item in _pending.Values) _ordered.Add(item);
        int count = Math.Min(Math.Max(0, maximum), _ordered.Count);
        // Four stable priority buckets preserve native enumeration ties without
        // the comparer/delegate wrappers created by runtime sorting helpers.
        for (int priority = 0; priority < 4 && chosen.Count < count; priority++)
            foreach (var item in _ordered)
                if ((item.Known ? 2 : 0) + (item.Idle ? 0 : 1) == priority)
                { chosen.Add(item); if (chosen.Count == count) break; }
        foreach (var item in chosen) _pending.Remove(item.Peer);
        return chosen;
    }
    public void IntervalReported() { TransitionsInterval = 0; ReplacedPendingInterval = 0; }
    public void Reset() { _seen.Clear(); _pending.Clear(); _ordered.Clear(); _chosen.Clear(); _last = float.NegativeInfinity; IntervalReported(); }
    private static PlayerActivityView Copy(PlayerActivityView p, PlayerActivityView? output = null)
    {
        output ??= new(); output.Peer = p.Peer; output.Name = p.Name; output.Session = p.Session; output.StateRevision = p.StateRevision;
        output.Known = p.Known; output.Alive = p.Alive; output.InBed = p.InBed; output.Idle = p.Idle;
        output.ObservedAt = p.ObservedAt; output.Reason = p.Reason; output.LastSignal = p.LastSignal;
        output.LastActivityAt = p.LastActivityAt; output.Recovery = p.Recovery; output.GraceRemaining = p.GraceRemaining; output.IdleSeconds = p.IdleSeconds;
        return output;
    }
}
