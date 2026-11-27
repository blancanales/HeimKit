using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Prefabs;
using HeimKit.Core.World;
using HeimKit.Features.TameTravel.Models;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelGuestSummons : IDisposable
{
    private sealed class Trip
    {
        internal TameTravelActor Traveller;
        internal ZDOID Character;
        internal long Profile;
        internal string Name = "";
        internal TameTravelSummonGate Gate = null!;
        internal float Started, NextSourceCheck, ConfirmedAt;
        internal bool SawExit, Ready;
        internal int NextProbe;
        internal Vector3 FirstExit;
        internal readonly List<AnimalTransfer> Animals = new();
        internal readonly List<Vector3> Landings = new();
        internal readonly List<Bounds> Occupied = new();
    }

    private static readonly int Unsummon = "RPC_UnSummon".GetStableHashCode();
    private static readonly int Command = "Command".GetStableHashCode();
    private static readonly int Damage = "RPC_Damage".GetStableHashCode();
    private static bool Preempt(int method) => method == Unsummon || method == Command || method == Damage;

    private readonly TameTravelCommands _commands;
    private readonly TameTravelFollowing _following;
    private readonly TameTravelDungeonFit _fit = new();
    private readonly TameTravelSettings _settings;
    private readonly TameTravelRules _rules;
    private readonly ObjectTransferAuthority _authority;
    private readonly TerrainContext _context = new();
    private readonly ManualLogSource _log;
    private readonly Action<Exception> _fail;
    private readonly Dictionary<long, Trip> _trips = new();
    private readonly List<Trip> _snapshot = new();
    private readonly List<ZDO> _candidates = new();
    private readonly Dictionary<long, (ZNetPeer Peer, ZDOID Character, Vector3 Position, float Time, float Next)> _lastSearch = new();
    private readonly Dictionary<ZNetPeer, (ZDO Actor, uint Revision, float Until)> _loading = new();
    private readonly List<ZNetPeer> _loadingPeers = new();
    private readonly Dictionary<ZDOID, ZDO> _removing = new();
    private readonly List<ZDOID> _removed = new();
    private readonly ZDOMan _world = ZDOMan.instance;
    private readonly Harmony _hooks = new("heimkit.tametravel.guest_summons");
    private static TameTravelGuestSummons? _current;
    private ZDO? _newRecord;
    private ZNetPeer? _creator;
    private ZDOID _newId;

    internal TameTravelGuestSummons(TameTravelSettings settings, TameTravelRules rules, ObjectTransferAuthority authority,
        TameTravelCommands commands, TameTravelFollowing following, ManualLogSource log, Action<Exception> fail)
    {
        _commands = commands; _following = following; _settings = settings; _rules = rules; _authority = authority; _log = log; _fail = fail;
        try
        {
            if (settings.SummonTravel.Value)
            {
                _hooks.Patch(NativeAccess.Target(typeof(ZDOMan), "CreateNewZDO", typeof(ZDOID), typeof(Vector3), typeof(int)),
                    postfix: Hook(nameof(Created)));
                _hooks.Patch(NativeAccess.Target(typeof(ZDO), "Deserialize", typeof(ZPackage)),
                    prefix: Hook(nameof(BeforeState)), postfix: Hook(nameof(AfterState)));
                _hooks.Patch(NativeAccess.Target(typeof(ZDOMan), "DestroyZDO", typeof(ZDO)), prefix: Hook(nameof(DestroyRequested)));
                _hooks.Patch(NativeAccess.Target(typeof(Tameable), "UpdateSummon"), prefix: Hook(nameof(CheckSummonDistance)));
            }
            _current = this;
        }
        catch { Dispose(); throw; }
    }

    private static HarmonyMethod Hook(string method) => new(typeof(TameTravelGuestSummons), method);

    private static void Created(ZDOMan __instance, ZDO __result)
    {
        var c = _current;
        var peer = IncomingPackets.CurrentDataPeer;
        if (c == null || !ReferenceEquals(__instance, c._world) || peer == null || __result.m_uid.UserID != peer.m_uid) return;
        c._newId = __result.m_uid;
        c._newRecord = __result;
        c._creator = peer;
    }

    // The sender publishes complete extra-data maps. Preserve the host's attribution across
    // updates without advancing an owner's wire revision or trusting a supplied caster stamp.
    private static void BeforeState(ZDO __instance, out long __state) =>
        __state = _current != null && IncomingPackets.CurrentDataPeer != null &&
            __instance.GetPrefab() == TameTravelSummons.Skeleton ? __instance.GetLong(TameTravelSummons.Summoner) : 0;

    private static void AfterState(ZDO __instance, long __state)
    {
        var c = _current;
        var sender = IncomingPackets.CurrentDataPeer;
        if (c == null || sender == null) return;
        try
        {
            bool created = ReferenceEquals(c._newRecord, __instance) && __instance.m_uid == c._newId && ReferenceEquals(sender, c._creator);
            if (created) { c._newRecord = null; c._creator = null; }
            if (__instance.GetPrefab() != TameTravelSummons.Skeleton) return;
            if (created && sender.IsReady() && !sender.m_characterID.IsNone())
            {
                var player = c._world.GetZDO(sender.m_characterID);
                if (player != null && player.IsValid())
                    __state = CasterAtCreation(sender.m_uid, __instance.m_uid.UserID, __instance.GetOwner(),
                        player.GetOwner(), player.GetLong(ZDOVars.s_playerID), __instance.Persistent,
                        __instance.GetInt(ZDOVars.s_maxInstances));
            }
            if (created && c._settings.DebugLogging.Enabled)
                DiagnosticLog.Write(c._log, "TameTravel", TameTravelLogTopics.Travel,
                    $"Received new friendly skeleton. animal={__instance.m_uid} character={sender.m_characterID} summoner={__state} owner={__instance.GetOwner()}.",
                    level: LogLevel.Debug);
            if (__instance.GetLong(TameTravelSummons.Summoner) != __state)
                ZDOExtraData.Set(__instance.m_uid, TameTravelSummons.Summoner, __state);
        }
        catch (Exception error) { c._fail(error); }
    }

    // The host becomes the simulator during the source drain. The native distance
    // check can run between replication of the player at the exit and placement.
    private static bool CheckSummonDistance(Tameable __instance)
    {
        var c = _current;
        if (c == null || c._trips.Count == 0 || __instance.m_unsummonDistance <= 0) return true;
        try
        {
            var record = __instance.GetComponent<ZNetView>()?.GetZDO();
            if (record == null || !record.IsOwner() || record.GetPrefab() != TameTravelSummons.Skeleton ||
                !c._authority.Contains(record.m_uid)) return true;
            foreach (var trip in c._trips.Values)
                foreach (var animal in trip.Animals)
                    if (!animal.Moved && animal.Id == record.m_uid && ReferenceEquals(animal.Record, record) &&
                        c._authority.Holds(animal) && Eligible(record, trip.Profile, trip.Name)) return false;
            return true;
        }
        catch (Exception error) { c._fail(error); return true; }
    }

    // DestroyZDO queues native owner destruction. During this frame the record can
    // still resolve even though the original creature has already been unsummoned.
    private static void DestroyRequested(ZDOMan __instance, ZDO zdo)
    {
        var c = _current;
        if (c == null || !ReferenceEquals(__instance, c._world) || zdo == null || !zdo.IsOwner() ||
            zdo.GetPrefab() != TameTravelSummons.Skeleton) return;
        c._removing[zdo.m_uid] = zdo;
    }

    internal static long CasterAtCreation(long sender, long allocator, long initialOwner, long characterOwner,
        long profile, bool persistent, int staffLimit) =>
        persistent && staffLimit > 0 && sender != 0 && sender == allocator && sender == initialOwner &&
        sender == characterOwner ? profile : 0;

    internal bool Idle => _trips.Count == 0;
    internal void Observe(ZNetPeer peer, ZDO player, float now, bool allowNew, PlayerSample? sample)
    {
        bool pendingGeometry = _loading.Remove(peer);
        if (_rules.ActorRejection(peer, out var actor, allowInterior: true) != null ||
            !ReferenceEquals(actor, player)) { Cancel(peer.m_uid); return; }
        var point = player.GetPosition();
        if (_trips.TryGetValue(peer.m_uid, out var trip))
        {
            if (!ReferenceEquals(trip.Traveller.Remote, peer) || trip.Character != peer.m_characterID) { Cancel(peer.m_uid); return; }
            var delta = point - trip.Gate.Exit;
            if (trip.SawExit)
            {
                float limit = trip.Ready ? TameTravelPlacementRules.LandingRadius : 10f;
                if (delta.sqrMagnitude > limit * limit || Character.InInterior(point) != Character.InInterior(trip.Gate.Exit))
                { Cancel(peer.m_uid, "left_destination"); return; }
                // While teleporting, UpdateTeleport repeatedly pins the character to the target.
                if (!trip.Ready && WalkedFromExit(trip.FirstExit, point, trip.Gate.Exit))
                { trip.Ready = true; trip.ConfirmedAt = now; }
            }
            else if (delta.sqrMagnitude <= 2.25f)
            {
                foreach (var t in trip.Animals)
                    if (t.Phase == 5 && _authority.Holds(t))
                    {
                        _authority.PauseFollow(t);
                        t.FollowControl = false;
                        t.Phase = 1;
                    }
                trip.FirstExit = point;
                trip.SawExit = true;
            }
            else if ((point - trip.Gate.Source).sqrMagnitude > 64) Cancel(peer.m_uid);
            return;
        }
        if (!allowNew) return;
        bool same = _lastSearch.TryGetValue(peer.m_uid, out var last) &&
            ReferenceEquals(last.Peer, peer) && last.Character == peer.m_characterID;
        if (same && now < last.Next)
        {
            if (pendingGeometry) _loading[peer] = (player, player.DataRevision, now + 2);
            return;
        }
        if (_lastSearch.Count >= 128 && !_lastSearch.ContainsKey(peer.m_uid)) _lastSearch.Clear();
        var previous = same && TameTravelTraversalRules.SourceRejection(0, now - last.Time, (point - last.Position).sqrMagnitude) == null
            ? last.Position : point;
        _lastSearch[peer.m_uid] = (peer, peer.m_characterID, point, now, now + .2f);
        long profile = player.GetLong(ZDOVars.s_playerID);
        if (profile == 0) return;
        var nearby = TameTravelRules.Nearby(point, SourceRecord);
        // Reuse this bounded search to arm outdoor portal observation before a wolf's
        // owner-local follow command has produced any HeimKit command binding.
        if (nearby != null && sample != null && sample.Character == peer.m_characterID &&
            sample.Traveller.Matches(peer) && !Character.InInterior(point))
        {
            bool portal = false, wolf = false;
            foreach (var z in nearby)
            {
                int hash = z.GetPrefab();
                portal |= hash == WoodPortal || hash == StonePortal;
                wolf |= hash == TameTravelRules.AdultWolf && NearSource(z, point) && z.GetBool(ZDOVars.s_tamed);
                if (portal && wolf) { sample.WolfApproachUntil = now + 2; break; }
            }
        }
        if (nearby == null || !Candidates(nearby, peer, profile, point, remoteOwnersOnly: false))
        {
            // Players without nearby summons have no doorway observation work.
            _lastSearch[peer.m_uid] = (peer, peer.m_characterID, point, now, now + 1.2f);
            return;
        }
        Search(peer, player, point, previous, profile, nearby, now, now + 2);
    }

    private void Search(ZNetPeer peer, ZDO player, Vector3 point, Vector3 previous, long profile, CandidateView nearby, float now, float until)
    {
        var gate = TameTravelSummonGate.Find(nearby, player, point, previous, _context, now, out bool loading);
        if (gate != null) Start(peer, profile, gate, now);
        else if (loading && now < until && _loading.Count < 64)
            _loading[peer] = (player, player.DataRevision, until);
    }

    private void RetryLoading(float now, bool allowNew)
    {
        if (!allowNew) { _loading.Clear(); return; }
        _loadingPeers.Clear();
        _loadingPeers.AddRange(_loading.Keys);
        foreach (var peer in _loadingPeers)
        {
            var probe = _loading[peer];
            _loading.Remove(peer);
            if (now >= probe.Until || _trips.ContainsKey(peer.m_uid) ||
                _rules.ActorRejection(peer, out var actor, allowInterior: true) != null ||
                !ReferenceEquals(probe.Actor, actor) || actor.DataRevision != probe.Revision) continue;
            var point = actor.GetPosition();
            var nearby = TameTravelRules.Nearby(point, SourceRecord);
            long profile = actor.GetLong(ZDOVars.s_playerID);
            if (nearby != null && Candidates(nearby, peer, profile, point, remoteOwnersOnly: false))
                Search(peer, actor, point, point, profile, nearby, now, probe.Until);
        }
    }

    internal static bool WalkedFromExit(Vector3 first, Vector3 current, Vector3 exit)
    {
        var moved = current - first;
        var offset = current - exit;
        return offset.sqrMagnitude <= 100 && new Vector2(offset.x, offset.z).sqrMagnitude >= .36f &&
            new Vector2(moved.x, moved.z).sqrMagnitude >= .36f;
    }

    internal void LocalAccepted(TameTravelActor host, TameTravelSummonGate gate)
    {
        Cancel(host.Owner);
        long profile = host.Local!.GetPlayerID();
        var nearby = TameTravelRules.Nearby(host.Local.transform.position, SourceRecord);
        if (nearby != null && Candidates(nearby, host, profile, host.Local.transform.position, remoteOwnersOnly: true))
            Start(host, profile, gate, Time.unscaledTime);
    }
    internal void LocalFinished(TameTravelActor host, Vector3 point)
    {
        if (!_trips.TryGetValue(host.Owner, out var trip)) return;
        if (trip.Character != host.Character || (point - trip.Gate.Exit).sqrMagnitude > 1 ||
            !ZNetScene.instance.IsAreaReady(trip.Gate.Exit)) { Cancel(host.Owner); return; }
        trip.SawExit = trip.Ready = true;
        trip.ConfirmedAt = Time.unscaledTime;
    }

    private static readonly int WoodPortal = "portal_wood".GetStableHashCode();
    private static readonly int StonePortal = "portal_stone".GetStableHashCode();

    private bool SourceRecord(ZDO record, Vector3 point)
    {
        int hash = record.GetPrefab();
        if (hash == TameTravelRules.AdultWolf || hash == TameTravelSummons.Skeleton || _commands.Bindings.ContainsKey(record.m_uid)) return true;
        if (hash == WoodPortal || hash == StonePortal) return (record.GetPosition() - point).sqrMagnitude <= 144;
        // A dungeon's exterior proxy can contain the interior exit far above its origin.
        return record.GetInt(ZDOVars.s_location) != 0;
    }

    private bool Candidates(CandidateView nearby, TameTravelActor actor, long profile, Vector3 point, bool remoteOwnersOnly)
    {
        _candidates.Clear();
        foreach (var z in nearby)
            if (NearSource(z, point) && !_removing.ContainsKey(z.m_uid) &&
                (BoundTame(z, actor) || _settings.SummonTravel.Value && Eligible(z, profile, actor.Name) &&
                    (!remoteOwnersOnly || z.HasOwner() && !z.IsOwner()) && !_authority.Contains(z.m_uid))) _candidates.Add(z);
        return _candidates.Count > 0;
    }

    private bool BoundTame(ZDO record, TameTravelActor actor) =>
        _commands.FollowingAuthorized(record.m_uid, actor, actor.Character, actor.Name) &&
        _rules.AnimalRejection(record, allowInterior: true) == null && record.GetString(ZDOVars.s_follow) == actor.Name;

    private static bool Eligible(ZDO z, long profile, string name) => profile != 0 && z.IsValid() && z.Persistent &&
        z.GetPrefab() == TameTravelSummons.Skeleton && NativeCharacterHealth.IsAlive(z) &&
        z.GetLong(TameTravelSummons.Summoner) == profile && z.GetString(ZDOVars.s_follow) == name;
    private bool NearSource(ZDO z, Vector3 point)
    {
        float range = z.GetPrefab() == TameTravelSummons.Skeleton
            ? _settings.SummonRangeMeters.Value
            : _settings.FollowerRangeMeters.Value;
        return (z.GetPosition() - point).sqrMagnitude <= range * range;
    }

    private void Start(TameTravelActor actor, long profile, TameTravelSummonGate gate, float now)
    {
        if (_trips.Count >= 64 || !gate.Valid()) return;
        var trip = new Trip { Traveller = actor, Character = actor.Character, Name = actor.Name,
            Profile = profile, Gate = gate, Started = now };

        _trips.Add(actor.Owner, trip);
        foreach (var z in _candidates)
        {
            if (!NearSource(z, gate.Source)) continue;
            if (z.GetPrefab() != TameTravelSummons.Skeleton)
            {
                if (gate.Doorway == null || !TameTravelTraversalRules.DungeonAllows(z.GetPrefab(), gate.LocationHash, !Character.InInterior(gate.Source)) || !BoundTame(z, actor) || _following.ChangingOwners(z.m_uid)) continue;
                _following.Stop(z.m_uid, "portal_reserved");
            }
            var t = new AnimalTransfer { Id = z.m_uid, Record = z, PreemptOnInteraction = Preempt, Started = now,
                Journey = new PortalJourney { Traveller = actor, Character = actor.Character, Name = actor.Name } };
            string rejection = "source_context_capacity";
            if (!_context.Include(z, now) || !_authority.Acquire(t, now, out rejection))
            {
                LogRelease(trip, t, rejection);
                continue;
            }
            trip.Animals.Add(t);
            if (trip.Animals.Count >= _settings.MaxFollowersPerTraversal.Value) break;
        }
        if (trip.Animals.Count == 0) { _trips.Remove(actor.Owner); return; }
        if (_settings.DebugLogging.Enabled)
            DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel,
                $"Reserved {trip.Animals.Count} follower(s) for {trip.Name} at {gate.Source} with expected exit {gate.Exit}.",
                level: LogLevel.Debug);
    }

    internal void Tick(float now, bool allowNew)
    {
        RetryLoading(now, allowNew);
        if (_removing.Count > 0)
        {
            _removed.Clear();
            foreach (var entry in _removing)
                if (!ReferenceEquals(_world.GetZDO(entry.Key), entry.Value)) _removed.Add(entry.Key);
            foreach (var id in _removed) _removing.Remove(id);
        }
        _snapshot.Clear();
        _snapshot.AddRange(_trips.Values);
        foreach (var trip in _snapshot)
        {
            if (trip.Animals.Count == 0) continue;
            if (_rules.ActorRejection(trip.Traveller, out var actor, allowInterior: true) != null ||
                actor.m_uid != trip.Character || actor.GetLong(ZDOVars.s_playerID) != trip.Profile ||
                !trip.Gate.Valid())
            { Cancel(trip.Traveller.Owner); continue; }
            var point = trip.Traveller.Position(actor);
            if (trip.Ready ? now - trip.ConfirmedAt > 30 : now - trip.Started > _settings.TravelWindowSeconds.Value)
            { Cancel(trip.Traveller.Owner, trip.Ready ? "placement_timeout" : "arrival_timeout"); continue; }
            bool canLand = trip.Ready && Character.InInterior(point) == Character.InInterior(trip.Gate.Exit) &&
                (point - trip.Gate.Exit).sqrMagnitude <= TameTravelPlacementRules.LandingRadius * TameTravelPlacementRules.LandingRadius &&
                (trip.Traveller.IsLocal || (trip.Traveller.Remote!.m_refPos - point).sqrMagnitude <= 36);
            // Prepare the expected exit during the native loading screen. Moving a record
            // still requires the later, independently confirmed arrival.
            string? destinationWait = null;
            if (trip.SawExit)
                destinationWait = FloorWait(trip, trip.Ready ? point : trip.Gate.Exit, now);
            int probe = trip.NextProbe % trip.Animals.Count;
            trip.NextProbe = probe + 1;
            bool? sourceReady = null;
            bool checkSource = !trip.SawExit && now >= trip.NextSourceCheck;
            if (checkSource) trip.NextSourceCheck = now + 1;
            for (int i = trip.Animals.Count - 1; i >= 0; i--)
            {
                var t = trip.Animals[i];
                string? rejected = _removing.ContainsKey(t.Id) ? "native_removal" :
                    !ReferenceEquals(_world.GetZDO(t.Id), t.Record) ? "original_record_missing" :
                    !_authority.Holds(t) ? "ownership_lease_lost" :
                    (t.Record.GetPrefab() == TameTravelSummons.Skeleton ? !Eligible(t.Record, trip.Profile, trip.Name) : !BoundTame(t.Record, trip.Traveller)) ? "follow_eligibility_changed" :
                    !t.Moved && !NearSource(t.Record, trip.Gate.Source) ? "left_source_range" : null;
                if (rejected != null) { Release(trip, i, rejected); continue; }
                if (!t.Moved) _context.Include(t.Record, now);
                if (t.Phase == 0)
                {
                    if (t.PreviousPeer == null || !t.PreviousPeer.IsReady() || now - t.Started > 6) { Release(trip, i, "source_drain_incomplete"); continue; }
                    if (!_authority.Echoed(t.Barrier)) continue;
                    if (!t.SourceStateObserved)
                    {
                        if (t.SourceEchoTime == 0) t.SourceEchoTime = now;
                        if (now - t.SourceEchoTime >= 2)
                        {
                            _authority.ForgetBarrier(t.Barrier);
                            _authority.RequestSourceState(t, now);
                        }
                        continue;
                    }
                    _authority.ForgetBarrier(t.Barrier); t.Barrier = 0; t.Phase = 1;
                }
                if (t.Phase == 5 && checkSource)
                {
                    sourceReady ??= FloorWait(trip, point, now) == null;
                    if (!sourceReady.Value) Release(trip, i, "source_context_unavailable");
                    continue;
                }
                if (t.Phase == 1 && !trip.SawExit && !trip.Traveller.IsLocal)
                {
                    // Keep native AI running while someone lingers at a doorway. A source
                    // simulator needs its floor, live character and drained follow state first.
                    sourceReady ??= FloorWait(trip, point, now) == null;
                    var view = ZNetScene.instance.FindInstance(t.Record);
                    var player = ZNetScene.instance.FindInstance(trip.Character);
                    var ai = view == null ? null : view.GetComponent<MonsterAI>();
                    if (sourceReady.Value && player != null && ai != null &&
                        (ai.GetFollowTarget() == null || ai.GetFollowTarget() == player))
                    {
                        ai.SetFollowTarget(player);
                        t.FollowControl = true;
                        _authority.RunFollow(t);
                    }
                    else if (now - trip.Started > 5) Release(trip, i, "source_follow_unavailable");
                    continue;
                }
                if (t.Phase == 1)
                {
                    t.SummonWait = !trip.Ready ? "arrival_confirmation" : !canLand ? "destination_position" :
                        !t.Record.IsOwner() ? "server_ownership" : "destination_context";
                    if (!canLand || !t.Record.IsOwner()) continue;
                    if (destinationWait != null) { t.SummonWait = destinationWait; continue; }
                    if (i != probe) continue;
                    t.SummonWait = "safe_landing";
                    Vector3 placement;
                    Bounds occupied;
                    if (t.Record.GetPrefab() == TameTravelSummons.Skeleton)
                    {
                        if (!TameTravelSummons.Landing(t.Record, point, trip.Gate.Rotation, trip.Landings, out placement)) continue;
                        occupied = TameTravelDungeonFit.SkeletonBounds(t.Record, placement);
                        if (trip.Occupied.Exists(occupied.Intersects)) continue;
                    }
                    else
                    {
                        var door = trip.Gate.Doorway;
                        t.SummonWait = "dungeon_door_clearance";
                        if (door == null || FloorWait(trip, trip.Gate.Source, now) != null ||
                            FloorWait(trip, trip.Gate.Exit, now) != null ||
                            !_fit.Landing(t.Record, door, trip.Occupied, out placement, out occupied)) continue;
                    }
                    trip.Occupied.Add(occupied);
                    TameTravelPlacement.Move(t.Record, placement, t.Record.GetPrefab() == TameTravelSummons.Skeleton ? trip.Gate.Rotation : TameTravelDungeonFit.Facing(trip.Gate.Rotation), trip.Character, false);
                    t.Placement = placement; t.Moved = true;
                    trip.Landings.Add(placement);
                    if (t.Record.GetPrefab() == TameTravelSummons.Skeleton)
                        TameTravelDiagnostics.SummonMove(_settings, _log, trip.Character, t.Id, placement);
                    else if (_settings.DebugLogging.Enabled)
                        DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel,
                            $"Dungeon tame relocated character={trip.Character} animal={t.Id} position={placement}", level: LogLevel.Debug);
                    _world.ForceSendZDO(t.Id);
                    if (trip.Traveller.IsLocal) { Release(trip, i, "local_arrival"); continue; }
                    t.Barrier = _authority.SendBarrier(trip.Traveller.Remote!, t.Record, now);
                    t.Phase = 2; t.PhaseTime = now;
                }
                if (t.Phase == 2 && _authority.Echoed(t.Barrier) && now - t.PhaseTime >= .5f)
                    _authority.Handoff(t);
                if (t.Phase == 3)
                {
                    if (t.OwnerObserved) Release(trip, i, "handoff_complete");
                    else _authority.RefreshDestination(t, now);
                }
            }
            if (trip.Animals.Count == 0) Cancel(trip.Traveller.Owner);
        }
    }

    private const float TerrainRadius = 8;
    private const float CollisionRadius = 12;
    private const float DungeonRadius = 96;

    private static bool LandingRecord(ZDO record, Vector3 point)
    {
        var delta = record.GetPosition() - point;
        float distance = delta.sqrMagnitude;
        if (distance <= CollisionRadius * CollisionRadius) return true;
        bool inTerrainArea = TerrainContext.WithinTerrainArea(delta, TerrainRadius);
        if (!inTerrainArea && distance > DungeonRadius * DungeonRadius) return false;
        var prefab = PrefabRegistry.Current.Prefab(record.GetPrefab());
        if (prefab == null) return distance <= DungeonRadius * DungeonRadius;
        return distance <= DungeonRadius * DungeonRadius && PrefabRegistry.Component<DungeonGenerator>(prefab) != null ||
            inTerrainArea && TerrainContext.IsTerrainPrefab(prefab);
    }

    private string? FloorWait(Trip trip, Vector3 point, float now)
    {
        if (trip.Traveller.IsLocal) return ZNetScene.instance.IsAreaReady(point) ? null : "local_scene_loading";
        var nearby = TameTravelRules.Nearby(point, LandingRecord);
        if (nearby == null) return "landing_context_over_capacity";
        // Interior geometry is loaded from the existing exterior location proxy and saved
        // dungeon generator in the same zone. Terrain is prepared at the exterior height.
        var terrainPoint = Character.InInterior(point) ? new Vector3(point.x, trip.Gate.Source.y, point.z) : point;
        if (Character.InInterior(terrainPoint)) terrainPoint.y = ZoneSystem.instance.m_waterLevel;
        if (!_context.Prepare(terrainPoint, nearby, now, TerrainRadius)) return _context.WaitReason ?? "terrain_loading";
        string? wait = null;
        foreach (var z in nearby)
        {
            var delta = z.GetPosition() - point;
            if (delta.sqrMagnitude > DungeonRadius * DungeonRadius) continue;
            var prefab = PrefabRegistry.Current.Prefab(z.GetPrefab());
            if (prefab == null) return "prefab_unavailable";
            if (delta.sqrMagnitude > CollisionRadius * CollisionRadius && PrefabRegistry.Component<DungeonGenerator>(prefab) == null) continue;
            if (PrefabRegistry.Component<Character>(prefab) == null && PrefabRegistry.Component<DungeonGenerator>(prefab) == null &&
                !PrefabRegistry.Current.Solid(z.GetPrefab())) continue;
            if (!_context.Include(z, now)) return "scene_capacity";
            if (ZNetScene.instance.FindInstance(z) == null) wait = "collision_instance_loading";
        }
        if (wait == null) Physics.SyncTransforms();
        return wait;
    }

    private void LogRelease(Trip trip, AnimalTransfer t, string reason)
    {
        if (_settings.DebugLogging.Enabled && !t.Moved)
            DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel,
                $"Follower transfer for {trip.Name} ended. character={trip.Character} animal={t.Id} phase={t.Phase} reason={reason} waiting_for={t.SummonWait} source_state={t.SourceStateObserved}.",
                level: LogLevel.Debug);
    }

    private void Release(Trip trip, int index, string reason)
    {
        var t = trip.Animals[index];
        LogRelease(trip, t, reason);
        _authority.ForgetBarrier(t.Barrier);
        if (_authority.Holds(t)) _authority.Release(t.Id, keepServerOwner: trip.Traveller.IsLocal && t.Moved, restoreUndrainedOwner: true);
        trip.Animals.RemoveAt(index);
        if (t.Moved && !Character.InInterior(t.Record.GetPosition()) && BoundTame(t.Record, trip.Traveller) &&
            (reason == "handoff_complete" || reason == "local_arrival"))
            _following.Command(trip.Traveller, t.Record, TameOrder.Follow);
    }

    internal void Cancel(long owner, string reason = "trip_cancelled")
    {
        if (!_trips.TryGetValue(owner, out var trip)) return;
        _trips.Remove(owner);
        for (int i = trip.Animals.Count - 1; i >= 0; i--) Release(trip, i, reason);
        if (trip.Landings.Count == 0 && _settings.DebugLogging.Enabled)
            DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel,
                $"Follower trip for {trip.Name} ended at {trip.Gate.Source} without relocation. Exit observed={trip.SawExit}, arrival confirmed={trip.Ready}.",
                level: LogLevel.Debug);
        if (trip.Landings.Count > 0)
            DiagnosticLog.Write(_log, "TameTravel", TameTravelLogTopics.Travel,
                $"Relocated {trip.Landings.Count} follower(s) for {trip.Name}.");
    }

    internal void CancelCharacter(ZDOID character)
    {
        foreach (var trip in _trips.Values)
            if (trip.Character == character) { Cancel(trip.Traveller.Owner); return; }
    }
    internal void CancelAll()
    {
        foreach (long owner in new List<long>(_trips.Keys)) Cancel(owner);
    }
    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        try { CancelAll(); }
        finally { _hooks.UnpatchSelf(); _context.Dispose(); }
    }
}
