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
    private bool _failed;
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
        if (_failed) return;
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
                    _context?.Damage(now, signal.Peer, target, character is Player, character.m_boss, actor.GetPosition());
                }
            }
        }
        catch (Exception error) { Failed(error); }
        finally { _pending.Clear(); }
    }
    private void Failed(Exception error)
    {
        bool raid = ModuleScheduler.Current.Combat.Raid;
        Dispose(); _failed = true; ModuleScheduler.Current.Combat.SetRaid(raid);
        DiagnosticLog.Write(_log, "HeimKit", "Damage-based battle observation is unavailable; native raid pauses remain active. Cause: " + error.GetType().Name + ".", LogLevel.Warning);
    }
    public void Dispose() { var lease = _lease; _lease = null; lease?.Dispose(); _pending.Clear(); ModuleScheduler.Current.Combat.Clear(); }
}
