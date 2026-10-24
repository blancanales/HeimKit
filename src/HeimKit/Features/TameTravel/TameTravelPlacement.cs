using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HeimKit.Features.TameTravel.Models;
using HeimKit.Shared;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelPlacement : IDisposable
{
    private static readonly Vector3[] FootprintOffsets = { Vector3.zero, Vector3.right, -Vector3.right, Vector3.forward, -Vector3.forward,
        new(.70710678f, 0, .70710678f), new(-.70710678f, 0, .70710678f), new(.70710678f, 0, -.70710678f), new(-.70710678f, 0, -.70710678f) };
    private static readonly Action<Character> ResetGround = (Action<Character>)AccessTools.Method(typeof(Character), "ResetGroundContact").CreateDelegate(typeof(Action<Character>));
    private static readonly System.Reflection.FieldInfo MaxAltitude = AccessTools.Field(typeof(Character), "m_maxAirAltitude"),
        LastGroundTouch = AccessTools.Field(typeof(Character), "m_lastGroundTouch"), FallTimer = AccessTools.Field(typeof(Character), "m_fallTimer"),
        Path = AccessTools.Field(typeof(BaseAI), "m_path"), SpawnPoint = AccessTools.Field(typeof(BaseAI), "m_spawnPoint");
    private readonly WorldTerrainContext _context = new();
    private readonly TameTravelPlacementSurfaces _surfaces = new();
    private readonly TameTravelPlacementAccess _access = new();
    private readonly TameTravelRules _rules;
    private readonly Dictionary<int, bool> _colliders = new();
    private readonly List<(Vector3 Position, float Clearance)> _creatures = new();
    private readonly List<Vector3> _players = new(), _placements = new();
    public string WaitReason { get; private set; } = "no_safe_ground_position";
    public ZDOID? BlockingObject { get; private set; }
    private readonly int _solid = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle", "character", "character_net", "character_ghost");
    private readonly int _building = LayerMask.GetMask("piece");
    public TameTravelPlacement(TameTravelRules rules) => _rules = rules;
    public bool Find(AnimalTransfer t, IEnumerable<AnimalTransfer> transfers, IReadOnlyList<ZNetPeer> peers, float now, bool evidence, out Vector3 position)
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
    private bool FindPosition(AnimalTransfer t, IEnumerable<AnimalTransfer> transfers, IReadOnlyList<ZNetPeer> peers, float now, bool evidence, out Vector3 position)
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
                var prefab = ZNetScene.instance.GetPrefab(z.GetPrefab());
                if (prefab == null) { WaitReason = "destination_prefab_unavailable"; BlockingObject = z.m_uid; return false; }
                // Retain saved context without changing its ownership or spawning duplicate records.
                if (prefab.GetComponent<Character>() != null) { creatures.Add((z.GetPosition(), BodyClearance(z))); continue; }
                if (!_context.Include(z, now)) { WaitReason = "destination_context_capacity"; BlockingObject = z.m_uid; return false; }
                if (!_colliders.TryGetValue(z.GetPrefab(), out bool solid))
                {
                    solid = prefab.GetComponentsInChildren<Collider>(true).Any(c => !c.isTrigger && c.enabled);
                    if (_colliders.Count < 4096) _colliders[z.GetPrefab()] = solid;
                }
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
        var capsule = _rules.Prefab(t.Record).GetComponent<CapsuleCollider>();
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
        float frontClearance = horizontalExtent + new Vector2(centerOffset.x, centerOffset.z).magnitude + 1;
        foreach (var peer in peers)
            if (peer.IsReady() && ZDOMan.instance.GetZDO(peer.m_characterID) is ZDO player) _players.Add(player.GetPosition());
        foreach (var other in transfers) if (!ReferenceEquals(other, t) && other.Moved) _placements.Add(other.Placement);
        Vector3? Ground(Vector3 p)
        {
            Vector3? Rejected(string reason) { if (evidence) Reject(t, reason); return null; }
            if (!TameTravelPlacementRules.InFront(p, t.Journey.DestinationPosition, forward, frontClearance)) return Rejected("portal_front_clearance");
            if (!_surfaces.Probe(p, out float height, out var normal, out string surfaceReason)) return Rejected(surfaceReason);
            if (Math.Abs(height - t.Journey.Exit.y) > 24) return Rejected("vertical_range");
            p.y = height + extent - centerOffset.y + .15f;
            if (Physics.Raycast(new Vector3(p.x, height + .1f, p.z), Vector3.up, 64, _building, QueryTriggerInteraction.Ignore)) return Rejected("building_overhead");
            if ((p - t.Journey.Exit).sqrMagnitude > TameTravelPlacementRules.ContextRadius * TameTravelPlacementRules.ContextRadius) return Rejected("walkout_range");
            // Cardinal and diagonal support samples cover the complete conservative footprint.
            foreach (var offset in FootprintOffsets)
                if (!_surfaces.Probe(p + offset * horizontalExtent, out float sample, out _, out _) ||
                    !TameTravelPlacementRules.SupportedFootprint(height, sample, offset * horizontalExtent, normal)) return Rejected("footprint_support");
            foreach (var player in _players)
                if ((player - p).sqrMagnitude < (horizontalExtent + 1.5f) * (horizontalExtent + 1.5f)) return Rejected("player_clearance");
            foreach (var other in _placements)
                if ((other - p).sqrMagnitude < (horizontalExtent + 6) * (horizontalExtent + 6)) return Rejected("follower_clearance");
            foreach (var creature in _creatures)
                if ((creature.Position - p).sqrMagnitude < (horizontalExtent + creature.Clearance) * (horizontalExtent + creature.Clearance)) return Rejected("creature_clearance");
            var center = p + centerOffset;
            return Physics.CheckCapsule(center + axis * halfLine, center - axis * halfLine, radius + .05f, _solid, QueryTriggerInteraction.Ignore) ? Rejected("solid_overlap") : p;
        }
        bool Clear(Vector3 before, Vector3 after)
        {
            var center = before + centerOffset; var delta = after - before;
            bool clear = !Physics.CapsuleCast(center + axis * halfLine, center - axis * halfLine, radius + .05f,
                delta.normalized, out _, delta.magnitude, _solid, QueryTriggerInteraction.Ignore);
            if (!clear && evidence) Reject(t, "walkout_collision");
            return clear;
        }
        // Delegates and obstacle snapshots are shared by every candidate in this search.
        Func<Vector3, Vector3?> groundProbe = Ground;
        Func<Vector3, Vector3, bool> clearProbe = Clear;
        int escapeProbes = 0, accessProbes = 0;
        var traveller = ZDOMan.instance.GetZDO(t.Journey.Character);
        if (traveller == null) { WaitReason = "traveller_unavailable"; return false; }
        int count = TameTravelPlacementRules.CandidateCount(horizontalExtent);
        int variation = unchecked(t.Id.GetHashCode() * 397 ^ (int)(t.Journey.Entered * 1000));
        int start = t.PlacementCursor % count;
        for (int visited = 0; visited < TameTravelPlacementRules.MaximumCandidatesPerPass; visited++)
        {
            int index = (start + visited) % count;
            t.PlacementCursor = (index + 1) % count;
            if (evidence) t.PlacementCandidates++;
            var candidate = TameTravelPlacementRules.Candidate(index, horizontalExtent, t.Journey.Exit, traveller.GetPosition(), forward, variation);
            if (evidence) { t.PlacementLastProbe = candidate; t.PlacementSupportHeight = null; }
            var delta = candidate - t.Journey.Exit;
            if (delta.x * delta.x + delta.z * delta.z > TameTravelPlacementRules.LandingRadius * TameTravelPlacementRules.LandingRadius)
            { if (evidence) Reject(t, "landing_range"); continue; }
            var direction = candidate - t.Journey.DestinationPosition; direction.y = 0;
            if (direction.sqrMagnitude < .001f) direction = forward;
            var point = Ground(candidate);
            if (!point.HasValue) continue;
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
                MaxAltitude.SetValue(character, position.y);
                LastGroundTouch.SetValue(character, 1f);
                FallTimer.SetValue(character, 0f);
            }
            var ai = view.GetComponent<MonsterAI>();
            if (ai != null)
            {
                (Path.GetValue(ai) as List<Vector3>)?.Clear();
                SpawnPoint.SetValue(ai, z.GetVec3(ZDOVars.s_spawnPoint, position));
                var player = ZNetScene.instance.FindInstance(t.Journey.Character);
                if (t.Waiting) { ai.SetFollowTarget(null); ai.SetPatrolPoint(); }
                else if (player != null) ai.SetFollowTarget(player);
            }
        }
        Physics.SyncTransforms();
    }
    private static float BodyClearance(ZDO z)
    {
        var body = ZNetScene.instance.GetPrefab(z.GetPrefab())?.GetComponent<CapsuleCollider>();
        if (body == null || !TameTravelRules.Scale(z, out var scale)) return 32;
        return (body.radius + (body.direction == 1 ? 0 : body.height * 0.5f)) * scale.x + 1;
    }
    public void Dispose() => _context.Dispose();
}
