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
    private readonly WorldTerrainContext _context = new();
    private readonly TameTravelRules _rules;
    private readonly Dictionary<int, bool> _colliders = new();
    public string WaitReason { get; private set; } = "no_safe_ground_position";
    public ZDOID? BlockingObject { get; private set; }
    private readonly int _solid = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle", "character", "character_net", "character_ghost");
    public TameTravelPlacement(TameTravelRules rules) => _rules = rules;
    public bool Find(AnimalTransfer t, IEnumerable<AnimalTransfer> transfers, IReadOnlyList<ZNetPeer> peers, float now, out Vector3 position)
    {
        position = default; WaitReason = "no_safe_ground_position"; BlockingObject = null;
        var records = TameTravelRules.Nearby(t.Journey.Exit);
        if (records == null) { WaitReason = "destination_scope_over_capacity"; return false; }
        if (!_context.Prepare(t.Journey.Exit, records, now, 24))
        { WaitReason = _context.WaitReason ?? "destination_terrain_unready"; BlockingObject = _context.WaitObject; return false; }
        bool ready = true;
        var creatures = new List<(Vector3 Position, float Clearance)>();
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
        var right = t.Journey.DestinationRotation * Vector3.right;
        for (int i = 0; i < 16; i++)
        {
            float angle = (i % 8 - 3.5f) * 0.32f;
            float distance = 4 + horizontalExtent + (i / 8) * (horizontalExtent * 2 + 1);
            var p = t.Journey.DestinationPosition + (forward * Mathf.Cos(angle) + right * Mathf.Sin(angle)) * distance;
            if (!Heightmap.GetHeight(p, out float height) || height <= ZoneSystem.instance.m_waterLevel + 0.5f || Math.Abs(height - t.Journey.Exit.y) > 6) continue;
            p.y = height + extent - centerOffset.y + 0.15f;
            var ground = p;
            ZoneSystem.instance.GetGroundData(ref ground, out var normal, out _, out _, out var hmap);
            if (normal.y < 0.8f || hmap == null || hmap.GetLava(ground) > 0) continue;
            if ((p - t.Journey.Exit).sqrMagnitude > 22 * 22) continue;
            // Check support at the footprint, including long horizontal capsules.
            bool supported = true;
            foreach (var offset in new[] { Vector3.zero, Vector3.right * horizontalExtent, -Vector3.right * horizontalExtent, Vector3.forward * horizontalExtent, -Vector3.forward * horizontalExtent })
                if (!Heightmap.GetHeight(p + offset, out float sample) || Math.Abs(sample - height) > 0.5f) { supported = false; break; }
            if (!supported) continue;
            if (peers.Any(peer => peer.IsReady() && ZDOMan.instance.GetZDO(peer.m_characterID) is ZDO player && (player.GetPosition() - p).sqrMagnitude < (horizontalExtent + 1.5f) * (horizontalExtent + 1.5f))) continue;
            if (transfers.Any(other => !ReferenceEquals(other, t) && other.Moved && (other.Placement - p).sqrMagnitude < (horizontalExtent + 6) * (horizontalExtent + 6))) continue;
            // Record-only nearby creatures are also obstacles, even when their server instance is not loaded.
            if (creatures.Any(c => (c.Position - p).sqrMagnitude < (horizontalExtent + c.Clearance) * (horizontalExtent + c.Clearance))) continue;
            Vector3 center = p + centerOffset;
            if (Physics.CheckCapsule(center + axis * halfLine, center - axis * halfLine, radius + 0.05f, _solid, QueryTriggerInteraction.Ignore)) continue;
            position = p; return true;
        }
        return false;
    }
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
                AccessTools.Method(typeof(Character), "ResetGroundContact").Invoke(character, Array.Empty<object>());
                AccessTools.Field(typeof(Character), "m_maxAirAltitude").SetValue(character, position.y);
                AccessTools.Field(typeof(Character), "m_lastGroundTouch").SetValue(character, 1f);
                AccessTools.Field(typeof(Character), "m_fallTimer").SetValue(character, 0f);
            }
            var ai = view.GetComponent<MonsterAI>();
            if (ai != null)
            {
                (AccessTools.Field(typeof(BaseAI), "m_path").GetValue(ai) as List<Vector3>)?.Clear();
                AccessTools.Field(typeof(BaseAI), "m_spawnPoint").SetValue(ai, z.GetVec3(ZDOVars.s_spawnPoint, position));
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
