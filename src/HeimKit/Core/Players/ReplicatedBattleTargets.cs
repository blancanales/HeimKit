using System;
using System.Diagnostics;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.Prefabs;
using HeimKit.Core.World;
using UnityEngine;

namespace HeimKit.Core.Players;

internal sealed class ReplicatedBattleTargets
{
    private const int RecordLimit = 64;
    private const double BudgetMilliseconds = .25;
    private const float Radius = 64;
    private readonly Cursor?[] _slots = new Cursor?[64];
    private int _nextPlayer;
    private bool _used;

    private sealed class Cursor
    {
        internal readonly int[] Offsets = new int[9];
        internal int Visit;
        internal ZDOID LastMatch;
    }

    internal readonly struct Match
    {
        private readonly string _name;
        private readonly ZDOID _creature, _player;
        private readonly long _owner;
        private readonly float _distance;
        internal Match(string name, ZDO record, ZDOID player, float distanceSquared)
        {
            _name = name; _creature = record.m_uid; _owner = record.GetOwner();
            _player = player; _distance = Mathf.Sqrt(distanceSquared);
        }
        internal string Describe() =>
            $" creature={_name}/{_creature} owner={_owner} player={_player} distance_m={_distance:F1}";
    }

    internal bool Sample(IPlayerPresence presence, ZDOMan world, out Match evidence)
    {
        evidence = default;
        long started = Stopwatch.GetTimestamp();
        int examined = 0;
        try
        {
            var peers = presence.Peers;
            bool local = Player.m_localPlayer != null;
            int count = peers.Count + (local ? 1 : 0);
            if (count == 0) return false;
            int index = _nextPlayer % count;
            _nextPlayer = (index + 1) % count;
            _used = true;
            WorldActor actor;
            if (index == peers.Count)
            {
                if (!WorldActors.TryLocal(out actor)) return false;
            }
            else
            {
                var peer = peers[index];
                if (!peer.IsReady()) return false;
                actor = new WorldActor(peer.m_uid, peer.m_characterID);
            }
            var player = WorldActors.Resolve(actor);
            if (player == null || !NativeCharacterHealth.IsAlive(player)) return false;
            var position = player.GetPosition();
            if (!PlayerWorldScope.Finite(position)) return false;
            var cursor = _slots[index % _slots.Length] ??= new Cursor();
            if (!cursor.LastMatch.IsNone())
            {
                examined++;
                if (Qualifies(world.GetZDO(cursor.LastMatch), actor.Character, position, out evidence)) return true;
                cursor.LastMatch = ZDOID.None;
            }

            // Alternate the player's own cell with each of its eight neighbours.
            // Each cell has its own offset so a dense base cannot restart every visit at record zero.
            int visit = cursor.Visit;
            cursor.Visit = (visit + 1) % 16;
            int cell = visit % 2 == 0 ? 4 : visit / 2;
            if (visit % 2 != 0 && cell >= 4) cell++;
            var center = ZoneSystem.GetZone(position);
            var sector = new Vector2s(center.x + cell % 3 - 1, center.y + cell / 3 - 1);
            var records = WorldObjectProvider.BorrowSector(world, sector);
            if (records == null || records.Count == 0) return false;
            int offset = cursor.Offsets[cell] % records.Count;
            int limit = Math.Min(records.Count, RecordLimit - examined);
            for (int i = 0; i < limit; i++)
            {
                if (ServerPerformance.Milliseconds(Stopwatch.GetTimestamp() - started) >= BudgetMilliseconds) break;
                var record = records[offset];
                offset = (offset + 1) % records.Count;
                cursor.Offsets[cell] = offset;
                examined++;
                if (!Qualifies(record, actor.Character, position, out evidence)) continue;
                cursor.LastMatch = record.m_uid;
                return true;
            }
            return false;
        }
        finally
        {
            PerformanceStages.Record("HeimKit", "replicated_battle_sample",
                ServerPerformance.Milliseconds(Stopwatch.GetTimestamp() - started), examined);
        }
    }

    private static bool Qualifies(ZDO? record, ZDOID player, Vector3 position, out Match evidence)
    {
        evidence = default;
        if (record == null || !record.IsValid() || !record.GetBool(ZDOVars.s_haveTargetHash) ||
            !record.GetBool(ZDOVars.s_alert) || record.GetBool(ZDOVars.s_tamed)) return false;
        float distanceSquared = (record.GetPosition() - position).sqrMagnitude;
        if (!(distanceSquared <= Radius * Radius) || !NativeCharacterHealth.IsAlive(record)) return false;
        // Resolve only an already registered scene prefab. No object instantiation or ownership request.
        if (!PrefabRegistry.Current.TryGet(record.GetPrefab(), out var info) ||
            PrefabRegistry.Component<MonsterAI>(info.Prefab) == null) return false;
        var body = PrefabRegistry.Component<Character>(info.Prefab);
        if (body == null) return false;
        switch (body.m_faction)
        {
            case Character.Faction.ForestMonsters:
            case Character.Faction.Undead:
            case Character.Faction.Demon:
            case Character.Faction.MountainMonsters:
            case Character.Faction.SeaMonsters:
            case Character.Faction.PlainsMonsters:
            case Character.Faction.MistlandsMonsters:
            case Character.Faction.DeepNorth:
            case Character.Faction.Boss:
                evidence = new Match(info.Name, record, player, distanceSquared);
                return true;
            default:
                return false;
        }
    }

    internal void Reset()
    {
        if (!_used) return;
        Array.Clear(_slots, 0, _slots.Length);
        _nextPlayer = 0;
        _used = false;
    }
}
