using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace HeimKit.Shared.Players;

// Character sessions share the presence census, independently of feature activation and AFK inference.
public sealed class PlayerConnectionNotices
{
    private readonly ManualLogSource _logger;
    private readonly Dictionary<ZNetPeer, (long Id, string Name, string? RawName)> _sessions = new();
    private readonly HashSet<ZNetPeer> _present = new();
    private readonly List<ZNetPeer> _departed = new();
    public PlayerConnectionNotices(ManualLogSource logger) => _logger = logger;

    public void Observe(IReadOnlyList<ZNetPeer> connected, IEnumerable<ZNetPeer> joined)
    {
        _present.Clear();
        foreach (var peer in connected) _present.Add(peer);
        _departed.Clear();
        foreach (var prior in _sessions)
        {
            if (_present.Contains(prior.Key)) continue;
            _departed.Add(prior.Key);
        }
        foreach (var peer in _departed)
        {
            var prior = _sessions[peer]; _sessions.Remove(peer);
            Write(prior.Id, prior.Name, "disconnected");
        }
        foreach (var peer in joined)
        {
            bool known = _sessions.TryGetValue(peer, out var prior);
            if (known && prior.RawName == peer.m_playerName) continue;
            string name = CleanName(peer.m_playerName);
            if (known && name.Length == 0) name = prior.Name;
            long id = known ? prior.Id : peer.m_uid;
            _sessions[peer] = (id, name, peer.m_playerName);
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
    public void Clear() { _sessions.Clear(); _present.Clear(); _departed.Clear(); }
}
