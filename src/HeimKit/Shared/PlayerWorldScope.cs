using System;
using System.Collections.Generic;
using HeimKit.Shared.Players;
using UnityEngine;

namespace HeimKit.Shared;

// Actor observations reuse Plugin's census. One rotating actor's sectors per pass.
internal sealed class PlayerWorldScope : IDisposable
{
    private readonly IPlayerPresence _presence;
    private int _cursor;
    private readonly Dictionary<long, Vector3> _previous = new();
    private readonly Dictionary<long, Vector3> _current = new();
    private readonly List<(Vector3 Before, Vector3 After)> _segments = new();
    public readonly List<Vector3> Positions = new();
    public readonly List<ZDO> Records = new();
    private readonly List<ZDO> _context = new();
    private WorldCandidateIndex? _index;
    private bool _contextQueried;
    private float _range;
    public void Index(Func<ZDO, bool> accept) => _index = new(accept);
    public bool ContextReady()
    {
        if (_contextQueried) return _context.Count <= 8192;
        _contextQueried = true; _context.Clear();
        ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(Center), new SimulationDistance((int)Math.Ceiling(_range / 64) + 1, 0), _context);
        return _context.Count <= 8192;
    }
    public List<ZDO> Context => _context;
    public Vector3 Center { get; private set; }
    public PlayerWorldScope(IPlayerPresence presence) => _presence = presence;
    public static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);
    public static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    public bool Refresh(float range)
    {
        if (!SampleActors()) return false;
        _range = range;
        if (_index != null) _index.Read(Center, (int)Math.Ceiling(range / 64) + 1, Records);
        else ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(Center), new SimulationDistance((int)Math.Ceiling(range / 64) + 1, 0), Records);
        if (Records.Count > 8192) { Records.Clear(); return false; }
        return true;
    }
    public bool SampleActors()
    {
        _contextQueried = false; _context.Clear();
        Positions.Clear(); Records.Clear(); _segments.Clear();
        var current = _current; current.Clear();
        if (_presence.HasJoinedPlayers != true) return false;
        foreach (var peer in _presence.Peers)
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
    public void Dispose() { _index?.Dispose(); _index = null; }
}
