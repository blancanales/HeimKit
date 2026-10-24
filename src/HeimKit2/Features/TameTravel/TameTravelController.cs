using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Features.TameTravel.Models;
using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelController : IDisposable
{
    private static TameTravelController? _current;
    private static readonly int PongMethod = "RPC_Pong".GetStableHashCode(), TeleportMethod = "RPC_TeleportTo".GetStableHashCode(),
        TeleportPlayerMethod = "RPC_TeleportPlayer".GetStableHashCode(), CommandMethod = "Command".GetStableHashCode(),
        SayMethod = "Say".GetStableHashCode(), ChatMethod = "ChatMessage".GetStableHashCode();
    private readonly Harmony _hooks = new("heimkit.tametravel.traversal");
    private readonly TameTravelSettings _settings;
    private readonly ManualLogSource _log;
    private readonly PlayerServices _players;
    private readonly Action<Exception> _fail;
    private readonly TameTravelRules _rules;
    private readonly TameTravelCommands _commands;
    private readonly ObjectTransferAuthority _authority;
    private readonly TameTravelPlacement _placement;
    private readonly TameTravelFollowing _following;
    private readonly TameTravelDiagnostics _diagnostics;
    private readonly IDisposable _messages;
    private readonly Dictionary<long, PlayerSample> _samples = new();
    private readonly Dictionary<ZDOID, AnimalTransfer> _transfers = new();
    private readonly HashSet<ZDOID> _reserved = new();
    private readonly Dictionary<(long Peer, long Message), float> _messagesSeen = new();
    private readonly List<(long Peer, long Message)> _expiredMessages = new();
    private readonly List<ZNetPeer> _readyPeers = new();
    private readonly List<KeyValuePair<long, PlayerSample>> _sampleSnapshot = new();
    private readonly List<AnimalTransfer> _transferSnapshot = new();
    private readonly List<FollowBinding> _bindingSnapshot = new();
    private readonly List<ZDO> _portalSnapshot = new();
    private readonly List<FollowBinding> _orderedBindings = new();
    private static readonly Comparison<FollowBinding> BindingOrder = (a, b) => NativeIdentity.Compare(a.Animal, b.Animal);
    private int _cursor;
    private bool _disposed;
    private bool _placementUsed;
    public TameTravelController(TameTravel module, TameTravelSettings settings, ManualLogSource log, PlayerServices players)
    {
        _settings = settings; _log = log; _players = players; _fail = module.Fail;
        _rules = new(players.Presence);
        _diagnostics = new(log, settings.DebugLogging);
        _authority = new(players.Presence, module.Fail);
        try { _placement = new(_rules); } catch { _authority.Dispose(); throw; }
        try { _following = new(_rules, settings, _authority, _diagnostics, log, module.Fail); }
        catch { ModuleCleanup.Run(log, "TameTravel", () => _placement.Dispose(), () => _authority.Dispose()); throw; }
        _commands = new(_rules, settings, log, id => _reserved.Contains(id) || _following.ChangingOwners(id), _following.Command, _diagnostics);
        try
        {
            if (_current != null) throw new InvalidOperationException("TameTravel traversal observer is already active.");
            _current = this;
            _hooks.Patch(AccessTools.Method(typeof(Player), "TeleportTo"), prefix: new HarmonyMethod(typeof(TameTravelController), nameof(CompetingTeleport)));
            _hooks.Patch(AccessTools.Method(typeof(Chat), "TeleportPlayer"), prefix: new HarmonyMethod(typeof(TameTravelController), nameof(AdministrativeTeleport)));
            _hooks.Patch(AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData"), prefix: new HarmonyMethod(typeof(TameTravelController), nameof(BeforePlayerData)), postfix: new HarmonyMethod(typeof(TameTravelController), nameof(AfterPlayerData)));
            _messages = players.RoutedMessages.Subscribe(Observe, module.Fail);
        }
        catch
        {
            if (ReferenceEquals(_current, this)) _current = null;
            ModuleCleanup.Run(log, "TameTravel", () => _hooks.UnpatchSelf(), () => _following.Dispose(), () => _placement.Dispose(), () => _authority.Dispose());
            throw;
        }
    }
    public void Tick(float now)
    {
        if (!ModuleScheduler.Current.Due(this, "processing", now, 0.25f)) return;
        using var timing = _diagnostics.OptionalEnabled ? _diagnostics.Measure(now, () => _samples.Count + _following.Count) : null;
        _expiredMessages.Clear();
        foreach (var pair in _messagesSeen) if (now - pair.Value > 30) _expiredMessages.Add(pair.Key);
        foreach (var key in _expiredMessages) _messagesSeen.Remove(key);
        var peers = _readyPeers; peers.Clear();
        for (int rosterIndex1 = 0; rosterIndex1 < _players.Presence.Peers.Count && _players.Presence.Peers[rosterIndex1] is var peer; rosterIndex1++) if (peer.IsReady()) { peers.Add(peer); if (peers.Count > 64) break; }
        if (peers.Count > 64) { ClearJourneys(); return; }
        _sampleSnapshot.Clear(); foreach (var pair in _samples) _sampleSnapshot.Add(pair);
        foreach (var pair in _sampleSnapshot)
            if (!ContainsPeer(peers, pair.Value.Peer) || pair.Value.Character != pair.Value.Peer.m_characterID || !_rules.Actor(pair.Value.Peer, out _))
            { Cancel(pair.Value, now); _samples.Remove(pair.Key); }
        _commands.Tick(now);
        int count = Math.Min(8, peers.Count);
        for (int i = 0; i < count; i++)
        {
            var peer = peers[(_cursor + i) % peers.Count];
            string? actorReason = _rules.ActorRejection(peer, out var actor);
            _diagnostics.ActorGate(peer, actorReason);
            if (actorReason != null) continue;
            if (!_samples.TryGetValue(peer.m_uid, out var sample))
            { _samples.Add(peer.m_uid, new PlayerSample { Peer = peer, Character = peer.m_characterID, Position = actor.GetPosition(), Time = now, Emote = actor.GetInt(ZDOVars.s_emoteID) }); continue; }
            if (!ReferenceEquals(sample.Peer, peer) || sample.Character != peer.m_characterID) continue;
            bool reset = sample.Positions.Overflowed;
            if (reset)
            {
                Cancel(sample, now, "position_history_overflow", actor.GetPosition()); sample.Positions.AcknowledgeOverflow();
                { if (_diagnostics.SelectionEnabled) _diagnostics.Traversal(peer, "position_history", "overflow_reset", transition: true); }
            }
            var bindings = _bindingSnapshot; bindings.Clear();
            foreach (var binding in _commands.Bindings.Values)
                if (!binding.Pending && ReferenceEquals(binding.Peer, peer) && binding.Character == sample.Character) bindings.Add(binding);
            if (bindings.Count > 0 && sample.Journey == null && sample.Positions.Count == 0)
                { if (_diagnostics.SelectionEnabled) { if (_diagnostics.TraversalReady(peer, "source_observation", "awaiting_owner_position")) _diagnostics.Traversal(peer, "source_observation", "awaiting_owner_position", fields: new (string, object?)[] { ("followers", bindings.Count) }, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("last_observation_age_s", now - sample.Time) } : null); } }
            List<ZDO>? records = null; bool queried = false;
            while (sample.Positions.Take(out var observation))
            {
                if (reset) { sample.Position = observation.Position; sample.Time = observation.Time; reset = false; continue; }
                if (sample.Journey != null)
                {
                    if (now - observation.Time > 2) Cancel(sample, now, "stale_position_observation", observation.Position);
                    else UpdateJourney(sample, observation.Position, observation.Time, now, fresh: true);
                }
                else if (bindings.Count > 0)
                {
                    if (sample.PendingPortal != null && ResolvePortal(sample, observation, now, bindings))
                    { sample.Position = observation.Position; sample.Time = observation.Time; continue; }
                    string? rejected = TameTravelTraversalRules.SourceRejection(now - observation.Time, observation.Time - sample.Time, (observation.Position - sample.Position).sqrMagnitude);
                    bool pointOnly = rejected == "source_sample_gap" && observation.Time >= sample.Time;
                    if (rejected != null) { if (_diagnostics.SelectionEnabled) { if (_diagnostics.TraversalReady(peer, "source_observation", rejected)) _diagnostics.Traversal(peer, "source_observation", rejected, fields: null, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("before", sample.Position), ("after", observation.Position), ("gap_s", observation.Time - sample.Time), ("age_s", now - observation.Time), ("fresh_point_only", pointOnly) } : null); } }
                    if (rejected == null || pointOnly)
                    {
                        if (now >= sample.BlockedUntil) TryEnter(sample, actor, observation, now, bindings, ref records, ref queried, pointOnly);
                        else { if (_diagnostics.SelectionEnabled) { if (_diagnostics.TraversalReady(peer, "source_observation", "cooldown")) _diagnostics.Traversal(peer, "source_observation", "cooldown", fields: null, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("remaining_s", sample.BlockedUntil - now) } : null); } }
                    }
                }
                sample.Position = observation.Position; sample.Time = observation.Time;
            }
            // Timeouts, destination clearance and ordered echoes continue while a
            // stationary/loading owner has no newer payload. Arrival itself needs
            // an accepted owner position, not this polling snapshot.
            if (bindings.Count == 0) sample.PendingPortal = null;
            else if (sample.PendingPortal != null)
                ResolvePortal(sample, new PositionObservation(sample.Position, sample.Time, 0), now, bindings);
            if (sample.Journey != null) UpdateJourney(sample, actor.GetPosition(), now, now, fresh: false);
        }
        if (peers.Count > 0) _cursor = (_cursor + count) % peers.Count;
        // Consume the historical source witness before comparing a follower with
        // the player's latest (possibly already teleported) position.
        _following.Tick(now); _authority.Tick(now);
        _placementUsed = false;
        _transferSnapshot.Clear(); foreach (var transfer in _transfers.Values) _transferSnapshot.Add(transfer);
        foreach (var t in _transferSnapshot) UpdateTransfer(t, now);
        // One new ownership/placement operation per pass; ongoing barriers remain independently bounded.
        foreach (var sample in _samples.Values)
        {
            var journey = sample.Journey;
            if (journey == null) continue;
            if (journey.Followers.Count == 0)
            {
                if (!HasTransfer(journey)) Complete(sample, now);
                continue;
            }
            if (_transfers.Count >= 64) break;
            if (!_rules.Actor(journey.Peer, out var traveller) || journey.Arrived >= 0 && TameTravelTraversalRules.DestinationRejection(
                (traveller.GetPosition() - journey.Exit).sqrMagnitude, now - journey.Entered, _settings.TravelWindowSeconds.Value) != null) continue;
            var id = journey.Followers.Dequeue(); var animal = ZDOMan.instance.GetZDO(id);
            bool waiting = journey.Waiting.Contains(id);
            string? rejected = _rules.AnimalRejection(animal);
            if (rejected == null && !waiting && !_commands.FollowingAuthorized(id, journey.Peer, journey.Character, journey.Name)) rejected = "follow_authorization_ended";
            if (rejected == null && !TameTravelCommandRules.ConsentValid(waiting ? TameOrder.Wait : TameOrder.Follow, animal!.GetString(ZDOVars.s_follow), journey.Name)) rejected = "follow_state_changed";
            if (rejected == null && (animal!.GetPosition() - journey.SourcePosition).sqrMagnitude > _settings.FollowerRangeMeters.Value * _settings.FollowerRangeMeters.Value) rejected = "animal_left_source_range";
            if (rejected != null)
            {
                journey.EndReason = rejected; _reserved.Remove(id);
                { if (_diagnostics.SelectionEnabled) { if (_diagnostics.TraversalReady(journey.Peer, "transfer_start", rejected, journey.Source, id, transition: true)) _diagnostics.Traversal(journey.Peer, "transfer_start", rejected, journey.Source, id, fields: null, transition: true, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("animal_position", animal?.GetPosition()), ("source_position", journey.SourcePosition) } : null); } }
                continue;
            }
            var transfer = new AnimalTransfer { Record = animal!, Id = id, Journey = journey, Started = now, PhaseTime = now, Waiting = waiting };
            _transfers.Add(id, transfer);
            if (!_authority.Acquire(transfer, now))
            {
                journey.EndReason = "authority_acquisition_unavailable"; _transfers.Remove(id); _reserved.Remove(id);
                { if (_diagnostics.SelectionEnabled) { if (_diagnostics.TraversalReady(journey.Peer, "transfer_start", journey.EndReason, journey.Source, id, transition: true)) _diagnostics.Traversal(journey.Peer, "transfer_start", journey.EndReason, journey.Source, id, fields: null, transition: true, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("owner", animal!.GetOwner()), ("data_revision", animal.DataRevision) } : null); } }
            }
            break;
        }
        _diagnostics.Observe(now, _commands.Bindings.Count, _reserved.Count, _transfers.Count);
    }
    private readonly List<AnimalTransfer> _cancelTransfers = new();
    private static bool ContainsPeer(List<ZNetPeer> peers, ZNetPeer expected) { foreach (var peer in peers) if (ReferenceEquals(peer, expected)) return true; return false; }
    private bool HasTransfer(PortalJourney journey) { foreach (var transfer in _transfers.Values) if (ReferenceEquals(transfer.Journey, journey)) return true; return false; }
    private static bool HasConsent(List<FollowBinding> bindings, float time) { foreach (var binding in bindings) if (binding.Consent <= time) return true; return false; }
    private void TryEnter(PlayerSample sample, ZDO actor, PositionObservation observation, float now, List<FollowBinding> bindings, ref List<ZDO>? records, ref bool queried, bool pointOnly)
    {
        // New consent must not authorize a previously queued approach retrospectively.
        if (!HasConsent(bindings, observation.Time)) return;
        var position = observation.Position;
        if (!queried)
        {
            // One sector scan and portal-definition pass per sampled player/pass,
            // reused for every queued segment rather than rescanning per packet.
            var scope = TameTravelRules.Nearby(position);
            _portalSnapshot.Clear();
            if (scope != null) foreach (var z in scope)
                if (z.GetPrefab() == "portal_wood".GetStableHashCode() || z.GetPrefab() == "portal_stone".GetStableHashCode()) _portalSnapshot.Add(z);
            records = scope == null ? null : _portalSnapshot; queried = true;
        }
        if (records == null) { { if (_diagnostics.SelectionEnabled) _diagnostics.Traversal(sample.Peer, "source_entry", "scope_over_capacity"); } return; }
        int nearby = 0, supported = 0, entries = 0, linkedEntries = 0;
        ZDO? soleNearby = null, source = null, linkedSource = null;
        foreach (var record in records)
        {
            if (record.m_uid == sample.ReentryPortal)
            {
                // Completion must not interpret standing in the arrival trigger
                // as a request for an immediate return trip. Leave it first.
                if (TameTravelRules.Crossed(record, actor, position, position)) continue;
                sample.ReentryPortal = ZDOID.None;
            }
            if ((record.GetPosition() - position).sqrMagnitude >= 100) continue;
            nearby++; soleNearby = record;
            if (TameTravelRules.Portal(record) == null) continue;
            supported++;
            if (TameTravelRules.Crossed(record, actor, pointOnly ? position : sample.Position, position))
            {
                entries++; source = record;
                if (PortalPairRejection(record, out _) == null) { linkedEntries++; linkedSource = record; }
            }
        }
        if (linkedEntries == 1) { source = linkedSource; }
        else if (linkedEntries > 1 || entries != 1)
        {
            { if (_diagnostics.SelectionEnabled) { if (_diagnostics.TraversalReady(sample.Peer, "source_entry", linkedEntries > 1 || entries > 1 ? "ambiguous_trigger" : supported > 0 ? "outside_trigger" : nearby > 0 ? "source_portal_ineligible" : "no_portal_nearby", source: nearby == 1 ? soleNearby!.m_uid : (ZDOID?)null)) _diagnostics.Traversal(sample.Peer, "source_entry", linkedEntries > 1 || entries > 1 ? "ambiguous_trigger" : supported > 0 ? "outside_trigger" : nearby > 0 ? "source_portal_ineligible" : "no_portal_nearby", source: nearby == 1 ? soleNearby!.m_uid : (ZDOID?)null, fields: new (string, object?)[] { ("pos", position), ("followers", bindings.Count) }, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("portals", nearby), ("supported_portals", supported), ("creator", nearby == 1 ? soleNearby!.GetLong(ZDOVars.s_creator) : (long?)null), ("trigger_matches", entries), ("paired_trigger_matches", linkedEntries) } : null); } }
            return;
        }
        var portalSource = source!;
        string? pairReason = PortalPairRejection(portalSource, out var destination);
        if (pairReason != null)
        {
            _diagnostics.PortalPair(sample.Peer, portalSource, destination, pairReason);
            if (TameTravelPortalRules.CanWait(pairReason))
                sample.PendingPortal = new PortalWitness { Source = portalSource, SourceId = portalSource.m_uid,
                    Destination = portalSource.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal), Tag = portalSource.GetString(ZDOVars.s_tag),
                    Position = portalSource.GetPosition(), Rotation = portalSource.GetRotation(), Observation = observation };
            return;
        }
        BeginJourney(sample, portalSource, destination!, observation, now, bindings, pointOnly ? "fresh_inside_after_gap" : "owner_position_history");
    }
    private void BeginJourney(PlayerSample sample, ZDO portalSource, ZDO destination, PositionObservation observation, float now,
        List<FollowBinding> bindings, string mode)
    {
        var position = observation.Position;
        var portal = TameTravelRules.Portal(portalSource)!;
        var journey = new PortalJourney { Peer = sample.Peer, Character = sample.Character, Name = sample.Peer.m_playerName,
            Source = portalSource.m_uid, Destination = destination.m_uid, SourcePosition = portalSource.GetPosition(), DestinationPosition = destination.GetPosition(),
            SourceRotation = portalSource.GetRotation(), DestinationRotation = destination.GetRotation(), Tag = portalSource.GetString(ZDOVars.s_tag), Entered = observation.Time,
            Exit = destination.GetPosition() + destination.GetRotation() * Vector3.forward * portal!.m_exitDistance + Vector3.up };
        float range = _settings.FollowerRangeMeters.Value;
        _orderedBindings.Clear();
        foreach (var binding in bindings) if (binding.Consent <= observation.Time) _orderedBindings.Add(binding);
        _orderedBindings.Sort(BindingOrder);
        foreach (var b in _orderedBindings)
        {
            if (journey.Followers.Count >= _settings.MaxFollowersPerTraversal.Value || _reserved.Count >= 512) break;
            if (!_commands.Bindings.TryGetValue(b.Animal, out var current) || !ReferenceEquals(current, b)) continue;
            var animal = ZDOMan.instance.GetZDO(b.Animal);
            string? reason = _rules.AnimalRejection(animal);
            if (reason == null && !TameTravelCommandRules.ConsentValid(b.Order, animal!.GetString(ZDOVars.s_follow), b.Name)) reason = "follow_changed";
            if (reason == null && (_reserved.Contains(b.Animal) || _following.ChangingOwners(b.Animal))) reason = "animal_handoff_busy";
            if (reason == null && (animal!.GetPosition() - position).sqrMagnitude > range * range) reason = "animal_outside_player_range";
            if (reason == null && (animal!.GetPosition() - portalSource.GetPosition()).sqrMagnitude > range * range) reason = "animal_outside_portal_range";
            if (reason != null)
            {
                { if (_diagnostics.SelectionEnabled) { if (_diagnostics.TraversalReady(sample.Peer, "follower_entry", reason, portalSource.m_uid, b.Animal)) _diagnostics.Traversal(sample.Peer, "follower_entry", reason, portalSource.m_uid, b.Animal, fields: new (string, object?)[] { ("pos", position) }, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("animal_position", animal?.GetPosition()), ("portal_position", portalSource.GetPosition()), ("maximum_range_m", range), ("order", b.Order) } : null); } }
                continue;
            }
            journey.Followers.Enqueue(b.Animal); if (b.Order == TameOrder.Wait) journey.Waiting.Add(b.Animal);
            _following.Stop(b.Animal, "portal_reserved");
            _reserved.Add(b.Animal);
            if (b.Order == TameOrder.Wait) _commands.Bindings.Remove(b.Animal);
        }
        if (journey.Followers.Count > 0)
        {
            sample.Journey = journey; _diagnostics.Entry(journey, mode);
            TameTravelCommands.Feedback(sample.Peer, "Portal trip detected; move clear and stay within 48 metres of the destination exit until animal arrival is confirmed.");
        }
    }
    private static string? PortalPairRejection(ZDO source, out ZDO? destination)
    {
        var target = source.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
        destination = ZDOMan.instance.GetZDO(target);
        return TameTravelPortalRules.PairRejection(!target.IsNone(), destination != null, TameTravelRules.PortalRejection(destination),
            destination?.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) == source.m_uid,
            source.GetString(ZDOVars.s_tag), destination?.GetString(ZDOVars.s_tag) ?? "",
            destination == null ? float.NaN : (source.GetPosition() - destination.GetPosition()).sqrMagnitude,
            ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals));
    }
    private bool ResolvePortal(PlayerSample sample, PositionObservation current, float now, List<FollowBinding> bindings)
    {
        var witness = sample.PendingPortal!; var source = witness.Source;
        string? rejected = null;
        if (!TameTravelPortalRules.WitnessValid(now - witness.Observation.Time)) rejected = "source_witness_expired";
        else if (source.m_uid != witness.SourceId || !ReferenceEquals(ZDOMan.instance.GetZDO(witness.SourceId), source) || TameTravelRules.Portal(source) == null ||
            source.GetString(ZDOVars.s_tag) != witness.Tag || (source.GetPosition() - witness.Position).sqrMagnitude > .0001f ||
            Quaternion.Angle(source.GetRotation(), witness.Rotation) > .01f) rejected = "witnessed_portal_changed";
        var target = rejected == null ? source.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) : ZDOID.None;
        if (rejected == null && !witness.Destination.IsNone() && witness.Destination != target) rejected = "witnessed_link_changed";
        if (rejected != null)
        {
            sample.PendingPortal = null;
            { if (_diagnostics.SelectionEnabled) _diagnostics.Traversal(sample.Peer, "source_pair_wait", rejected, witness.SourceId); }
            return true;
        }
        if (witness.Destination.IsNone() && !target.IsNone()) witness.Destination = target;
        string? pairReason = PortalPairRejection(source, out var destination);
        if (pairReason != null) _diagnostics.PortalPair(sample.Peer, source, destination, pairReason);
        if (pairReason != null && !TameTravelPortalRules.CanWait(pairReason))
        { sample.PendingPortal = null; return true; }
        if (now - current.Time > 2) return true; // Never invent an arrival from a stale polling position.
        bool atExit = false;
        if (destination != null && TameTravelRules.Portal(destination) != null)
        {
            var exit = destination.GetPosition() + destination.GetRotation() * Vector3.forward * TameTravelRules.Portal(source)!.m_exitDistance + Vector3.up;
            var delta = current.Position - exit;
            atExit = new Vector2(delta.x, delta.z).sqrMagnitude <= 9 && Math.Abs(delta.y) <= 4;
        }
        if (!atExit && (current.Position - witness.Position).sqrMagnitude > 144)
        {
            sample.PendingPortal = null;
            { if (_diagnostics.SelectionEnabled) _diagnostics.Traversal(sample.Peer, "source_pair_wait", "movement_outside_witnessed_endpoints", source.m_uid); }
            return true;
        }
        if (pairReason != null) return true;
        sample.PendingPortal = null;
        BeginJourney(sample, source, destination!, witness.Observation, now, bindings, "retained_source_witness");
        if (sample.Journey != null) UpdateJourney(sample, current.Position, current.Time, now, fresh: true);
        return true;
    }
    private bool Pair(PortalJourney j)
    {
        var source = ZDOMan.instance.GetZDO(j.Source); var destination = ZDOMan.instance.GetZDO(j.Destination);
        return TameTravelRules.Portal(source) != null && TameTravelRules.Portal(destination) != null &&
            source!.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) == j.Destination && destination!.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) == j.Source &&
            source.GetString(ZDOVars.s_tag) == j.Tag && destination.GetString(ZDOVars.s_tag) == j.Tag &&
            (source.GetPosition() - j.SourcePosition).sqrMagnitude < 0.0001f && (destination.GetPosition() - j.DestinationPosition).sqrMagnitude < 0.0001f &&
            Quaternion.Angle(source.GetRotation(), j.SourceRotation) < 0.01f && Quaternion.Angle(destination.GetRotation(), j.DestinationRotation) < 0.01f &&
            !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals);
    }
    private void UpdateJourney(PlayerSample sample, Vector3 p, float observedAt, float now, bool fresh)
    {
        var j = sample.Journey!;
        if (!Pair(j) || j.Character != sample.Peer.m_characterID || j.Name != sample.Peer.m_playerName)
        { Cancel(sample, now, "pair_or_actor_changed", p); return; }
        var delta = p - j.Exit;
        bool atExit = new Vector2(delta.x, delta.z).sqrMagnitude <= 9 && Math.Abs(delta.y) <= 4;
        if (j.Arrived < 0)
        {
            if (now - j.Entered > _settings.TravelWindowSeconds.Value) { Cancel(sample, now, "arrival_timeout", p); return; }
            if (!fresh) return;
            string? arrivalRejection = TameTravelTraversalRules.ArrivalRejection(atExit, observedAt - j.Entered, (p - j.SourcePosition).sqrMagnitude);
            if (arrivalRejection == "arrival_too_early")
            {
                { if (_diagnostics.SelectionEnabled) { if (_diagnostics.TraversalReady(j.Peer, "arrival", arrivalRejection, j.Source)) _diagnostics.Traversal(j.Peer, "arrival", arrivalRejection, j.Source, fields: new (string, object?)[] { ("pos", p) }, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("entry_age_s", observedAt - j.Entered), ("expected_exit", j.Exit) } : null); } }
                return;
            }
            if (arrivalRejection != null) { Cancel(sample, now, arrivalRejection, p); return; }
            if (atExit)
            {
                j.Arrived = observedAt;
                { if (_diagnostics.SelectionEnabled) { if (_diagnostics.TraversalReady(j.Peer, "arrival", "linked_exit_observed", j.Source, transition: true)) _diagnostics.Traversal(j.Peer, "arrival", "linked_exit_observed", j.Source, fields: new (string, object?)[] { ("pos", p) }, transition: true, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("expected_exit", j.Exit), ("entry_age_s", observedAt - j.Entered) } : null); } }
            }
        }
        else
        {
            string? rejection = TameTravelTraversalRules.DestinationRejection(delta.sqrMagnitude, now - j.Entered, _settings.TravelWindowSeconds.Value, j.Moved > 0);
            if (rejection != null) { Cancel(sample, now, rejection, p); return; }
            if (j.Ready || now - j.Arrived < 2 || now - j.Entered < 8 || !PlayerWorldScope.Finite(j.Peer.m_refPos) || (j.Peer.m_refPos - p).sqrMagnitude > 16) return;
            if (j.Barrier == 0) j.Barrier = _authority.SendBarrier(j.Peer, null, now);
            else if (_authority.Echoed(j.Barrier))
            {
                j.Ready = true;
                { if (_diagnostics.SelectionEnabled) _diagnostics.Traversal(j.Peer, "destination", "readiness_confirmed", j.Source, transition: true); }
            }
        }
    }
    private void UpdateTransfer(AnimalTransfer t, float now)
    {
        var sample = _samples.TryGetValue(t.Journey.Peer.m_uid, out var found) ? found : null;
        string? reason = null;
        if (sample?.Journey != t.Journey) reason = "journey_changed";
        else if (!_rules.Actor(t.Journey.Peer, out var actor)) reason = "traveller_unavailable";
        else if (!Pair(t.Journey)) reason = "portal_pair_changed";
        else if (t.Journey.Arrived >= 0 && TameTravelTraversalRules.DestinationRejection((actor.GetPosition() - t.Journey.Exit).sqrMagnitude,
            now - t.Journey.Entered, _settings.TravelWindowSeconds.Value, t.Moved) is string destination) reason = destination;
        else if (ZDOMan.instance.GetZDO(t.Id) != t.Record || t.Record.m_uid != t.Id) reason = "animal_record_replaced";
        else if (_rules.AnimalRejection(t.Record) is string rejected) reason = rejected;
        else if (!t.Waiting && !_commands.FollowingAuthorized(t.Id, t.Journey.Peer, t.Journey.Character, t.Journey.Name)) reason = "follow_authorization_ended";
        else if (!TameTravelCommandRules.ConsentValid(t.Waiting ? TameOrder.Wait : TameOrder.Follow, t.Record.GetString(ZDOVars.s_follow), t.Journey.Name)) reason = "follow_changed";
        else if (TameTravelTraversalRules.PhaseExpired(t.Phase, t.PlacementStarted, now - t.PhaseTime)) reason = "phase_timeout";
        if (reason != null) { FinishTransfer(t, false, reason); return; }
        if (t.Phase == 0)
        {
            if (t.PreviousPeer == null || !t.PreviousPeer.IsReady()) { FinishTransfer(t, false, "source_owner_unavailable"); return; }
            if (!_authority.Echoed(t.Barrier)) return;
            if (!t.SourceStateObserved)
            {
                if (t.SourceEchoTime == 0) t.SourceEchoTime = now;
                if (now - t.SourceEchoTime >= 2) _authority.RequestSourceState(t, now);
                return;
            }
            t.Phase = 1; t.PhaseTime = now;
        }
        if (t.Phase == 1)
        {
            if (!t.Journey.Ready) return;
            if (!t.PlacementStarted) { t.PlacementStarted = true; t.PhaseTime = now; }
            if (_placementUsed) return;
            _placementUsed = true;
            if ((t.Record.GetPosition() - t.Source).sqrMagnitude > 144)
            { { if (_diagnostics.SelectionEnabled) _diagnostics.Traversal(t.Journey.Peer, "placement", "animal_moved_from_drain_source", t.Journey.Source, t.Id); } return; }
            if (!_placement.Find(t, _transfers.Values, _players.Presence.Peers, now, _settings.DebugLogging.Enabled, out var position))
            {
                { if (_diagnostics.SelectionEnabled) { if (_diagnostics.TraversalReady(t.Journey.Peer, "placement", _placement.WaitReason, t.Journey.Source, t.Id)) _diagnostics.Traversal(t.Journey.Peer, "placement", _placement.WaitReason, t.Journey.Source, t.Id, fields: new (string, object?)[] { ("pos", ZDOMan.instance.GetZDO(t.Journey.Character)?.GetPosition()) }, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("blocking_object", _placement.BlockingObject), ("phase_age_s", now - t.PhaseTime), ("expected_exit", t.Journey.Exit), ("search_cursor", t.PlacementCursor), ("candidates", t.PlacementCandidates), ("rejections", TameTravelDiagnostics.PlacementReasons(t)) } : null); } }
                return;
            }
            // The original record remains owned and fenced throughout the move.
            TameTravelPlacement.Move(t, position); t.Placement = position; t.Moved = true; t.Journey.Moved++;
            t.Phase = 2; t.PhaseTime = now;
            ZDOMan.instance.ForceSendZDO(t.Id);
            t.Barrier = _authority.SendBarrier(t.Journey.Peer, t.Record, now);
            return;
        }
        if (t.Phase == 2 && _authority.Echoed(t.Barrier) && now - t.PhaseTime >= 0.5f)
        { _authority.Handoff(t); t.PhaseTime = now; t.NextDestinationSend = now + 2; return; }
        if (t.Phase == 3 && !t.OwnerObserved) _authority.RefreshDestination(t, now);
        if (t.Phase == 3 && t.OwnerObserved)
        { t.Phase = 4; t.PhaseTime = now; return; }
        if (t.Phase == 4 && now - t.PhaseTime >= 2) FinishTransfer(t, true);
    }
    private void FinishTransfer(AnimalTransfer t, bool confirmed, string reason = "journey_cancelled")
    {
        TransferSnapshot? snapshot = null;
        if (_settings.DebugLogging.Enabled)
        {
            var world = ZDOMan.instance;
            bool same = world != null && ReferenceEquals(world.GetZDO(t.Id), t.Record) && t.Record.m_uid == t.Id && t.Record.IsValid();
            snapshot = new TransferSnapshot { Reason = confirmed ? "destination_owner_confirmed" : reason, SameRecord = same,
                Owner = same ? t.Record.GetOwner() : (long?)null, DataRevision = same ? t.Record.DataRevision : (uint?)null,
                OwnerRevision = same ? t.Record.OwnerRevision : (ushort?)null, Position = same ? t.Record.GetPosition() : (Vector3?)null,
                Follow = same ? t.Record.GetString(ZDOVars.s_follow) : null,
                Health = same && t.Record.GetFloat(ZDOVars.s_health, out float savedHealth) ? savedHealth : (float?)null,
                HealthSaved = same ? t.Record.GetFloat(ZDOVars.s_health, out _) : (bool?)null,
                Tamed = same ? t.Record.GetBool(ZDOVars.s_tamed) : (bool?)null, Rider = same ? t.Record.GetLong(ZDOVars.s_user) : (long?)null,
                PhaseAge = Time.unscaledTime - t.PhaseTime, BarrierEchoed = _authority.Echoed(t.Barrier) };
        }
        _transfers.Remove(t.Id); _reserved.Remove(t.Id); _authority.Release(t.Id, resetDestination: t.Moved && !confirmed);
        t.FollowContinues = !t.Waiting && _commands.FollowingAuthorized(t.Id, t.Journey.Peer, t.Journey.Character, t.Journey.Name);
        if (!confirmed) t.Journey.EndReason = reason;
        if (confirmed)
        {
            t.Journey.Confirmed++;
            if (t.FollowContinues) _following.Command(t.Journey.Peer, t.Record, TameOrder.Follow);
        }
        _diagnostics.Transfer(t, confirmed, snapshot);
    }
    private void Complete(PlayerSample sample, float now, string? reason = null)
    {
        var j = sample.Journey!; sample.Journey = null; sample.BlockedUntil = now + 3;
        sample.ReentryPortal = j.Moved > 0 ? j.Destination : j.Source;
        if (j.Moved == 0)
        {
            reason ??= j.EndReason ?? "no_animal_transfer_completed";
            { if (_diagnostics.SelectionEnabled) _diagnostics.Traversal(j.Peer, "journey_ended", reason, j.Source, transition: true); }
            DiagnosticLog.Write(_log, "TameTravel", $"Portal travel for {j.Name} ended before relocation.");
            TameTravelCommands.Feedback(j.Peer, TameTravelTraversalRules.CancellationMessage(reason) + " Following animals keep their permission; optional portal waits need a fresh command.");
            return;
        }
        DiagnosticLog.Write(_log, "TameTravel", j.Moved == j.Confirmed ? $"Moved {j.Confirmed} follower(s) for {j.Name}; destination ownership resumed." :
            $"Relocated {j.Moved} follower(s) for {j.Name}; destination ownership confirmed for {j.Confirmed}.");
        TameTravelCommands.Feedback(j.Peer, j.Moved == j.Confirmed ? $"Portal arrival confirmed for {j.Confirmed} animal(s)." :
            "Animal relocation was recorded but destination ownership was not fully confirmed; check the destination before retrying.");
    }
    private void Cancel(PlayerSample sample, float now, string reason = "session_or_competing_teleport", Vector3? position = null)
    {
        sample.PendingPortal = null;
        var j = sample.Journey; if (j == null) { sample.BlockedUntil = now + 3; return; }
        _diagnostics.Cancel(j, reason, position, now);
        foreach (var id in j.Followers) _reserved.Remove(id);
        j.Followers.Clear();
        _cancelTransfers.Clear(); foreach (var t in _transfers.Values) if (ReferenceEquals(t.Journey, j)) _cancelTransfers.Add(t);
        foreach (var t in _cancelTransfers) FinishTransfer(t, false, reason);
        Complete(sample, now, reason);
    }
    private void ClearJourneys() { foreach (var s in _samples.Values) Cancel(s, Time.unscaledTime); }
    private void Observe(ZRpc rpc, ZPackage packet)
    {
        if (_disposed) return;
        var peer = PlayerPeerLookup.Ready(_players.Presence.Peers, rpc);
        if (peer == null || !RoutedActivityReader.TryRead(packet, peer.m_uid, out var target, out int method)) return;
        if (method != PongMethod && method != TeleportMethod && method != TeleportPlayerMethod && method != CommandMethod && method != SayMethod && method != ChatMethod) return;
        if (!RoutedActivityReader.TryReadParameters(packet, peer.m_uid, out target, out method, out var parameters) || parameters == null) return;
        float now = Time.unscaledTime;
        if (method == PongMethod && target.IsNone())
        { float nonce = parameters.ReadSingle(); if (parameters.GetPos() == parameters.Size()) _authority.ObservePong(peer, nonce); return; }
        if (method == TeleportMethod || method == TeleportPlayerMethod)
        {
            var data = ZdoPackets.ReadRouted(packet);
            foreach (var s in _samples.Values) if (s.Character == target || target.IsNone() && (data.m_targetPeerID == 0 || data.m_targetPeerID == s.Peer.m_uid)) Cancel(s, now);
            return;
        }
        if (method != CommandMethod && method != SayMethod && method != ChatMethod) return;
        var routed = ZdoPackets.ReadRouted(packet);
        var key = (peer.m_uid, routed.m_msgID);
        if (_messagesSeen.ContainsKey(key) || _messagesSeen.Count >= 1024) return;
        _messagesSeen.Add(key, now);
        if (!_samples.TryGetValue(peer.m_uid, out var sample) || !ReferenceEquals(sample.Peer, peer) || now - sample.CommandTime < 1) return;
        sample.CommandTime = now;
        try { _commands.Observe(peer, target, method, parameters, now); }
        catch (EndOfStreamException) { /* Malformed command is not consent; native handling remains independent. */ }
    }
    private static void CompetingTeleport(Player __instance)
    {
        var c = _current; if (c == null) return;
        var id = __instance.GetZDOID();
        foreach (var s in c._samples.Values) if (s.Character == id) c.Cancel(s, Time.unscaledTime);
    }
    private static void AdministrativeTeleport(long targetPeerID)
    {
        var c = _current; if (c == null) return;
        if (targetPeerID == 0) { c.ClearJourneys(); return; }
        if (c._samples.TryGetValue(targetPeerID, out var s)) c.Cancel(s, Time.unscaledTime);
    }
    private static void BeforePlayerData(ZRpc rpc, out (ZNetPeer? Peer, ZDO? Actor, uint Revision, int Emote) __state)
    {
        __state = default;
        var c = _current;
        if (c == null || c._disposed) return;
        try
        {
            var peer = PlayerPeerLookup.Ready(c._players.Presence.Peers, rpc);
            if (peer != null && !peer.m_characterID.IsNone())
            {
                var actor = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (actor != null && actor.IsValid() && actor.GetOwner() == peer.m_uid)
                    __state = (peer, actor, actor.DataRevision, actor.GetInt(ZDOVars.s_emoteID));
            }
        }
        catch (Exception error) { c._fail(error); }
    }
    private static void AfterPlayerData((ZNetPeer? Peer, ZDO? Actor, uint Revision, int Emote) __state)
    {
        var c = _current;
        if (c == null || c._disposed) return;
        try { c.PlayerDataApplied(__state); }
        catch (Exception error) { c._fail(error); }
    }
    private void PlayerDataApplied((ZNetPeer? Peer, ZDO? Actor, uint Revision, int Emote) state)
    {
        var peer = state.Peer; var actor = state.Actor;
        if (peer == null || actor == null || !actor.IsValid() || actor.DataRevision <= state.Revision ||
            actor.m_uid != peer.m_characterID || actor.GetOwner() != peer.m_uid || !ReferenceEquals(ZDOMan.instance.GetZDO(peer.m_characterID), actor)) return;
        int emote = actor.GetInt(ZDOVars.s_emoteID);
        string command = actor.GetString(ZDOVars.s_emote);
        if (emote != state.Emote) _diagnostics.Command(peer, "emote_applied", command, counter: emote);
        string? actorReason = _rules.ActorRejection(peer, out var current);
        _diagnostics.ActorGate(peer, actorReason);
        if (actorReason != null || !ReferenceEquals(actor, current))
        { if (emote != state.Emote) _diagnostics.Command(peer, "rejected", command, actorReason, emote); return; }
        if (!_samples.TryGetValue(peer.m_uid, out var sample) || !ReferenceEquals(sample.Peer, peer) || sample.Character != peer.m_characterID)
        { if (emote != state.Emote) _diagnostics.Command(peer, "rejected", command, "player_sample_unavailable", emote); return; }
        if (!sample.PlayerDataObserved)
        { sample.PlayerDataObserved = true; _diagnostics.Command(peer, "player_data_applied", command, counter: emote); }
        bool watching = sample.Journey != null;
        if (!watching) foreach (var binding in _commands.Bindings.Values)
            if (ReferenceEquals(binding.Peer, peer) && binding.Character == sample.Character) { watching = true; break; }
        if (watching || emote > sample.Emote && TameTravelCommandRules.Emote(command, out _))
            sample.Positions.Add(new PositionObservation(actor.GetPosition(), Time.unscaledTime, actor.DataRevision));
        else { sample.Position = actor.GetPosition(); sample.Time = Time.unscaledTime; }
        if (emote <= sample.Emote) return;
        sample.Emote = emote;
        float now = Time.unscaledTime;
        if (!TameTravelCommandRules.Emote(command, out var order)) return;
        if (now - sample.CommandTime < 1) { _diagnostics.Command(peer, "rejected", command, "command_rate_limited", emote); return; }
        sample.CommandTime = now;
        _commands.Request(peer, order, now);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        var errors = new List<Exception>();
        foreach (Action step in new Action[] { () => _messages.Dispose(), () => _hooks.UnpatchSelf(), () => _following.Dispose(), ClearJourneys, () => _authority.Dispose(), () => _placement.Dispose() })
            try { step(); } catch (Exception error) { errors.Add(error); }
        if (ReferenceEquals(_current, this)) _current = null;
        _samples.Clear(); _commands.Bindings.Clear(); _reserved.Clear(); _messagesSeen.Clear(); _transfers.Clear();
        if (errors.Count > 0) throw new AggregateException("TameTravel cleanup failed.", errors);
    }
}
