using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HeimKit.Shared.Players.Models;

namespace HeimKit.Shared.Players;

// Ordinary AFK transitions belong to the detector, independently of optional debug samples.
public sealed class PlayerActivityNotices
{
    private readonly ManualLogSource _logger;
    private readonly Dictionary<long, (long Session, string Name)> _idle = new();
    public PlayerActivityNotices(ManualLogSource logger) => _logger = logger;

    public void Observe(IReadOnlyList<PlayerActivityView> players, int? joinedCount)
    {
        var current = new Dictionary<long, PlayerActivityView>();
        foreach (var player in players.Take(4096))
            if (!current.ContainsKey(player.Peer)) current.Add(player.Peer, player);
        int count = current.Values.Count(player => player.Known && player.Alive && player.Idle);
        foreach (var prior in _idle.ToArray())
        {
            current.TryGetValue(prior.Key, out var player);
            if (player != null && player.Session == prior.Value.Session && player.Known && player.Alive && player.Idle) continue;
            _idle.Remove(prior.Key);
            string reason = player == null ? "disconnected or no longer observed" : player.Session != prior.Value.Session ? "player session changed" :
                !player.Known ? "player data unavailable" : !player.Alive ? "player died" : player.InBed ? "entered a bed" :
                player.Reason == "join_or_observation_grace" ? "activity tracking restarted" : "activity detected";
            Write(prior.Key, prior.Value.Name, "left AFK status (" + reason + ")", count, joinedCount);
        }
        foreach (var player in current.Values)
        {
            if (!player.Known || !player.Alive || !player.Idle) continue;
            bool entered = !_idle.ContainsKey(player.Peer);
            _idle[player.Peer] = (player.Session, player.Name);
            if (entered) Write(player.Peer, player.Name, "became AFK", count, joinedCount);
        }
    }

    public void Reset(string reason, int? joinedCount)
    {
        var previous = _idle.ToArray(); _idle.Clear();
        foreach (var player in previous)
            Write(player.Key, player.Value.Name, "left AFK status (" + reason + ")", joinedCount == 0 ? 0 : (int?)null, joinedCount);
    }

    private void Write(long peer, string name, string transition, int? count, int? joinedCount)
    {
        name = new string((name ?? "").Take(128).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        string who = name.Length == 0 ? "Peer " + peer : "Player \"" + name.Replace("\"", "\\\"") + "\" (peer " + peer + ")";
        string population = count.HasValue ? count.Value + (joinedCount.HasValue ? " of " + joinedCount.Value + " joined" : "; joined count unavailable") : "unavailable (tracking stopped)";
        DiagnosticLog.Write(_logger, "PlayerActivity", DiagnosticEvent.Lifecycle, who + " " + transition + ". Current AFK players: " + population + ".");
    }
}
