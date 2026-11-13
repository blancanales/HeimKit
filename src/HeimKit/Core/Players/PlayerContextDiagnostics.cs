using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using UnityEngine;

namespace HeimKit.Core.Players;

// Observation only: no ownership changes, world discovery, physics probes or combat-policy writes.
internal sealed class PlayerContextDiagnostics : IDisposable
{
    private sealed class State
    {
        internal ZDOID Actor, Target;
        internal int TargetPrefab, NpcCursor;
        internal long DamageSource, TargetedSource;
        internal string Role = "none", Signature = "";
        internal bool BossTarget;
        internal Vector3 DamagePosition, TargetPosition;
        internal float DamageAt = float.NegativeInfinity, TargetedAt = float.NegativeInfinity, Next, Written = float.NegativeInfinity;
        internal bool Sensed, Alerted;
    }
    private readonly IPlayerPresence _presence;
    private readonly PlayerRoutedMessages _messages;
    private readonly DebugLoggingPolicy _policy;
    private readonly ManualLogSource _log;
    private readonly Dictionary<ZNetPeer, State> _states = new();
    private readonly Dictionary<(ZNetPeer Peer, ZDOID Actor, ZDOID Target), (bool Sensed, bool Alerted, float At)> _targeted = new();
    private readonly List<ZNetPeer> _remove = new();
    private IDisposable? _lease;
    private ZDOMan? _world;
    private float _next, _last = float.NegativeInfinity;
    private ZNetPeer? _waitingPeer;
    private float _waitUntil;
    private int _cursor;
    private bool _failed;
    internal PlayerWeatherContext Weather { get; } = new();
    internal bool CaptureEnabled => !_failed && DiagnosticLog.ContextAvailable(_log, _policy);
    internal void BeginWeather(bool admitted, bool rain, bool water)
    { try { Weather.Begin(admitted, rain, water); } catch (Exception error) { Fail(error); } }
    internal void WeatherCandidate(Vector2s zone, bool managed)
    { try { Weather.Candidate(zone, managed); } catch (Exception error) { Fail(error); } }
    internal void WeatherAcquired(Vector2s zone)
    { try { Weather.Acquired(zone); } catch (Exception error) { Fail(error); } }
    internal void EndWeather(float now, IReadOnlyCollection<Vector2s> centres, bool limited)
    { try { Weather.End(now, centres, limited); } catch (Exception error) { Fail(error); } }
    internal void WeatherGate(float now, Vector2s zone, bool rainWet)
    { try { Weather.Gate(now, zone, rainWet); } catch (Exception error) { Fail(error); } }
    internal void WeatherConfirmed(float now, Vector2s zone)
    { try { Weather.Confirmed(now, zone); } catch (Exception error) { Fail(error); } }
    internal PlayerContextDiagnostics(IPlayerPresence presence, PlayerRoutedMessages messages, DebugLoggingPolicy policy, ManualLogSource log)
    { _presence = presence; _messages = messages; _policy = policy; _log = log; }
    private State? Get(ZNetPeer peer)
    {
        if (!_states.TryGetValue(peer, out var state))
        {
            if (_states.Count >= 64) return null;
            _states.Add(peer, state = new State { Actor = peer.m_characterID });
        }
        if (state.Actor != peer.m_characterID) _states[peer] = state = new State { Actor = peer.m_characterID };
        return state;
    }
    internal void Damage(float now, ZNetPeer source, ZDO target, bool playerTarget, bool bossTarget, Vector3 sourcePosition)
    {
        if (!CaptureEnabled || !ReferenceEquals(_world, ZDOMan.instance)) return;
        try
        {
            Capture(source, "nearby_rpc_sender_attacker_unverified", sourcePosition);
            if (playerTarget)
                foreach (var peer in _presence.Peers) if (peer.m_characterID == target.m_uid) { Capture(peer, "damage_message_target_hit_unconfirmed", target.GetPosition()); break; }
            void Capture(ZNetPeer peer, string role, Vector3 position)
            {
                var state = Get(peer); if (state == null) return;
                state.DamageAt = now; state.DamageSource = source.m_uid; state.Target = target.m_uid; state.TargetPrefab = target.GetPrefab(); state.Role = role;
                state.BossTarget = bossTarget;
                state.DamagePosition = position; state.TargetPosition = target.GetPosition();
            }
        }
        catch (Exception error) { Fail(error); }
    }
    private void Observe(ZRpc rpc, ZPackage packet)
    {
        if (!CaptureEnabled || _targeted.Count >= 64) return;
        var peer = PlayerPeerLookup.Ready(_presence.Peers, rpc);
        if (peer == null || peer.m_characterID.IsNone() ||
            !RoutedActivityReader.TryReadTargeted(packet, peer.m_uid, out var target, out bool sensed, out bool alerted) || target.IsNone()) return;
        _targeted[(peer, peer.m_characterID, target)] = (sensed, alerted, Time.unscaledTime);
    }
    internal void Tick(float now)
    {
        if (!ReferenceEquals(_world, ZDOMan.instance) || now < _last)
        { Dispose(); _world = ZDOMan.instance; _failed = false; }
        _last = now;
        if (!_policy.ConfiguredEnabled || _presence.HasJoinedPlayers != true || !WorldServices.World) { Dispose(); return; }
        if (_failed || now < _next) return;
        try
        {
            if (!CaptureEnabled) { _targeted.Clear(); return; }
            _lease ??= _messages.Subscribe(Observe, Fail);
            _remove.Clear();
            foreach (var peer in _states.Keys)
            {
                bool present = false; foreach (var current in _presence.Peers) if (ReferenceEquals(current, peer)) { present = true; break; }
                if (!present) _remove.Add(peer);
            }
            foreach (var peer in _remove) _states.Remove(peer);
            foreach (var signal in _targeted)
            {
                bool present = false; foreach (var peer in _presence.Peers) if (ReferenceEquals(peer, signal.Key.Peer) && peer.m_characterID == signal.Key.Actor) { present = true; break; }
                if (!present) continue;
                foreach (var peer in _presence.Peers) if (peer.m_characterID == signal.Key.Target && ValidActor(peer, out var actor) && NativeCharacterHealth.IsAlive(actor!))
                {
                    var state = Get(peer); if (state == null) break;
                    state.TargetedAt = signal.Value.At; state.Sensed = signal.Value.Sensed; state.Alerted = signal.Value.Alerted; state.TargetedSource = signal.Key.Peer.m_uid; break;
                }
            }
            _targeted.Clear();
            if (_presence.Peers.Count == 0 || !CaptureEnabled) return;
            var selected = _presence.Peers[_cursor % _presence.Peers.Count];
            var entry = Get(selected);
            if (entry == null || now < entry.Next) { Advance(); return; }
            // Align with an existing feature query instead of issuing a diagnostic
            // query or copying world records. Readiness polling is one actor lookup.
            if (ValidActor(selected, out var ready) && NativeCharacterHealth.IsAlive(ready!) && WorldObjectProvider.Current != null &&
                !WorldObjectProvider.Current.TryCachedCell(ZoneSystem.GetZone(ready!.GetPosition()), out _))
            {
                if (!ReferenceEquals(_waitingPeer, selected)) { _waitingPeer = selected; _waitUntil = now + 1.25f; }
                if (now < _waitUntil) return;
            }
            entry.Next = now + 10;
            Advance();
            using var timing = PerformanceStages.Measure("PlayerActivity", "player_context", 1);
            Report(now, selected, entry);
            void Advance() { _cursor++; if (_cursor >= 1000000) _cursor = 0; _next = now + 1; _waitingPeer = null; }
        }
        catch (Exception error) { Fail(error); }
    }
    private bool ValidActor(ZNetPeer peer, out ZDO? actor)
    {
        actor = peer.m_characterID.IsNone() ? null : _world?.GetZDO(peer.m_characterID);
        return peer.IsReady() && actor != null && actor.IsValid() && actor.GetOwner() == peer.m_uid && PlayerWorldScope.Finite(actor.GetPosition());
    }
    private void Report(float now, ZNetPeer peer, State entry)
    {
        string player = PlayerConnectionNotices.CleanName(peer.m_playerName) + " peer=" + peer.m_uid + " character=" + peer.m_characterID;
        if (!ValidActor(peer, out var actor))
        {
            Emit("actor_unavailable", new[] { DiagnosticLog.Field("player", player), DiagnosticLog.Field("actor_state", "unavailable") }); return;
        }
        Vector3 position = actor!.GetPosition(); var zone = ZoneSystem.GetZone(position);
        bool interior = Character.InInterior(position), alive = NativeCharacterHealth.IsAlive(actor);
        string biome = WorldGenerator.instance == null ? "unknown" : WorldGenerator.instance.GetBiome(position).ToString();
        var environment = EnvMan.instance?.GetCurrentEnvironment();
        string env = environment?.m_name ?? "unknown", wet = environment == null ? "unknown" : EnvMan.IsWet().ToString();
        var pressure = ModuleScheduler.Current.Combat;
        string global = pressure.Raid ? "raid" : pressure.Active(now) ? pressure.Boss ? "boss_damage" : "damage" : "none_observed";
        var ev = RandEventSystem.instance?.GetCurrentRandomEvent();
        float distance = ev == null ? float.PositiveInfinity : Utils.DistanceXZ(position, ev.m_pos);
        string raid = ev == null ? "none" : ev.m_name + " range_m=" + F(ev.m_eventRange) + " distance_xz_m=" + F(distance) +
            " within_horizontal_range=" + (distance <= ev.m_eventRange) + " participation=unconfirmed";
        string weather = "server_environment=" + env + " server_native_wet=" + wet + " player_local_weather=unverified";
        string protection = Weather.Read(now, zone, out string protectionKey);
        bool recentDamage = now >= entry.DamageAt && now - entry.DamageAt < (entry.BossTarget ? 20 : 8), recentTargeted = now >= entry.TargetedAt && now - entry.TargetedAt < 2;
        int npcEvidence = 0;
        string npc = alive ? NpcSample(zone, position, peer.m_characterID, entry, out npcEvidence) : "coverage=actor_not_alive";
        string key = zone + ":" + biome + ":" + interior + ":" + alive + ":" + global + ":" + env + ":" + wet + ":" + recentDamage + ":" + recentTargeted +
            ":" + entry.Target + ":" + (ev?.m_name ?? "none") + ":" + (ev != null && distance <= ev.m_eventRange) + ":" + protectionKey + ":" + npcEvidence + ":" + entry.Sensed + ":" + entry.Alerted;
        string targetName = entry.Target.IsNone() ? "none" : PrefabRegistry.Current.TryKnown(entry.TargetPrefab, out var knownTarget) ? knownTarget.Name : "unknown";
        string combat = "global_pause=" + global + " global_boss_window=" + pressure.Boss + " damage_role=" + entry.Role + " damage_age_s=" + Age(now, entry.DamageAt) +
            " damage_xyz_m=" + (float.IsNegativeInfinity(entry.DamageAt) ? "unknown" : Point(entry.DamagePosition)) +
            " target_xyz_m=" + (float.IsNegativeInfinity(entry.DamageAt) ? "unknown" : Point(entry.TargetPosition)) +
            " target=" + targetName + "/" + entry.Target + " boss_target=" + entry.BossTarget +
            " damage_source_peer=" + (float.IsNegativeInfinity(entry.DamageAt) ? "unknown" : entry.DamageSource.ToString()) + " targeted_rpc_age_s=" + Age(now, entry.TargetedAt) +
            " targeted_source_peer=" + (float.IsNegativeInfinity(entry.TargetedAt) ? "unknown" : entry.TargetedSource.ToString()) +
            " last_sensed=" + (float.IsNegativeInfinity(entry.TargetedAt) ? "unknown" : entry.Sensed.ToString()) +
            " last_alerted=" + (float.IsNegativeInfinity(entry.TargetedAt) ? "unknown" : entry.Alerted.ToString()) + " npc_identity_from_rpc=unknown";
        Emit(key, new[] { DiagnosticLog.Field("player", player), DiagnosticLog.Field("position", "xyz_m=" + F(position.x) + "," + F(position.y) + "," + F(position.z) +
            " zone=" + zone + " biome=" + biome + " interior=" + interior + " alive=" + alive), DiagnosticLog.Field("combat", combat),
            DiagnosticLog.Field("raid", raid), DiagnosticLog.Field("weather", weather), DiagnosticLog.Field("weather_guard", protection),
            DiagnosticLog.Field("npc_sample", npc), DiagnosticLog.Field("work", ServerPerformance.ContextWork()) });
        void Emit(string signature, LogField[] fields)
        {
            if (signature == entry.Signature && now >= entry.Written && now - entry.Written < 300) return;
            if (DiagnosticLog.WriteContext(_log, _policy, fields)) { entry.Signature = signature; entry.Written = now; }
        }
    }
    private static string NpcSample(Vector2s zone, Vector3 position, ZDOID player, State state, out int evidence)
    {
        evidence = 0;
        var provider = WorldObjectProvider.Current;
        if (provider == null || !provider.TryCachedCell(zone, out var records)) return "coverage=no_cached_player_zone exact_target=unknown";
        int limit = Math.Min(128, records.Count), examined = 0, ai = 0, bosses = 0, alerted = 0, targets = 0, clientUnknown = 0, ownedInspected = 0, exact = 0, staticTargets = 0, classified = 0, unknown = 0;
        int examplePrefab = 0, examplePriority = 0; ZDOID exampleId = ZDOID.None; long exampleOwner = 0;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < limit; i++)
        {
            if (i > 0 && ServerPerformance.Milliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - started) >= 1) break;
            examined++;
            var record = records[(state.NpcCursor + i) % records.Count];
            if (!record.IsValid() || !CombatActivity.Nearby(position, record.GetPosition())) continue;
            // Only classify already-discovered records; never instantiate or take ownership.
            bool alert = record.GetBool(ZDOVars.s_alert), hasTarget = record.GetBool(ZDOVars.s_haveTargetHash);
            if (!alert && !hasTarget) continue;
            if (classified >= 8 || !PrefabRegistry.Current.TryKnown(record.GetPrefab(), out var known)) { unknown++; continue; }
            classified++;
            if (PrefabRegistry.Component<BaseAI>(known.Prefab) == null) continue;
            if (!NativeCharacterHealth.IsAlive(record)) { unknown++; continue; }
            bool boss = PrefabRegistry.Component<Character>(known.Prefab)?.m_boss == true;
            if (boss) bosses++;
            Remember(boss ? 2 : 1);
            ai++; if (record.GetBool(ZDOVars.s_alert)) alerted++;
            if (hasTarget) targets++;
            if (record.GetOwner() != ZDOMan.GetSessionID()) { if (hasTarget) clientUnknown++; continue; }
            if (ownedInspected >= 4) continue;
            var instance = ZNetScene.instance?.FindInstance(record);
            if (instance == null || !instance.TryGetComponent<BaseAI>(out var live)) continue;
            ownedInspected++;
            var target = live.GetTargetCreature(); if (target != null && target.GetZDOID() == player) { exact++; Remember(4); }
            if (live is MonsterAI monster && monster.GetStaticTarget() != null) { staticTargets++; Remember(3); }
            void Remember(int priority)
            {
                if (priority <= examplePriority) return;
                examplePriority = priority; examplePrefab = record.GetPrefab(); exampleId = record.m_uid; exampleOwner = record.GetOwner();
            }
        }
        if (records.Count > 0) state.NpcCursor = (state.NpcCursor + examined) % records.Count;
        evidence = 32 | (alerted > 0 ? 1 : 0) | (targets > 0 ? 2 : 0) | (exact > 0 ? 4 : 0) | (staticTargets > 0 ? 8 : 0) | (unknown > 0 ? 16 : 0) | (bosses > 0 ? 64 : 0);
        string example = exampleId.IsNone() ? "unknown" : (PrefabRegistry.Current.TryKnown(examplePrefab, out var sample) ? sample.Name : "unknown") + "/" + exampleId + " owner=" + exampleOwner;
        return "coverage=partial_cached_zone examined=" + examined + "/" + records.Count + " active_ai=" + ai + " unclassified=" + unknown + " alerted=" + alerted + " has_target=" + targets +
            " boss_flagged_ai=" + bosses + " server_owned_targets_inspected=" + ownedInspected + " exact_target_player=" + exact + " server_owned_static_targets=" + staticTargets +
            " non_server_target_unknown=" + clientUnknown + " example=" + example + " outside_sample=unknown";
    }
    private static string F(float n) => n.ToString("F1", CultureInfo.InvariantCulture);
    private static string Point(Vector3 p) => F(p.x) + "," + F(p.y) + "," + F(p.z);
    private static string Age(float now, float at) => float.IsNegativeInfinity(at) || now < at ? "unknown" : F(now - at);
    private void Fail(Exception error)
    {
        _failed = true;
        try { Dispose(); } catch { /* Observer cleanup cannot stop gameplay. */ }
        try { DiagnosticLog.Write(_log, "PlayerActivity", CoreLogTopics.Scope, "Player-context diagnostics stopped; gameplay and combat policy continue unchanged.", level: LogLevel.Error, error: error); } catch { }
    }
    public void Dispose()
    {
        var lease = _lease; _lease = null;
        try { lease?.Dispose(); }
        finally { _states.Clear(); _targeted.Clear(); _remove.Clear(); Weather.Clear(); _next = 0; _cursor = 0; _waitingPeer = null; _waitUntil = 0; }
    }
}
