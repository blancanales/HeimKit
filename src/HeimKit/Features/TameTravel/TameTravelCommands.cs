using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HeimKit.Features.TameTravel.Models;
using HeimKit.Shared;
using HeimKit.Shared.Players;
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
    public readonly Dictionary<ZDOID, FollowBinding> Bindings = new();
    public TameTravelCommands(TameTravelRules rules, TameTravelSettings settings, ManualLogSource log, Func<ZDOID, bool> reserved, Action<ZNetPeer, ZDO, TameOrder> confirmed, TameTravelDiagnostics diagnostics)
    { _rules = rules; _settings = settings; _log = log; _reserved = reserved; _confirmed = confirmed; _diagnostics = diagnostics; }
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
            if (animal.GetString(ZDOVars.s_follow).Length == 0) Bind(peer, animal, now, pending: true, TameOrder.Follow);
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
    public void Request(ZNetPeer peer, TameOrder order, float now)
    {
        _diagnostics.Command(peer, "request_received", order.ToString());
        string? actorReason = _rules.ActorRejection(peer, out var actor);
        if (actorReason != null) { Reject(peer, actorReason, "Your character is not ready for animal commands."); return; }
        var nearby = TameTravelRules.Nearby(actor.GetPosition());
        if (nearby == null) { Reject(peer, "scope_over_capacity", "Animal scope is unavailable; try again outside this dense area."); return; }
        var eligible = nearby.Where(z => _rules.Animal(z) && (z.GetPosition() - actor.GetPosition()).sqrMagnitude <= _settings.CommandRangeMeters.Value * _settings.CommandRangeMeters.Value).ToArray();
        if (_diagnostics.SelectionEnabled)
        {
            // Include rejected supported animals so an empty selection has evidence.
            var supported = nearby.Where(_rules.Supported).Select(z => (Record: z, Distance: Vector3.Distance(z.GetPosition(), actor.GetPosition())))
                .OrderBy(p => PlayerWorldScope.Finite(p.Distance) ? p.Distance : float.MaxValue).ToArray();
            _diagnostics.Selection(peer, order, nearby.Count, supported.Length, eligible.Length);
            foreach (var candidate in supported.Take(3))
                _diagnostics.Candidate(peer, order, candidate.Record, candidate.Distance, _settings.CommandRangeMeters.Value,
                    _rules.AnimalRejection(candidate.Record) ?? (candidate.Distance > _settings.CommandRangeMeters.Value ? "outside_command_range" : "eligible"));
        }
        if (order == TameOrder.Stay)
        {
            var own = eligible.Where(z => TameTravelCommandRules.StopAssociated(z.GetString(ZDOVars.s_follow), peer.m_playerName,
                Bindings.TryGetValue(z.m_uid, out var binding) && ReferenceEquals(binding.Peer, peer) && binding.Character == peer.m_characterID))
                .OrderBy(z => z.m_uid.ToString()).Take(17).ToArray();
            if (own.Length == 0) { Reject(peer, "no_associated_animals", "No following or waiting animal associated with you is in command range."); return; }
            int busy = 0;
            foreach (var animal in own.Take(16))
                if (_reserved(animal.m_uid)) busy++; else Apply(peer, animal, order, now);
            if (busy > 0) Reject(peer, "animal_handoff_busy", "Some animals are changing ownership; wait for their result before repeating /nonono.");
            if (own.Length > 16) Feedback(peer, "Stay addresses sixteen animals at a time; repeat /nonono for the remaining followers.");
            return;
        }
        var candidates = eligible
            .Select(z => (Record: z, Distance: Vector3.Distance(z.GetPosition(), actor.GetPosition())))
            .OrderBy(p => p.Distance).Take(2).ToArray();
        if (candidates.Length == 0) { Reject(peer, "no_eligible_tame", "No eligible tame in command range."); return; }
        if (candidates.Length == 2 && candidates[1].Distance - candidates[0].Distance < 0.5f) { Reject(peer, "nearest_animal_ambiguous", "Move closer to one animal; selection is ambiguous."); return; }
        var selected = candidates[0].Record;
        if (_reserved(selected.m_uid)) { Reject(peer, "animal_handoff_busy", "That animal is changing ownership; wait for its result."); return; }
        Apply(peer, selected, order, now);
    }
    private void Apply(ZNetPeer peer, ZDO selected, TameOrder order, float now)
    {
        bool associated = Bindings.TryGetValue(selected.m_uid, out var existing) && ReferenceEquals(existing.Peer, peer) && existing.Character == peer.m_characterID;
        if (existing != null && !associated) { Reject(peer, "foreign_association", "That animal has another player's active command."); return; }
        if (existing?.Pending == true) { Reject(peer, "command_pending", "Waiting for the previous animal command to confirm."); return; }
        string follow = selected.GetString(ZDOVars.s_follow);
        if (follow.Length != 0 && follow != peer.m_playerName) { Reject(peer, "foreign_following", "That animal is following another player."); return; }
        // A fresh authenticated stay/wait can address native following after a previous trip
        // consumed its binding. A saved name alone still never starts a portal journey.
        if (order != TameOrder.Follow && !associated && follow != peer.m_playerName)
        { Reject(peer, "association_required", order == TameOrder.Stay ? "Animal is already staying; no portal travel authorized." : "Use !follow or /comehere to associate this animal first."); return; }
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
        Bindings[animal.m_uid] = new FollowBinding { Peer = peer, Character = peer.m_characterID, Animal = animal.m_uid,
            Name = peer.m_playerName, Consent = now, Requested = now, Pending = pending, Order = order, WaitPosition = animal.GetPosition() };
        return true;
    }
    public void Tick(float now)
    {
        foreach (var pair in Bindings.ToArray())
        {
            var b = pair.Value; var animal = ZDOMan.instance.GetZDO(pair.Key);
            string? reason = _rules.ActorRejection(b.Peer, out _);
            if (reason == null && b.Peer.m_characterID != b.Character) reason = "character_replaced";
            if (reason == null && b.Name != b.Peer.m_playerName) reason = "player_name_changed";
            reason ??= _rules.AnimalRejection(animal);
            if (reason == null && now - b.Consent > _settings.FollowConsentSeconds.Value) reason = "consent_expired";
            if (reason != null) { RemoveBinding(pair.Key, b, reason, now, animal); continue; }
            string follow = animal!.GetString(ZDOVars.s_follow);
            if (b.Pending)
            {
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
        Bindings.Remove(id);
        _diagnostics.Traversal(binding.Peer, "consent_ended", reason, animal: id, fields: new (string, object?)[] {
            ("order", binding.Order), ("consent_age_s", now - binding.Consent), ("following", animal?.GetString(ZDOVars.s_follow)),
            ("wait_position", binding.WaitPosition), ("animal_position", animal?.GetPosition()), ("maximum_wait_range_m", _settings.FollowerRangeMeters.Value) }, transition: true);
    }
    private void Result(ZNetPeer peer, ZDO animal, TameOrder order)
    {
        _diagnostics.Command(peer, "confirmed", order.ToString(), animal: animal.m_uid);
        string description = order == TameOrder.Follow ? "follow" : order == TameOrder.Stay ? "stay" : "portal wait";
        DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Result, $"Confirmed {description} for {peer.m_playerName}.");
        string label = ZNetScene.instance.GetPrefab(animal.GetPrefab())?.name ?? "Animal";
        Feedback(peer, order == TameOrder.Follow ? label + " is following; one portal trip authorized." :
            order == TameOrder.Stay ? label + " is staying; portal travel cancelled." : label + " is waiting; one nearby portal trip authorized.");
        _confirmed(peer, animal, order);
    }
    private void Reject(ZNetPeer peer, string reason, string text, bool failed = false)
    {
        _diagnostics.Command(peer, "rejected", null, reason);
        if (failed) DiagnosticLog.Write(_log, "TameTravel", DiagnosticEvent.Result, $"Animal command for {peer.m_playerName} was not confirmed; no toggle was retried.", level: LogLevel.Warning);
        Feedback(peer, text);
    }
    public static void Feedback(ZNetPeer peer, string text)
    {
        if (ZRoutedRpc.instance == null || !peer.IsReady() || peer.m_characterID.IsNone()) return;
        ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, peer.m_characterID, "Message", (int)MessageHud.MessageType.TopLeft, "TameTravel: " + text, 0);
    }
}
