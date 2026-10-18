using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace HeimKit.Shared.Players;

// Character sessions share the presence census, independently of feature activation and AFK inference.
public sealed class PlayerConnectionNotices
{
    private readonly ManualLogSource _logger;
    private readonly Dictionary<ZNetPeer, (long Id, string Name)> _sessions = new();
    public PlayerConnectionNotices(ManualLogSource logger) => _logger = logger;

    public void Observe(IReadOnlyList<ZNetPeer> connected, IEnumerable<ZNetPeer> joined)
    {
        var present = new HashSet<ZNetPeer>(connected);
        foreach (var prior in _sessions.ToArray())
        {
            if (present.Contains(prior.Key)) continue;
            _sessions.Remove(prior.Key);
            Write(prior.Value.Id, prior.Value.Name, "disconnected");
        }
        foreach (var peer in joined)
        {
            string name = CleanName(peer.m_playerName);
            bool known = _sessions.TryGetValue(peer, out var prior);
            if (known && name.Length == 0) name = prior.Name;
            long id = known ? prior.Id : peer.m_uid;
            _sessions[peer] = (id, name);
            if (!known) Write(id, name, "connected");
        }
    }

    private static string CleanName(string? name) => new string((name ?? "").Take(128)
        .Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
    private void Write(long id, string name, string action)
    {
        string identity = name.Length == 0 ? "Peer " + id : "Player \"" + name.Replace("\"", "\\\"") + "\" (peer " + id + ")";
        DiagnosticLog.Write(_logger, "PlayerPresence", DiagnosticEvent.Lifecycle, identity + " " + action + ".");
    }
    public void Clear() => _sessions.Clear();
}
