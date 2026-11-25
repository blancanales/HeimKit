using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using UnityEngine;

namespace HeimKit.Core.Players;

internal sealed class CombatActivity : IDisposable
{
    private readonly IPlayerPresence _presence;
    private readonly PlayerRoutedMessages _messages;
    private readonly ManualLogSource _log;
    private readonly Dictionary<(ZNetPeer Peer, ZDOID Actor, ZDOID Target), byte> _pending = new();
    private IDisposable? _lease;
    private ZDOMan? _world;
    private bool _failed, _targetSamplingFailed;
    private readonly ReplicatedBattleTargets _replicated = new();
    private bool _replicatedFailed;
    private int _aiCursor;
    private float _nextTargetSample, _lastTick;
    private static readonly int Damage = "RPC_Damage".GetStableHashCode();
    private readonly PlayerContextDiagnostics? _context;
    internal CombatActivity(IPlayerPresence presence, PlayerRoutedMessages messages, ManualLogSource log, PlayerContextDiagnostics? context = null)
    { _presence = presence; _messages = messages; _log = log; _context = context; }
    private void Observe(ZRpc rpc, ZPackage packet)
    {
        var peer = PlayerPeerLookup.Ready(_presence.Peers, rpc);
        if (peer == null || peer.m_characterID.IsNone() || _pending.Count >= 64 ||
            !RoutedActivityReader.TryRead(packet, peer.m_uid, out var target, out int method) || method != Damage || target.IsNone()) return;
        _pending[(peer, peer.m_characterID, target)] = 0;
    }
    internal static bool Nearby(Vector3 actor, Vector3 target) => PlayerWorldScope.Finite(actor) && PlayerWorldScope.Finite(target) && (actor - target).sqrMagnitude <= 128 * 128;
    internal void Tick(float now)
    {
        ModuleScheduler.Current.ObserveTime(now);
        if (!ReferenceEquals(_world, ZDOMan.instance)) { Dispose(); _world = ZDOMan.instance; _failed = false; }
        if (_presence.HasJoinedPlayers != true || !WorldServices.World) { Dispose(); return; }
        // Raid detection remains available even if damage observation failed.
        ModuleScheduler.Current.Combat.Active(now); // expire/clear rolled-back damage evidence before sampling a raid
        var events = RandEventSystem.instance;
        ModuleScheduler.Current.Combat.SetRaid(events != null && events.GetCurrentRandomEvent() != null);
        bool damage = false;
        if (!_failed)
        try
        {
            _lease ??= _messages.Subscribe(Observe, Failed);
            // GetCurrentRandomEvent is the server's raid, unlike the local-player
            // active-event/UI state. A paused/unattended raid still holds admission.
            var peers = _presence.Peers;
            foreach (var signal in _pending.Keys)
            {
                bool connected = false;
                for (int i = 0; i < peers.Count; i++) if (ReferenceEquals(peers[i], signal.Peer)) { connected = peers[i].IsReady(); break; }
                if (!connected || signal.Peer.m_characterID != signal.Actor) continue;
                var actor = _world!.GetZDO(signal.Actor); var target = _world.GetZDO(signal.Target);
                if (actor == null || target == null || !actor.IsValid() || !target.IsValid() || actor.GetOwner() != signal.Peer.m_uid ||
                    !NativeCharacterHealth.IsAlive(actor) || !Nearby(actor.GetPosition(), target.GetPosition())) continue;
                var prefab = PrefabRegistry.Current.Prefab(target.GetPrefab()); var character = PrefabRegistry.Component<Character>(prefab);
                if (character != null)
                {
                    ModuleScheduler.Current.Combat.Observe(now, character.m_boss);
                    damage = true;
                    _context?.Damage(now, signal.Peer, target, character is Player, character.m_boss, actor.GetPosition());
                }
            }
        }
        catch (Exception error) { Failed(error); }
        finally { _pending.Clear(); }

        var battle = ModuleScheduler.Current.Battle;
        bool previous = battle.Active;
        int inspected = 0, matched = 0;
        string? reason = null;
        string? details = null;
        if (battle.Requested && (now >= _nextTargetSample || now < _lastTick))
        {
            _nextTargetSample = now + .5f;
            try { if (!_targetSamplingFailed) reason = SampleTargets(out inspected, out matched); }
            catch (Exception error)
            {
                _targetSamplingFailed = true;
                DiagnosticLog.Write(_log, "HeimKit", "Battle target sampling unavailable: " + error.GetType().Name, level: LogLevel.Warning);
            }
            if (reason == null && !damage && !ModuleScheduler.Current.Combat.Raid && !_replicatedFailed)
            {
                try
                {
                    if (_replicated.Sample(_presence, _world!, out var evidence))
                    {
                        reason = "replicated_hostile_near_player";
                        matched = 1;
                        if (!previous) details = evidence.Describe();
                    }
                }
                catch (Exception error)
                {
                    _replicatedFailed = true;
                    DiagnosticLog.Write(_log, "HeimKit", "Replicated battle sampling unavailable: " + error.GetType().Name, level: LogLevel.Warning);
                }
            }
        }
        _lastTick = now;
        if (ModuleScheduler.Current.Combat.Raid) reason = "native_raid";
        else if (reason == null && damage) reason = "observed_character_damage";
        battle.Update(now, reason, matched);
        if (previous != battle.Active)
        {
            if (battle.Active)
                DiagnosticLog.Write(_log, "HeimKit", $"Battle pause started. reason={reason} sampled_live_ai={inspected} matching_ai={matched} quiet_seconds=30{details}.");
            else LogEnd(now, battle.Requested ? "quiet" : "features_disabled");
        }
    }

    // GetAllInstances returns the live native list. A remote AI replicates HaveTarget,
    // but its target identity may exist only on its owner. Label that proximity inference separately.
    private string? SampleTargets(out int inspected, out int matched)
    {
        inspected = matched = 0;
        var ai = BaseAI.GetAllInstances();
        var players = Player.GetAllPlayers();
        int count = Math.Min(32, ai.Count);
        if (count == 0 || players.Count == 0) return null;
        bool exact = false;
        for (int i = 0; i < count; i++)
        {
            if (_aiCursor >= ai.Count) _aiCursor = 0;
            var monster = ai[_aiCursor++] as MonsterAI;
            inspected++;
            if (!monster) continue;
            var view = monster!.GetComponent<ZNetView>();
            if (!view || !view!.IsValid() || !monster.HaveTarget()) continue;
            var body = monster.GetComponent<Character>();
            if (!body || body!.IsDead() || body.IsTamed()) continue;
            var target = monster.GetTargetCreature();
            if (target && !(target is Player)) continue;
            for (int j = 0; j < players.Count && j < 64; j++)
            {
                var player = players[j];
                if (!player || player!.IsDead() || (target && target != player)) continue;
                var delta = player.transform.position - body.transform.position;
                float range = target ? 128 : 32;
                if (!(delta.sqrMagnitude <= range * range) || !monster.IsEnemy(player)) continue;
                matched++;
                if (target) exact = true;
                break;
            }
        }
        return exact ? "monster_targeting_player" : matched > 0 ? "engaged_monster_near_player" : null;
    }

    private void LogEnd(float now, string reason)
    {
        var battle = ModuleScheduler.Current.Battle;
        DiagnosticLog.Write(_log, "HeimKit", $"Battle pause ended. reason={reason} last_evidence={battle.Reason} duration_s={Math.Max(0, now - battle.Started):F1} peak_matching_ai_sample={battle.PeakSample}.");

    }
    private void Failed(Exception error)
    {
        bool raid = ModuleScheduler.Current.Combat.Raid;
        _lease?.Dispose(); _lease = null; _pending.Clear(); _failed = true; ModuleScheduler.Current.Combat.SetRaid(raid);
        DiagnosticLog.Write(_log, "HeimKit", "Damage-based battle observation is unavailable; native raid pauses remain active. Cause: " + error.GetType().Name + ".", LogLevel.Warning);
    }
    public void Dispose()
    {
        var scheduler = ModuleScheduler.Current;
        if (scheduler.Battle.Active) LogEnd(_lastTick, "session_ended");
        scheduler.Battle.Reset();
        var lease = _lease; _lease = null; lease?.Dispose(); _pending.Clear(); scheduler.Combat.Clear();
        _replicated.Reset(); _replicatedFailed = false;
        _nextTargetSample = 0; _aiCursor = 0; _targetSamplingFailed = false;
    }
}
