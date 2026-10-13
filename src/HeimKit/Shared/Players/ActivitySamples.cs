using HeimKit.Shared.Players.Models;
using System;
using System.Collections.Generic;
using System.Linq;
namespace HeimKit.Shared.Players;
// Bounded transition selection only. Shared activity diagnostics owns formatting and gating.
public sealed class ActivitySamples
{
    private readonly Dictionary<long, PlayerActivityView> _seen = new(), _pending = new();
    private float _last = float.NegativeInfinity;
    public long TransitionsInterval { get; private set; }
    public long ReplacedPendingInterval { get; private set; }
    public int Pending => _pending.Count;
    public void Capture(float now, IEnumerable<PlayerActivityView> players)
    {
        if (now >= _last && now - _last < 0.5f) return;
        if (now < _last) Reset();
        _last = now;
        var peers = new HashSet<long>();
        foreach (var current in players.Take(4096))
        {
            peers.Add(current.Peer);
            bool changed = !_seen.TryGetValue(current.Peer, out var previous) || previous.Session != current.Session || previous.StateRevision != current.StateRevision;
            if (changed)
            {
                if (previous != null) TransitionsInterval += previous.Session != current.Session ? 1 : Math.Max(1, current.StateRevision - previous.StateRevision);
                Queue(current);
            }
            _seen[current.Peer] = Copy(current);
        }
        foreach (long peer in _seen.Keys.Where(peer => !peers.Contains(peer)).ToArray())
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
        var chosen = _pending.Values.OrderBy(p => p.Known).ThenByDescending(p => p.Idle).Take(maximum).ToList();
        foreach (var item in chosen) _pending.Remove(item.Peer);
        return chosen;
    }
    public void IntervalReported() { TransitionsInterval = 0; ReplacedPendingInterval = 0; }
    public void Reset() { _seen.Clear(); _pending.Clear(); _last = float.NegativeInfinity; IntervalReported(); }
    private static PlayerActivityView Copy(PlayerActivityView p) => new()
    { Peer = p.Peer, Name = p.Name, Session = p.Session, StateRevision = p.StateRevision, Known = p.Known, Alive = p.Alive, InBed = p.InBed, Idle = p.Idle,
      ObservedAt = p.ObservedAt, Reason = p.Reason, LastSignal = p.LastSignal, LastActivityAt = p.LastActivityAt, Recovery = p.Recovery, GraceRemaining = p.GraceRemaining, IdleSeconds = p.IdleSeconds };
}
