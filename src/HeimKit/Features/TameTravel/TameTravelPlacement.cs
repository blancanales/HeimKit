using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HeimKit.Features.TameTravel.Models;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelPlacement : IDisposable
{
    private static readonly Vector3[] FootprintOffsets = { Vector3.zero, Vector3.right, -Vector3.right, Vector3.forward, -Vector3.forward,
        new(.70710678f, 0, .70710678f), new(-.70710678f, 0, .70710678f), new(.70710678f, 0, -.70710678f), new(-.70710678f, 0, -.70710678f) };
    private static readonly Action<Character> ResetGround = NativeAccess.Method<Action<Character>>(typeof(Character), "ResetGroundContact");
    private static readonly AccessTools.FieldRef<Character, float> MaxAltitude = NativeAccess.Field<Character, float>("m_maxAirAltitude");
    private static readonly AccessTools.FieldRef<Character, float> LastGroundTouch = NativeAccess.Field<Character, float>("m_lastGroundTouch");
    private static readonly AccessTools.FieldRef<Character, float> FallTimer = NativeAccess.Field<Character, float>("m_fallTimer");
    private static readonly AccessTools.FieldRef<BaseAI, List<Vector3>> Path = NativeAccess.Field<BaseAI, List<Vector3>>("m_path");
    private static readonly AccessTools.FieldRef<BaseAI, Vector3> SpawnPoint = NativeAccess.Field<BaseAI, Vector3>("m_spawnPoint");
    private readonly TerrainContext _context = new();
    private readonly TameTravelPlacementSurfaces _surfaces = new();
    private readonly TameTravelPlacementAccess _access = new();
    private readonly TameTravelRules _rules;
    private readonly List<(Vector3 Position, float Clearance)> _creatures = new();
    private readonly List<Vector3> _players = new(), _placements = new();
    public string WaitReason { get; private set; } = "no_safe_ground_position";
    public ZDOID? BlockingObject { get; private set; }
    private readonly int _solid = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle", "character", "character_net", "character_ghost");
    private readonly int _building = LayerMask.GetMask("piece");
    // Padding applies to buildings and objects, not to terrain or creatures, which have their own checks.
    private readonly int _obstacles = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "vehicle");
    private readonly Collider[] _overlaps = new Collider[32];
    private CapsuleCollider? _padding;
    private readonly Func<Vector3, Vector3?> _groundProbe;
    private readonly Func<Vector3, Vector3, bool> _clearProbe;
    private AnimalTransfer _probeTransfer = null!;
    private bool _probeEvidence;
    private Vector3 _probeCenterOffset, _probeAxis;
    private Quaternion _probeRotation;
    private int _probeDirection;
    private float _probeExtent, _probeHorizontalExtent, _probeHalfLine, _probeRadius;
    public TameTravelPlacement(TameTravelRules rules) { _rules = rules; _groundProbe = ProbeGround; _clearProbe = ProbeClear; }
    public bool Find(AnimalTransfer t, Dictionary<ZDOID, AnimalTransfer>.ValueCollection transfers, IReadOnlyList<ZNetPeer> peers, float now, bool evidence, out Vector3 position)
    {
        long started = evidence ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        double contextBefore = t.PlacementContextMilliseconds;
        try { return FindPosition(t, transfers, peers, now, evidence, out position); }
        finally
        {
            if (evidence)
            {
                t.PlacementAttempts++;
                t.PlacementSearchMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency -
                    (t.PlacementContextMilliseconds - contextBefore);
                if (WaitReason != "placed") Reject(t, WaitReason);
            }
        }
    }
    private bool FindPosition(AnimalTransfer t, Dictionary<ZDOID, AnimalTransfer>.ValueCollection transfers, IReadOnlyList<ZNetPeer> peers, float now, bool evidence, out Vector3 position)
    {
        position = default; WaitReason = "no_safe_ground_position"; BlockingObject = null;
        long contextStarted = evidence ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            var records = TameTravelRules.Nearby(t.Journey.Exit);
            if (records == null) { WaitReason = "destination_scope_over_capacity"; return false; }
            if (!_context.Prepare(t.Journey.Exit, records, now, TameTravelPlacementRules.ContextRadius))
            { WaitReason = _context.WaitReason ?? "destination_terrain_unready"; BlockingObject = _context.WaitObject; return false; }
            bool ready = true;
            var creatures = _creatures; creatures.Clear();
            _players.Clear(); _placements.Clear();
            foreach (var z in records)
            {
                if (z.m_uid == t.Id) continue;
                var prefab = PrefabRegistry.Current.Prefab(z.GetPrefab());
                if (prefab == null) { WaitReason = "destination_prefab_unavailable"; BlockingObject = z.m_uid; return false; }
                // Retain saved context without changing its ownership or spawning duplicate records.
                if (PrefabRegistry.Component<Character>(prefab) != null) { creatures.Add((z.GetPosition(), BodyClearance(z))); continue; }
                if (!_context.Include(z, now)) { WaitReason = "destination_context_capacity"; BlockingObject = z.m_uid; return false; }
                bool solid = PrefabRegistry.Current.Solid(z.GetPrefab());
                if (solid && ZNetScene.instance.FindInstance(z) == null) { ready = false; BlockingObject = z.m_uid; }
            }
            if (!ready) { WaitReason = "destination_collision_instances_unready"; return false; }
            Physics.SyncTransforms();
        }
        finally
        {
            if (evidence) t.PlacementContextMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - contextStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        }
        if (!TameTravelRules.Scale(t.Record, out var scale)) return false;
        var capsule = PrefabRegistry.Component<CapsuleCollider>(_rules.Prefab(t.Record))!;
        float radius = capsule.radius * scale.x;
        float halfLine = Mathf.Max(0, capsule.height * scale.x * 0.5f - radius);
        Vector3 axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
        axis = t.Journey.DestinationRotation * axis;
        Vector3 centerOffset = t.Journey.DestinationRotation * (capsule.center * scale.x);
        float extent = radius + halfLine * Math.Abs(axis.y);
        float horizontalExtent = radius + halfLine * new Vector2(axis.x, axis.z).magnitude;
        var forward = t.Journey.DestinationRotation * Vector3.forward;
        forward.y = 0;
        if (forward.sqrMagnitude < .001f) { WaitReason = "destination_facing_unsupported"; return false; }
        forward = forward.normalized;
        _probeTransfer = t; _probeEvidence = evidence; _probeExtent = extent; _probeCenterOffset = centerOffset; _probeHorizontalExtent = horizontalExtent; _probeAxis = axis; _probeHalfLine = halfLine; _probeRadius = radius;
        _probeRotation = t.Journey.DestinationRotation; _probeDirection = capsule.direction;
        for (int index = 0; index < peers.Count && peers[index] is var peer; index++)
            if (peer.IsReady() && ZDOMan.instance.GetZDO(peer.m_characterID) is ZDO player) _players.Add(player.GetPosition());
        foreach (var other in transfers) if (!ReferenceEquals(other, t) && other.Moved) _placements.Add(other.Placement);
        // Delegates and obstacle snapshots are shared by every candidate in this search.
        Func<Vector3, Vector3?> groundProbe = _groundProbe;
        Func<Vector3, Vector3, bool> clearProbe = _clearProbe;
        int escapeProbes = 0, accessProbes = 0;
        var traveller = ZDOMan.instance.GetZDO(t.Journey.Character);
        if (traveller == null) { WaitReason = "traveller_unavailable"; return false; }
        // The sweep is centred where the traveller stands; once they move a metre it starts again beside them.
        var standing = traveller.GetPosition();
        if (!t.PlacementAnchor.HasValue || (standing - t.PlacementAnchor.Value).sqrMagnitude > 1) { t.PlacementAnchor = standing; t.PlacementCursor = 0; }
        var anchor = t.PlacementAnchor.Value;
        int count = TameTravelPlacementRules.CandidateCount(horizontalExtent);
        int start = t.PlacementCursor % count;
        for (int visited = 0; visited < TameTravelPlacementRules.MaximumCandidatesPerPass; visited++)
        {
            int index = (start + visited) % count;
            t.PlacementCursor = (index + 1) % count;
            if (evidence) t.PlacementCandidates++;
            var candidate = TameTravelPlacementRules.Candidate(index, horizontalExtent, t.Journey.Exit, anchor, forward);
            if (evidence) { t.PlacementLastProbe = candidate; t.PlacementSupportHeight = null; }
            var point = ProbeLanding(candidate);
            if (!point.HasValue) continue;
            var direction = point.Value - anchor; direction.y = 0;
            if (direction.sqrMagnitude < .001f) direction = forward;
            if (evidence) t.PlacementSupportHeight = point.Value.y - extent + centerOffset.y - .15f;
            if (TameTravelPlacementRules.HasEscape(point.Value, direction, Math.Max(3, horizontalExtent + 2), groundProbe, clearProbe, ref escapeProbes))
            {
                accessProbes++;
                // Navigation targets stand on the support surface, not at an offset animal root.
                var support = point.Value - Vector3.up * (extent - centerOffset.y + .15f);
                t.PlacementAccessWarmupUntil ??= now + 4;
                if (_access.Reachable(traveller.GetPosition(), support, out string accessReason))
                { position = point.Value; WaitReason = "placed"; return true; }
                if (evidence) Reject(t, accessReason);
                WaitReason = accessReason;
                if (accessReason == "access_path_unready_or_blocked" && now < t.PlacementAccessWarmupUntil.Value)
                { t.PlacementCursor = index; return false; }
                if (accessProbes >= 4) return false;
                continue;
            }
            WaitReason = "destination_escape_route_blocked";
            if (escapeProbes >= TameTravelPlacementRules.MaximumEscapeProbes)
            {
                WaitReason = "destination_escape_search_deferred";
                return false;
            }
        }
        return false;
    }

    // A landing that crowds a building or object is pushed clear by the measured overlap and checked again.
    private Vector3? ProbeLanding(Vector3 candidate)
    {
        for (int push = 0; ; push++)
        {
            var delta = candidate - _probeTransfer.Journey.Exit;
            if (delta.x * delta.x + delta.z * delta.z > TameTravelPlacementRules.LandingRadius * TameTravelPlacementRules.LandingRadius)
            { if (_probeEvidence) Reject(_probeTransfer, "landing_range"); return null; }
            var point = ProbeGround(candidate);
            if (!point.HasValue) return null;
            if (!Crowded(point.Value, out var away)) return point;
            if (_probeEvidence) Reject(_probeTransfer, "obstacle_padding");
            if (push == TameTravelPlacementRules.MaximumPushes || away.sqrMagnitude < .0001f) return null;
            candidate = point.Value + away.normalized * (away.magnitude + .05f);
            if (_probeEvidence) _probeTransfer.PlacementLastProbe = candidate;
        }
    }
    // The padded body is raised so its underside stays where the unpadded body's was: only its sides and top grow.
    // Overlapping objects report the direction and distance that clear them; only the horizontal part is used.
    private bool Crowded(Vector3 root, out Vector3 away)
    {
        away = Vector3.zero;
        float padding = TameTravelPlacementRules.ObstaclePadding, radius = _probeRadius + padding;
        var center = root + _probeCenterOffset + Vector3.up * (padding - .05f);
        int count = Physics.OverlapCapsuleNonAlloc(center + _probeAxis * _probeHalfLine, center - _probeAxis * _probeHalfLine, radius,
            _overlaps, _obstacles, QueryTriggerInteraction.Ignore);
        if (count == 0) return false;
        if (count == _overlaps.Length) return true;
        var probe = PaddingProbe();
        probe.radius = radius; probe.height = 2 * (_probeHalfLine + radius); probe.direction = _probeDirection;
        // Penetration needs an enabled collider; it stays enabled only for these synchronous queries.
        probe.enabled = true;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var other = _overlaps[i]; _overlaps[i] = null!;
                if (other != null && Physics.ComputePenetration(probe, center, _probeRotation, other, other.transform.position, other.transform.rotation, out var direction, out float distance))
                    away += direction * distance;
            }
        }
        finally { probe.enabled = false; }
        away.y = 0;
        return true;
    }
    private CapsuleCollider PaddingProbe()
    {
        if (_padding != null) return _padding;
        var probe = new GameObject("HeimKit TameTravel padding probe") { hideFlags = HideFlags.HideAndDontSave, layer = 2 };
        _padding = probe.AddComponent<CapsuleCollider>();
        _padding.isTrigger = true; _padding.enabled = false; _padding.center = Vector3.zero;
        return _padding;
    }
    private Vector3? ProbeGround(Vector3 p)
        {
            Vector3? Rejected(string reason) { if (_probeEvidence) Reject(_probeTransfer, reason); return null; }
            if (!_surfaces.Probe(p, out float height, out var normal, out string surfaceReason)) return Rejected(surfaceReason);
            if (Math.Abs(height - _probeTransfer.Journey.Exit.y) > 24) return Rejected("vertical_range");
            p.y = height + _probeExtent - _probeCenterOffset.y + .15f;
            if (Physics.Raycast(new Vector3(p.x, height + .1f, p.z), Vector3.up, 64, _building, QueryTriggerInteraction.Ignore)) return Rejected("building_overhead");
            if ((p - _probeTransfer.Journey.Exit).sqrMagnitude > TameTravelPlacementRules.ContextRadius * TameTravelPlacementRules.ContextRadius) return Rejected("walkout_range");
            // Cardinal and diagonal support samples cover the complete conservative footprint.
            foreach (var offset in FootprintOffsets)
                if (!_surfaces.Probe(p + offset * _probeHorizontalExtent, out float sample, out _, out _) ||
                    !TameTravelPlacementRules.SupportedFootprint(height, sample, offset * _probeHorizontalExtent, normal)) return Rejected("footprint_support");
            foreach (var player in _players)
                if ((player - p).sqrMagnitude < (_probeHorizontalExtent + 1.5f) * (_probeHorizontalExtent + 1.5f)) return Rejected("player_clearance");
            foreach (var other in _placements)
                if ((other - p).sqrMagnitude < (_probeHorizontalExtent + 6) * (_probeHorizontalExtent + 6)) return Rejected("follower_clearance");
            foreach (var creature in _creatures)
                if ((creature.Position - p).sqrMagnitude < (_probeHorizontalExtent + creature.Clearance) * (_probeHorizontalExtent + creature.Clearance)) return Rejected("creature_clearance");
            var center = p + _probeCenterOffset;
            return Physics.CheckCapsule(center + _probeAxis * _probeHalfLine, center - _probeAxis * _probeHalfLine, _probeRadius + .05f, _solid, QueryTriggerInteraction.Ignore) ? Rejected("solid_overlap") : p;
        }
    private bool ProbeClear(Vector3 before, Vector3 after)
        {
            var center = before + _probeCenterOffset; var delta = after - before;
            bool clear = !Physics.CapsuleCast(center + _probeAxis * _probeHalfLine, center - _probeAxis * _probeHalfLine, _probeRadius + .05f,
                delta.normalized, out _, delta.magnitude, _solid, QueryTriggerInteraction.Ignore);
            if (!clear && _probeEvidence) Reject(_probeTransfer, "walkout_collision");
            return clear;
        }
    private static void Reject(AnimalTransfer t, string reason) =>
        t.PlacementRejections[reason] = t.PlacementRejections.TryGetValue(reason, out int count) ? count + 1 : 1;

    public static void Move(AnimalTransfer t, Vector3 position)
    {
        var z = t.Record;
        Vector3 delta = position - z.GetPosition();
        z.Set(ZDOVars.s_spawnPoint, z.GetVec3(ZDOVars.s_spawnPoint, z.GetPosition()) + delta);
        if (z.GetBool(ZDOVars.s_patrol)) z.Set(ZDOVars.s_patrolPoint, z.GetVec3(ZDOVars.s_patrolPoint, z.GetPosition()) + delta);
        z.SetPosition(position); z.SetRotation(t.Journey.DestinationRotation);
        z.Set(ZDOVars.s_velHash, Vector3.zero); z.Set(ZDOVars.s_bodyVelHash, Vector3.zero); z.Set(ZDOVars.s_bodyAVelHash, Vector3.zero);
        z.Set("heimkit_tametravel_stamp", ZNet.instance.GetTime().Ticks);
        var view = ZNetScene.instance.FindInstance(z);
        if (view != null)
        {
            view.transform.SetPositionAndRotation(position, t.Journey.DestinationRotation);
            var body = view.GetComponent<Rigidbody>();
            if (body != null) { body.position = position; body.rotation = t.Journey.DestinationRotation; if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; } }
            var character = view.GetComponent<Character>();
            if (character != null)
            {
                ResetGround(character);
                MaxAltitude(character) = position.y;
                LastGroundTouch(character) = 1f;
                FallTimer(character) = 0f;
            }
            var ai = view.GetComponent<MonsterAI>();
            if (ai != null)
            {
                Path(ai)?.Clear();
                SpawnPoint(ai) = z.GetVec3(ZDOVars.s_spawnPoint, position);
                var player = ZNetScene.instance.FindInstance(t.Journey.Character);
                if (t.Waiting) { ai.SetFollowTarget(null); ai.SetPatrolPoint(); }
                else if (player != null) ai.SetFollowTarget(player);
            }
        }
        Physics.SyncTransforms();
    }
    private static float BodyClearance(ZDO z)
    {
        var body = PrefabRegistry.Component<CapsuleCollider>(PrefabRegistry.Current.Prefab(z.GetPrefab()));
        if (body == null || !TameTravelRules.Scale(z, out var scale)) return 32;
        return (body.radius + (body.direction == 1 ? 0 : body.height * 0.5f)) * scale.x + 1;
    }
    public void Dispose()
    {
        _context.Dispose();
        if (_padding != null) { UnityEngine.Object.Destroy(_padding.gameObject); _padding = null; }
    }
}
