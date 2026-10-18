using System;
using System.Collections.Generic;
using System.Linq;
using HeimKit.Features.SharedMap.Models;
using HeimKit.Shared.Players;
using UnityEngine;

namespace HeimKit.Features.SharedMap;

internal sealed class SharedMapSilverDiscovery : IDisposable
{
    internal static readonly int TerrainPrefab = "_TerrainCompiler".GetStableHashCode();
    private readonly ZDOMan _world;
    private readonly IPlayerPresence _presence;
    private readonly PlayerRoutedMessages _messages;
    private readonly Action<Vector3, ZDOID> _impact;
    private readonly Action<Exception> _failed;
    private readonly Dictionary<long, (ZDOID Actor, float At)> _recent = new();
    private IDisposable? _subscription;
    private int _terrainBudget;
    private float _terrainWindow;

    public SharedMapSilverDiscovery(ZDOMan world, IPlayerPresence presence, PlayerRoutedMessages messages,
        Action<Vector3, ZDOID> impact, Action<Exception> failed)
    { _world = world; _presence = presence; _messages = messages; _impact = impact; _failed = failed; }

    public void Install()
    {
        if (ZNetScene.instance.GetPrefab(TerrainPrefab)?.GetComponent<TerrainComp>() == null)
            throw new InvalidOperationException("Native terrain compiler is unavailable for Silver discovery.");
        _subscription = _messages.Subscribe(Observe, _failed);
    }

    private bool Actor(ZNetPeer peer, out ZDO actor, out Vector3 position, out string animation)
    {
        actor = null!; position = default; animation = "";
        if (!peer.IsReady() || peer.m_characterID.IsNone()) return false;
        actor = _world.GetZDO(peer.m_characterID);
        if (actor == null || !actor.IsValid() || actor.GetOwner() != peer.m_uid || actor.GetBool(ZDOVars.s_dead)) return false;
        float health = actor.GetFloat(ZDOVars.s_health, float.NaN);
        position = actor.GetPosition();
        if (!SharedMapExploration.Finite(health) || health <= 0 || !Finite(position) || Character.InInterior(position)) return false;
        var item = ZNetScene.instance.GetPrefab(actor.GetInt(ZDOVars.s_rightItem))?.GetComponent<ItemDrop>();
        if (item == null || item.m_itemData.m_shared.m_skillType != Skills.SkillType.Pickaxes) return false;
        animation = item.m_itemData.m_shared.m_attack?.m_attackAnimation ?? "";
        return animation.Length > 0;
    }

    private static bool Finite(Vector3 point) => SharedMapExploration.Finite(point.x) &&
        SharedMapExploration.Finite(point.y) && SharedMapExploration.Finite(point.z);
    private static bool Near(Vector3 actor, Vector3 point) => Finite(point) && (actor - point).sqrMagnitude <= 64f;
    private static bool AttackTrigger(string trigger, string animation)
    {
        if (trigger == animation) return true;
        if (!trigger.StartsWith(animation, StringComparison.Ordinal) || trigger.Length > animation.Length + 2) return false;
        string suffix = trigger.Substring(animation.Length);
        return suffix.Length > 0 && suffix.All(c => c >= '0' && c <= '9');
    }

    private void Observe(ZRpc rpc, ZPackage packet)
    {
        if (_presence.HasJoinedPlayers != true) return;
        var peer = _presence.Peers.FirstOrDefault(p => p.IsReady() && ReferenceEquals(p.m_rpc, rpc));
        if (peer == null) return;
        if (!RoutedActivityReader.TryRead(packet, peer.m_uid, out _, out int method) ||
            method != "SetTrigger".GetStableHashCode() && method != "RPC_ApplyOperation".GetStableHashCode() &&
            method != "RPC_Damage".GetStableHashCode() && method != "Hit".GetStableHashCode()) return;
        if (!Actor(peer, out _, out var position, out string animation)) return;
        if (!RoutedActivityReader.TryReadParameters(packet, peer.m_uid, out var target, out _, out var parameters) || parameters == null) return;
        try
        {
            if (method == "SetTrigger".GetStableHashCode())
            {
                string trigger = parameters.ReadString();
                if (target != peer.m_characterID || trigger.Length > 128 || parameters.GetPos() != parameters.Size() || !AttackTrigger(trigger, animation)) return;
                if (_recent.Count >= 4096)
                    foreach (long key in _recent.Where(p => Time.unscaledTime < p.Value.At || Time.unscaledTime - p.Value.At > 3).Select(p => p.Key).ToArray()) _recent.Remove(key);
                if (_recent.Count < 4096 || _recent.ContainsKey(peer.m_uid)) _recent[peer.m_uid] = (peer.m_characterID, Time.unscaledTime);
                return;
            }
            var targetZdo = _world.GetZDO(target);
            if (targetZdo == null || !targetZdo.IsValid()) return;
            if (method == "RPC_ApplyOperation".GetStableHashCode())
            {
                if (targetZdo.GetPrefab() != TerrainPrefab) return;
                int start = parameters.GetPos(), length = parameters.ReadInt();
                if (length < 17 || length > 64 || parameters.Size() - parameters.GetPos() != length) return;
                parameters.SetPos(start); var operation = parameters.ReadPackage();
                Vector3 point = operation.ReadVector3(); byte rotated = operation.ReadByte();
                if (rotated > 1) return;
                if (rotated != 0 && !Finite(operation.ReadVector3())) return;
                int type = operation.ReadInt();
                if (operation.GetPos() != operation.Size() || type != "digg_v3".GetStableHashCode() || !Near(position, point)) return;
                _impact(point, ZDOID.None); return;
            }
            var hit = new HitData(); hit.Deserialize(ref parameters);
            int left = parameters.Size() - parameters.GetPos();
            if (left == 4) { if (parameters.ReadInt() < 0) return; }
            else if (left != 0) return;
            if (hit.m_attacker != peer.m_characterID || hit.m_skill != Skills.SkillType.Pickaxes ||
                !SharedMapExploration.Finite(hit.m_damage.m_pickaxe) || hit.m_damage.m_pickaxe <= 0 || !Near(position, hit.m_point)) return;
            _impact(hit.m_point, target);
        }
        catch (Exception error) when (error is System.IO.IOException || error is System.IO.InvalidDataException || error is ArgumentException || error is FormatException)
        { /* Incomplete/unsupported evidence never reveals a vein or modifies native traffic. */ }
    }

    private bool RecentNear(Vector3 point)
    {
        float now = Time.unscaledTime; int matches = 0;
        foreach (var peer in _presence.Peers)
        {
            if (!_recent.TryGetValue(peer.m_uid, out var recent) || recent.Actor != peer.m_characterID ||
                now < recent.At || now - recent.At > 3 || !Actor(peer, out _, out var position, out _) || !Near(position, point)) continue;
            if (++matches > 1) return false;
        }
        return matches == 1;
    }

    public void AcceptedTerrain(ZDO zdo, byte[]? previous)
    {
        if (_recent.Count == 0 || zdo.GetPrefab() != TerrainPrefab || previous == null) return;
        float now = Time.unscaledTime;
        if (now < _terrainWindow || now - _terrainWindow >= 1) { _terrainWindow = now; _terrainBudget = 0; }
        if (_terrainBudget++ >= 32) return;
        var next = SharedMapTerrainData.Read(zdo.GetByteArray(ZDOVars.s_TCData));
        if (next == null) return;
        var point = new Vector3(next.X, next.Y, next.Z);
        if (!RecentNear(point) || !SharedMapTerrainData.Lowered(SharedMapTerrainData.Read(previous), next)) return;
        _impact(point, ZDOID.None);
    }

    public void AcceptedSilver(ZDO zdo, MapReplicationSnapshot before)
    {
        // A baseline avoids treating initial replication of old mining data as a new hit.
        if (before.PreviousPrefab != zdo.GetPrefab()) return;
        string current = zdo.GetString(ZDOVars.s_health);
        float health = zdo.GetFloat(ZDOVars.s_health, float.NaN);
        bool changed = current != before.MiningString || SharedMapExploration.Finite(health) &&
            SharedMapExploration.Finite(before.MiningHealth) && health < before.MiningHealth;
        if (changed && RecentNear(zdo.GetPosition())) _impact(zdo.GetPosition(), zdo.m_uid);
    }

    public void Dispose()
    {
        var subscription = _subscription; _subscription = null;
        try { subscription?.Dispose(); }
        finally { _recent.Clear(); }
    }
}
