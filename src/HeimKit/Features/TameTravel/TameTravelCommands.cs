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
using BepInEx.Logging;
using HeimKit.Features.TameTravel.Models;
using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelCommands
{
    private readonly TameTravelRules _rules;
    private readonly TameTravelSettings _settings;
    private readonly ManualLogSource _log;
    private readonly Func<ZDOID, bool> _reserved;
    private readonly Action<ZNetPeer, ZDO, TameOrder> _confirmed;
    private readonly TameTravelDiagnostics _diagnostics;
    private readonly TameTravelStops? _stops;
    public readonly Dictionary<ZDOID, FollowBinding> Bindings = new();
    public bool FollowingAuthorized(ZDOID id, ZNetPeer peer, ZDOID character, string name)
        => Bindings.TryGetValue(id, out var binding) && !binding.Pending && binding.Order == TameOrder.Follow &&
            ReferenceEquals(binding.Peer, peer) && binding.Character == character && binding.Name == name;
    private readonly List<KeyValuePair<ZDOID, FollowBinding>> _snapshot = new();
    public TameTravelCommands(TameTravelRules rules, TameTravelSettings settings, ManualLogSource log, Func<ZDOID, bool> reserved, Action<ZNetPeer, ZDO, TameOrder> confirmed, TameTravelDiagnostics diagnostics, TameTravelStops? stops = null)
    { _rules = rules; _settings = settings; _log = log; _reserved = reserved; _confirmed = confirmed; _diagnostics = diagnostics; _stops = stops; }
    public void Observe(ZNetPeer peer, ZDOID target, int method, ZPackage parameters, float now)
    {
        if (!_rules.Actor(peer, out var actor)) return;
        if (method == "Command".GetStableHashCode())
        {
            var animal = ZDOMan.instance.GetZDO(target);
            if (!_rules.Animal(animal) || _reserved(target) || (animal!.GetPosition() - actor.GetPosition()).sqrMagnitude > _settings.CommandRangeMeters.Value * _settings.CommandRangeMeters.Value) return;
            var id = parameters.ReadZDOID(); parameters.ReadBool();
            if (id != peer.m_characterID || parameters.GetPos() != parameters.Size()) return;
            // A native toggle invalidates any earlier association, including another player's.
            Bindings.Remove(target);
            TameTravelFollowState.Untrack(target);
            if (animal.GetString(ZDOVars.s_follow).Length == 0) Bind(peer, animal, now, pending: true, TameOrder.Follow);
            else if (animal.GetString(ZDOVars.s_follow) == peer.m_playerName) _stops?.QueueCleanup(animal, peer.m_playerName, now);
            return;
        }
        if (method != "Say".GetStableHashCode() && method != "ChatMessage".GetStableHashCode()) return;
        if (method == "Say".GetStableHashCode() && target != peer.m_characterID || method == "ChatMessage".GetStableHashCode() && !target.IsNone()) return;
        if (method == "ChatMessage".GetStableHashCode()) parameters.ReadVector3();
        int type = parameters.ReadInt();
        // Native UserInfo contains name and platform ID. Neither supplied string authenticates the sender.
        parameters.ReadString(); parameters.ReadString(); string text = parameters.ReadString().Trim();
        if (parameters.GetPos() != parameters.Size() || type < 0 || type > 2) return;
        if (text.Equals("!tame", StringComparison.OrdinalIgnoreCase)) { Feedback(peer, "!follow: follow. !stay: stop/cancel. !wait: stop/keep one portal trip. Solo input: /comehere, /nonono, /wave."); return; }
        if (TameTravelCommandRules.Chat(text, out var order)) Request(peer, order, now);
    }
    private readonly List<ZDO> _eligible = new(), _own = new();
    private readonly List<(ZDO Record, float Distance)> _supported = new();
    private static readonly Comparison<ZDO> AnimalOrder = (a, b) => NativeIdentity.Compare(a.m_uid, b.m_uid);
    private static readonly Comparison<(ZDO Record, float Distance)> DistanceOrder = (a, b) =>
        (PlayerWorldScope.Finite(a.Distance) ? a.Distance : float.MaxValue).CompareTo(PlayerWorldScope.Finite(b.Distance) ? b.Distance : float.MaxValue);
    public void Request(ZNetPeer peer, TameOrder order, float now)
    {
        _diagnostics.Command(peer, "request_received", order.ToString());
        string? actorReason = _rules.ActorRejection(peer, out var actor);
        if (actorReason != null) { Reject(peer, actorReason, "Your character is not ready for animal commands."); return; }
        var nearby = TameTravelRules.Nearby(actor.GetPosition());
        if (nearby == null) { Reject(peer, "scope_over_capacity", "Animal scope is unavailable; try again outside this dense area."); return; }
        float range = TameTravelCommandRules.SelectionRange(order, _settings.CommandRangeMeters.Value);
        var eligible = _eligible; eligible.Clear();
        foreach (var z in nearby) if (_rules.Animal(z) && (z.GetPosition() - actor.GetPosition()).sqrMagnitude <= range * range) eligible.Add(z);
        _own.Clear();
        if (order == TameOrder.Stay) foreach (var z in eligible) if (StopAssociated(peer, z)) _own.Add(z);
        if (_diagnostics.SelectionEnabled)
        {
            _supported.Clear();
            foreach (var z in nearby) if (_rules.Supported(z)) _supported.Add((z, Vector3.Distance(z.GetPosition(), actor.GetPosition())));
            _supported.Sort(DistanceOrder);
            _diagnostics.Selection(peer, order, nearby.Count, _supported.Count, order == TameOrder.Stay ? _own.Count : eligible.Count);
            for (int i = 0; i < Math.Min(3, _supported.Count); i++)
            {
                var candidate = _supported[i];
                _diagnostics.Candidate(peer, order, candidate.Record, candidate.Distance, range,
                    _rules.AnimalRejection(candidate.Record) ?? (candidate.Distance > range ? "outside_command_range" :
                        order == TameOrder.Stay && !StopAssociated(peer, candidate.Record) ?
                            candidate.Record.GetString(ZDOVars.s_follow).Length == 0 ? "not_following_you" : "following_another_player" : "eligible"));
            }
        }
        if (order == TameOrder.Stay)
        {
            _own.Sort(AnimalOrder);
            if (_own.Count == 0) { Reject(peer, "no_associated_animals", "No following or waiting animal associated with you is in command range."); return; }
            for (int i = 0; i < Math.Min(16, _own.Count); i++) QueueStop(peer, _own[i], now);
            if (_own.Count > 16) Feedback(peer, "Stay addresses sixteen animals at a time; repeat /nonono for the remaining followers.");
            return;
        }
        ZDO? selected = null; float first = float.MaxValue, second = float.MaxValue;
        foreach (var candidate in eligible)
        {
            float distance = Vector3.Distance(candidate.GetPosition(), actor.GetPosition());
            if (distance < first) { second = first; first = distance; selected = candidate; }
            else if (distance < second) second = distance;
        }
        if (selected == null) { Reject(peer, "no_eligible_tame", "No eligible tame in command range."); return; }
        if (second - first < .5f) { Reject(peer, "nearest_animal_ambiguous", "Move closer to one animal; selection is ambiguous."); return; }
        if (_reserved(selected.m_uid)) { Reject(peer, "animal_handoff_busy", "That animal is changing ownership; wait for its result."); return; }
        Apply(peer, selected, order, now);
    }
    private bool StopAssociated(ZNetPeer peer, ZDO animal)
    {
        bool bound = Bindings.TryGetValue(animal.m_uid, out var binding) && ReferenceEquals(binding.Peer, peer) && binding.Character == peer.m_characterID;
        string follow = animal.GetString(ZDOVars.s_follow);
        return TameTravelCommandRules.StopAssociated(follow, peer.m_playerName, bound, binding?.ReclaimName ?? "", binding?.Pending ?? false);
    }
    private void Apply(ZNetPeer peer, ZDO selected, TameOrder order, float now)
    {
        if (_stops?.Contains(selected.m_uid) == true)
        { Reject(peer, "stop_pending", "Waiting for the animal's stop handshake to finish."); return; }
        bool associated = Bindings.TryGetValue(selected.m_uid, out var existing) && ReferenceEquals(existing.Peer, peer) && existing.Character == peer.m_characterID;
        if (existing != null && !associated)
        {
            if (_rules.ConnectedPeer(existing.Peer)) { Reject(peer, "foreign_association", "That animal has another player's active command."); return; }
            Bindings.Remove(selected.m_uid); existing = null;
        }
        if (existing?.Pending == true) { Reject(peer, "command_pending", "Waiting for the previous animal command to confirm."); return; }
        string follow = selected.GetString(ZDOVars.s_follow);
        if (follow.Length != 0 && follow != peer.m_playerName)
        {
            if (order != TameOrder.Follow || _rules.ConnectedName(follow) || _stops == null)
            { Reject(peer, "foreign_following", "That animal is following another connected player, or its owner cannot be verified."); return; }
            if (!Bind(peer, selected, now, pending: true, order)) return;
            var reclaim = Bindings[selected.m_uid]; reclaim.ReclaimName = follow; reclaim.CommandSent = false;
            TameTravelFollowState.Mark(selected, follow);
            Feedback(peer, "Clearing the absent player's saved following before starting yours.");
            _diagnostics.Command(peer, "reclaim_queued", "Follow", "saved_follower_offline", animal: selected.m_uid);
            return;
        }
        // A fresh authenticated command can address native following after reconnect.
        // A saved name alone still never starts a portal journey.
        if (order != TameOrder.Follow && !associated && follow != peer.m_playerName)
        { Reject(peer, "association_required", order == TameOrder.Stay ? "Animal is already staying; no portal travel authorized." : "Use !follow or /comehere to associate this animal first."); return; }
        if (order == TameOrder.Wait && _stops != null)
        { Bind(peer, selected, now, pending: true, order); return; }
        string expected = TameTravelCommandRules.ExpectedFollow(order, peer.m_playerName);
        if (follow == expected)
        {
            if (order == TameOrder.Stay) Bindings.Remove(selected.m_uid);
            else if (!Bind(peer, selected, now, pending: false, order)) return;
            Result(peer, selected, order); return;
        }
        long owner = selected.GetOwner();
        if (owner == 0) { Reject(peer, "owner_unassigned", "Animal ownership is not ready; try again shortly."); return; }
        if (!Bind(peer, selected, now, pending: true, order)) return;
        ZRoutedRpc.instance.InvokeRoutedRPC(owner, selected.m_uid, "Command", peer.m_characterID, false);
        _diagnostics.Command(peer, "native_toggle_sent", order.ToString(), animal: selected.m_uid);
    }
    private bool Bind(ZNetPeer peer, ZDO animal, float now, bool pending, TameOrder order)
    {
        if (Bindings.Count >= 512 && !Bindings.ContainsKey(animal.m_uid)) { Reject(peer, "binding_capacity", "Animal command capacity reached; try again later."); return false; }
        if (order == TameOrder.Follow) TameTravelFollowState.Mark(animal, peer.m_playerName);
        Bindings[animal.m_uid] = new FollowBinding { Peer = peer, Character = peer.m_characterID, Animal = animal.m_uid,
            Name = peer.m_playerName, Consent = now, Requested = now, Pending = pending, Order = order, WaitPosition = animal.GetPosition() };
        return true;
    }
    private void QueueStop(ZNetPeer peer, ZDO animal, float now)
    {
        // Revoke portal authority immediately. The controller sees the changed binding and releases its
        // transfer fence before the native stop is sent; a running follow-spacing session instead keeps
        // server ownership so the stop reaches the server's own instance and is released once confirmed.
        Bindings.TryGetValue(animal.m_uid, out var previous);
        if (previous != null && (!ReferenceEquals(previous.Peer, peer) || previous.Character != peer.m_characterID))
        { Reject(peer, "foreign_association", "That animal has another player's active command."); return; }
        if (previous != null && previous.Pending && previous.Order == TameOrder.Stay)
        { Feedback(peer, "The stop request is already pending."); return; }
        if (!Bind(peer, animal, now, pending: true, TameOrder.Stay)) return;
        Bindings[animal.m_uid].CommandSent = false;
        Bindings[animal.m_uid].ReclaimName = previous?.ReclaimName ?? "";
        Bindings[animal.m_uid].AwaitingEarlierFollow = previous != null &&
            TameTravelCommandRules.AwaitingEarlierFollow(previous.Pending, previous.Order, previous.CommandSent);
        if (_stops == null) _confirmed(peer, animal, TameOrder.Stay);
        _diagnostics.Command(peer, "stop_queued", "Stay", Bindings[animal.m_uid].AwaitingEarlierFollow ? "earlier_follow_pending" : "ownership_and_stop_pending", animal: animal.m_uid);
        Feedback(peer, "Following permission cancelled; confirming animal stay.");
    }
    public void Tick(float now)
    {
        _snapshot.Clear(); foreach (var pair in Bindings) _snapshot.Add(pair);
        foreach (var pair in _snapshot)
        {
            var b = pair.Value; var animal = ZDOMan.instance.GetZDO(pair.Key);
            string? reason = _rules.ActorRejection(b.Peer, out _);
            if (reason == null && b.Peer.m_characterID != b.Character) reason = "character_replaced";
            if (reason == null && b.Name != b.Peer.m_playerName) reason = "player_name_changed";
            reason ??= _rules.AnimalRejection(animal);
            if (reason == null && TameTravelCommandRules.ConsentExpired(b.Order, now - b.Consent, _settings.FollowConsentSeconds.Value)) reason = "consent_expired";
            if (reason != null) { RemoveBinding(pair.Key, b, reason, now, animal); continue; }
            string follow = animal!.GetString(ZDOVars.s_follow);
            if (b.Pending && b.ReclaimName.Length != 0 && _stops != null)
            {
                if (_rules.ConnectedName(b.ReclaimName) || follow.Length != 0 && follow != b.ReclaimName)
                { Bindings.Remove(pair.Key); Reject(b.Peer, "reclaim_owner_changed", "The former follower reconnected or the animal changed following; command cancelled."); continue; }
                if (!_stops.Contains(pair.Key) && _reserved(pair.Key)) continue;
                if (_stops.TryStop(animal, b.ReclaimName, now))
                {
                    b.ReclaimName = ""; b.Requested = now;
                    if (b.Order == TameOrder.Follow) TameTravelFollowState.Mark(animal, b.Name);
                    else { b.Pending = false; Result(b.Peer, animal, b.Order); Bindings.Remove(pair.Key); }
                }
                else if (now - b.Requested > 15)
                { Bindings.Remove(pair.Key); Reject(b.Peer, "reclaim_stop_timeout", "The old following could not be cleared; follow was not started.", failed: true); }
                continue;
            }
            if (b.Pending && b.Order != TameOrder.Follow && _stops != null)
            {
                if (!_stops.Contains(pair.Key) && _reserved(pair.Key)) continue;
                if (_stops.TryStop(animal, b.Name, now))
                { b.Pending = false; Result(b.Peer, animal, b.Order); if (b.Order == TameOrder.Stay) Bindings.Remove(pair.Key); }
                else if (now - b.Requested > 15)
                { Bindings.Remove(pair.Key); Reject(b.Peer, "stop_handshake_timeout", "Stay could not be confirmed; retry /nonono.", failed: true); }
                continue;
            }
            if (b.Pending)
            {
                if (!b.CommandSent)
                {
                    if (follow.Length != 0 && follow != b.Name) { RemoveBinding(pair.Key, b, "follow_state_changed", now, animal); continue; }
                    if (_reserved(pair.Key)) continue;
                    // An earlier follow toggle may still arrive. Do not confirm an empty
                    // saved state as stay until that request has resolved, or toggle blindly.
                    if (b.AwaitingEarlierFollow)
                    {
                        if (follow == b.Name) b.AwaitingEarlierFollow = false;
                        else
                        {
                            if (now - b.Requested > 5)
                            { Bindings.Remove(pair.Key); Reject(b.Peer, "earlier_follow_unconfirmed", "Following permission cancelled, but the earlier command did not resolve; stay is unconfirmed. Try /nonono again.", failed: true); }
                            continue;
                        }
                    }
                    if (follow != TameTravelCommandRules.ExpectedFollow(b.Order, b.Name))
                    {
                        long owner = animal.GetOwner();
                        if (owner == 0)
                        {
                            if (now - b.Requested > 5) { Bindings.Remove(pair.Key); Reject(b.Peer, "stop_owner_unavailable", "Portal travel cancelled, but stay could not be confirmed; try /nonono again.", failed: true); }
                            continue;
                        }
                        ZRoutedRpc.instance.InvokeRoutedRPC(owner, pair.Key, "Command", b.Character, false);
                        b.CommandSent = true; b.Requested = now;
                        _diagnostics.Command(b.Peer, "native_toggle_sent", b.Order.ToString(), animal: pair.Key);
                        continue;
                    }
                }
                if (follow == TameTravelCommandRules.ExpectedFollow(b.Order, b.Name))
                { b.Pending = false; Result(b.Peer, animal, b.Order); if (b.Order == TameOrder.Stay) Bindings.Remove(pair.Key); }
                else if (now - b.Requested > 5) { Bindings.Remove(pair.Key); Reject(b.Peer, "native_command_timeout", "Animal command was not confirmed; no toggle was retried.", failed: true); }
            }
            else if (!TameTravelCommandRules.ConsentValid(b.Order, follow, b.Name)) RemoveBinding(pair.Key, b, "follow_state_changed", now, animal);
            else if (b.Order == TameOrder.Wait && (animal.GetPosition() - b.WaitPosition).sqrMagnitude > _settings.FollowerRangeMeters.Value * _settings.FollowerRangeMeters.Value)
                RemoveBinding(pair.Key, b, "animal_left_wait_area", now, animal);
        }
    }
    private void RemoveBinding(ZDOID id, FollowBinding binding, string reason, float now, ZDO? animal)
    {
        if (_stops != null && animal != null && _rules.Animal(animal) &&
            (binding.Order == TameOrder.Follow || binding.Pending) && reason != "follow_state_changed")
            _stops.QueueCleanup(animal, binding.ReclaimName.Length != 0 ? binding.ReclaimName : binding.Name, now);
        else TameTravelFollowState.Untrack(id);
        Bindings.Remove(id);
        { if (_diagnostics.TraversalReady(binding.Peer, "consent_ended", reason, animal: id, transition: true)) _diagnostics.Traversal(binding.Peer, "consent_ended", reason, animal: id, fields: null, transition: true, ready: true, verboseFields: _diagnostics.VerboseAfterNormalEnabled ? new (string, object?)[] { ("order", binding.Order), ("consent_age_s", now - binding.Consent), ("following", animal?.GetString(ZDOVars.s_follow)), ("wait_position", binding.WaitPosition), ("animal_position", animal?.GetPosition()), ("maximum_wait_range_m", _settings.FollowerRangeMeters.Value) } : null); }
    }
    internal void ClearForShutdown()
    {
        if (_stops == null || ZDOMan.instance == null) return;
        foreach (var binding in Bindings.Values)
        {
            var animal = ZDOMan.instance.GetZDO(binding.Animal);
            if (animal != null) _stops.ClearForShutdown(animal, binding.ReclaimName.Length != 0 ? binding.ReclaimName : binding.Name);
        }
    }
    private void Result(ZNetPeer peer, ZDO animal, TameOrder order)
    {
        _diagnostics.Command(peer, "confirmed", order.ToString(), animal: animal.m_uid);
        string description = order == TameOrder.Follow ? "follow" : order == TameOrder.Stay ? "stay" : "portal wait";
        DiagnosticLog.Write(_log, "TameTravel", $"Confirmed {description} for {peer.m_playerName}.");
        string label = PrefabRegistry.Current.Prefab(animal.GetPrefab())?.name ?? "Animal";
        Feedback(peer, order == TameOrder.Follow ? label + " is following, including through portals; /nonono stops following." :
            order == TameOrder.Stay ? label + " is staying; portal travel cancelled." : label + " is waiting; one nearby portal trip authorized.");
        _confirmed(peer, animal, order);
    }
    private void Reject(ZNetPeer peer, string reason, string text, bool failed = false)
    {
        _diagnostics.Command(peer, "rejected", null, reason);
        if (failed) DiagnosticLog.Write(_log, "TameTravel", $"Animal command for {peer.m_playerName} was not confirmed; no toggle was retried.", level: LogLevel.Warning);
        Feedback(peer, text);
    }
    public static void Feedback(ZNetPeer peer, string text)
    {
        if (ZRoutedRpc.instance == null || !peer.IsReady() || peer.m_characterID.IsNone()) return;
        ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, peer.m_characterID, "Message", (int)MessageHud.MessageType.TopLeft, "TameTravel: " + text, 0);
    }
}
