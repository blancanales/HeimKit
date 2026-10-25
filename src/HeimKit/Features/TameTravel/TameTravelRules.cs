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
using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelRules
{
    private readonly IPlayerPresence _presence;
    private readonly PrefabCatalog<GameObject> _animals = PrefabRegistry.Current.Catalog<GameObject>("TameTravelRules._animals");
    public TameTravelRules(IPlayerPresence presence)
    {
        _presence = presence;
        foreach (string name in new[] { "Wolf", "Boar", "Lox", "Asksvin", "Hen", "Moose" })
        {
            var p = PrefabRegistry.Current.Prefab(name);
            var sync = PrefabRegistry.Component<ZSyncTransform>(p);
            if (p == null || PrefabRegistry.Component<Tameable>(p) == null || PrefabRegistry.Component<Humanoid>(p) == null || PrefabRegistry.Component<MonsterAI>(p) == null ||
                PrefabRegistry.Component<ZNetView>(p)?.m_persistent != true || sync == null || !sync.m_syncPosition || sync.m_characterParentSync ||
                PrefabRegistry.Component<CapsuleCollider>(p) == null) throw new InvalidOperationException("Unsupported native tame definition: " + name);
            _animals.Add(name.GetStableHashCode(), p);
        }
    }
    public bool Actor(ZNetPeer peer, out ZDO actor) => ActorRejection(peer, out actor) == null;
    internal bool ConnectedName(string name) => ConnectedName(_presence, name);
    internal static bool ConnectedName(IPlayerPresence presence, string name)
    {
        // A transport peer remains protected while loading, dead or respawning.
        // Unavailable presence is not evidence that an owner logged out.
        if (!presence.ConnectedCount.HasValue) return true;
        foreach (var peer in presence.Peers)
            if (string.Equals(peer.m_playerName, name, StringComparison.Ordinal)) return true;
        return false;
    }
    internal bool ConnectedPeer(ZNetPeer peer)
    {
        if (!_presence.ConnectedCount.HasValue) return true;
        foreach (var current in _presence.Peers) if (ReferenceEquals(current, peer)) return true;
        return false;
    }
    public string? ActorRejection(ZNetPeer peer, out ZDO actor)
    {
        actor = null!;
        bool present = false; int names = 0;
        var peers = _presence.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            if (ReferenceEquals(peers[i], peer)) present = true;
            if (peers[i].IsReady() && string.Equals(peers[i].m_playerName, peer.m_playerName, StringComparison.Ordinal)) names++;
        }
        if (!peer.IsReady() || !present) return "peer_not_ready";
        if (peer.m_characterID.IsNone()) return "character_not_ready";
        var z = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (z == null || !z.IsValid()) return "character_missing";
        if (z.GetOwner() != peer.m_uid) return "character_owner_changed";
        if (!NativeCharacterHealth.IsAlive(z)) return "character_not_alive";
        if (!PlayerWorldScope.Finite(z.GetPosition())) return "character_position_invalid";
        if (Character.InInterior(z.GetPosition())) return "character_interior";
        if (string.IsNullOrEmpty(peer.m_playerName)) return "player_name_unavailable";
        if (names != 1) return "player_name_ambiguous";
        // Native saved following resolves this character name, not the chat-supplied UserInfo.
        if (z.GetString(ZDOVars.s_playerName) != peer.m_playerName) return "character_name_mismatch";
        actor = z; return null;
    }
    public bool Animal(ZDO? z) => AnimalRejection(z) == null;
    public string? AnimalRejection(ZDO? z)
    {
        if (z == null) return "animal_missing";
        if (!z.IsValid()) return "animal_invalid";
        if (!z.Persistent) return "animal_not_persistent";
        if (!_animals.ContainsKey(z.GetPrefab())) return "animal_prefab_changed";
        if (!z.GetBool(ZDOVars.s_tamed)) return "animal_not_tamed";
        if (z.GetBool(ZDOVars.s_dead)) return "animal_dead";
        if (z.GetLong(ZDOVars.s_user) != 0) return "animal_ridden";
        if (!PlayerWorldScope.Finite(z.GetPosition())) return "animal_position_invalid";
        if (Character.InInterior(z.GetPosition())) return "animal_interior";
        if (!NativeCharacterHealth.IsAlive(z)) return "animal_health_invalid";
        return Scale(z, out _) ? null : "animal_scale_unsupported";
    }
    public GameObject Prefab(ZDO z) => _animals[z.GetPrefab()];
    public bool Supported(ZDO z) => _animals.ContainsKey(z.GetPrefab());
    public static bool Scale(ZDO z, out Vector3 scale)
    {
        var p = PrefabRegistry.Current.Prefab(z.GetPrefab());
        scale = z.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
        if (scale == Vector3.zero) { float s = z.GetFloat(ZDOVars.s_scaleScalarHash, p != null ? p.transform.localScale.x : 1); scale = Vector3.one * s; }
        return PlayerWorldScope.Finite(scale) && scale.x >= 0.25f && scale.x <= 4 && Math.Abs(scale.x - scale.y) < 0.001f && Math.Abs(scale.x - scale.z) < 0.001f;
    }
    // Borrowed until the next Nearby call; consume immediately, do not retain.
    public static CandidateView? Nearby(Vector3 point) => WorldObjectProvider.Current.For("TameTravel").Nearby(point);
    public static TeleportWorld? Portal(ZDO? z)
    {
        return PortalRejection(z) == null ? PrefabRegistry.Component<TeleportWorld>(PrefabRegistry.Current.Prefab(z!.GetPrefab())) : null;
    }
    public static string? PortalRejection(ZDO? z)
    {
        if (z == null || !z.IsValid()) return "record_missing";
        if (z.GetPrefab() != "portal_wood".GetStableHashCode() && z.GetPrefab() != "portal_stone".GetStableHashCode()) return "prefab_unsupported";
        if (z.GetLong(ZDOVars.s_creator) == 0) return "not_player_built";
        if (!PlayerWorldScope.Finite(z.GetPosition()) || Character.InInterior(z.GetPosition())) return "position_unsupported";
        if (!Scale(z, out var scale) || (scale - Vector3.one).sqrMagnitude > .0001f) return "scale_unsupported";
        return PrefabRegistry.Component<TeleportWorld>(PrefabRegistry.Current.Prefab(z.GetPrefab())) == null ? "definition_unavailable" : null;
    }
    public static bool Trigger(ZDO portal, ZDO actor, out Bounds box)
    {
        box = default;
        var p = PrefabRegistry.Current.Prefab(portal.GetPrefab());
        if (p == null) return false;
        var triggers = PrefabRegistry.Children<TeleportWorldTrigger>(p, true);
        if (triggers.Length != 1) return false;
        var collider = PrefabRegistry.Component<BoxCollider>(triggers[0]);
        var player = PrefabRegistry.Component<CapsuleCollider>(PrefabRegistry.Current.Prefab(actor.GetPrefab()));
        if (collider == null || !collider.isTrigger || player == null || player.direction != 1) return false;
        // Convert the native child box to a conservative root-local AABB, including child rotation/scale.
        Vector3 center = p.transform.InverseTransformPoint(collider.transform.TransformPoint(collider.center));
        box = new Bounds(center, Vector3.zero);
        Vector3 half = collider.size * 0.5f;
        for (int i = 0; i < 8; i++) box.Encapsulate(p.transform.InverseTransformPoint(collider.transform.TransformPoint(collider.center + new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z))));
        // Positions describe player feet; account for its centre and vertical extent.
        box.center -= player.center;
        box.Expand(new Vector3(player.radius * 2, player.height, player.radius * 2));
        return box.size.sqrMagnitude > 0;
    }
    public static bool Crossed(ZDO portal, ZDO actor, Vector3 before, Vector3 after)
    {
        if (!Trigger(portal, actor, out var box)) return false;
        var inverse = Quaternion.Inverse(portal.GetRotation());
        before = inverse * (before - portal.GetPosition()); after = inverse * (after - portal.GetPosition());
        return TameTravelTraversalRules.Touches(box, before, after);
    }
}
