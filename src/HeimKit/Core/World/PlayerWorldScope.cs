using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit.Core.World;

// Actor observations reuse Plugin's census. One rotating actor's sectors per pass.
internal sealed class PlayerWorldScope : IDisposable
{
    private readonly IPlayerPresence _presence;
    internal string Owner { get; }
    private int _cursor;
    private readonly Dictionary<long, Vector3> _previous = new();
    private readonly Dictionary<long, Vector3> _current = new();
    private readonly List<(Vector3 Before, Vector3 After)> _segments = new();
    public readonly List<Vector3> Positions = new();
    public readonly List<ZDO> Records = new();
    private readonly List<ZDO> _context = new();
    private CandidateSubscription? _index;
    private bool _contextQueried;
    private float _range;
    private bool _actorsReliable;
    public void Index(Func<ZDO, bool> accept) => _index = WorldObjectProvider.Current.Subscribe(Owner, accept);
    public bool ContextReady()
    {
        if (_contextQueried) return _context.Count <= 8192;
        _contextQueried = true; _context.Clear();
        WorldObjectProvider.Current.Query(ZoneSystem.GetZone(Center), new SimulationDistance((int)Math.Ceiling(_range / 64) + 1, 0), _context);
        return _context.Count <= 8192;
    }
    public List<ZDO> Context => _context;
    public Vector3 Center { get; private set; }
    public PlayerWorldScope(IPlayerPresence presence, string owner = nameof(PlayerWorldScope)) { _presence = presence; Owner = owner; }
    public static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);
    public static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    public bool Refresh(float range)
    {
        if (!SampleActors()) return false;
        _range = range;
        if (_index != null) _index.Read(Center, (int)Math.Ceiling(range / 64) + 1, Records);
        else WorldObjectProvider.Current.Query(ZoneSystem.GetZone(Center), new SimulationDistance((int)Math.Ceiling(range / 64) + 1, 0), Records);
        if (Records.Count > 8192) { Records.Clear(); return false; }
        return true;
    }
    public bool SampleActors()
    {
        _contextQueried = false; _context.Clear();
        Positions.Clear(); Records.Clear(); _segments.Clear();
        var current = _current; current.Clear();
        _actorsReliable = false;
        if (_presence.HasJoinedPlayers != true) return false;
        if (!ReferenceEquals(this, WorldObjectProvider.Current.Scope))
        {
            WorldObjectProvider.Current.SamplePlayers();
            var shared = WorldObjectProvider.Current.Scope;
            if (!shared._actorsReliable) return false;
            foreach (var pair in shared._current)
            {
                Positions.Add(pair.Value); current[pair.Key] = pair.Value;
                if (_previous.TryGetValue(pair.Key, out var before)) _segments.Add((before, pair.Value));
            }
            _previous.Clear(); foreach (var pair in current) _previous[pair.Key] = pair.Value;
            if (Positions.Count == 0) return false;
            Center = Positions[_cursor++ % Positions.Count]; if (_cursor >= 1000000) _cursor = 0;
            return !Character.InInterior(Center);
        }
        for (int rosterIndex1 = 0; rosterIndex1 < _presence.Peers.Count && _presence.Peers[rosterIndex1] is var peer; rosterIndex1++)
        {
            if (!peer.IsReady()) continue;
            if (peer.m_characterID.IsNone()) return false;
            var actor = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (actor == null || !actor.IsValid() || actor.GetOwner() != peer.m_uid) return false;
            bool savedHealth = actor.GetFloat(ZDOVars.s_health, out float health);
            var position = actor.GetPosition();
            if (savedHealth && !Finite(health) || !Finite(position)) return false;
            if (!NativeCharacterHealth.IsAlive(actor)) continue;
            if (Positions.Count >= 64) return false;
            Positions.Add(position);
            if (_previous.TryGetValue(peer.m_uid, out var previous)) _segments.Add((previous, position));
            current[peer.m_uid] = position;
        }
        _previous.Clear(); foreach (var pair in current) _previous[pair.Key] = pair.Value;
        if (Positions.Count == 0) return false;
        _actorsReliable = true;
        Center = Positions[_cursor++ % Positions.Count];
        if (_cursor >= 1000000) _cursor = 0;
        if (Character.InInterior(Center)) return false;
        return true;
    }
    public bool InRange(Vector3 point, float range) => Finite(point) && (point - Center).sqrMagnitude <= range * range;
    public bool PlayerNear(Vector3 point, float distance)
    {
        foreach (var position in Positions) if ((position - point).sqrMagnitude <= distance * distance) return true;
        foreach (var segment in _segments)
        {
            var line = segment.After - segment.Before;
            float t = line.sqrMagnitude > 0 ? Mathf.Clamp01(Vector3.Dot(point - segment.Before, line) / line.sqrMagnitude) : 0;
            if ((point - (segment.Before + line * t)).sqrMagnitude <= distance * distance) return true;
        }
        return false;
    }
    public void ClearActors() { _actorsReliable = false; Positions.Clear(); Records.Clear(); _previous.Clear(); _current.Clear(); _segments.Clear(); }
    public void Dispose() { _index?.Dispose(); _index = null; WorldObjectProvider.Current?.ReleaseScope(this); ClearActors(); }
}
