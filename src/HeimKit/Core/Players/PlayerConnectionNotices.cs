using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace HeimKit.Core.Players;

// Character sessions share the presence census, independently of feature activation and AFK inference.
public sealed class PlayerConnectionNotices
{
    private readonly ManualLogSource _logger;
    private readonly Dictionary<ZNetPeer, (long Id, string Name, string? RawName)> _sessions = new();
    private readonly Dictionary<ZNetPeer, string> _confirmedClients = new();
    internal bool ClientDetectionAvailable { get; set; }
    private readonly HashSet<ZNetPeer> _present = new();
    private readonly List<ZNetPeer> _departed = new();
    internal PlayerJoinHistory? History { get; set; }
    public PlayerConnectionNotices(ManualLogSource logger) => _logger = logger;

    public void Observe(IReadOnlyList<ZNetPeer> connected, IEnumerable<ZNetPeer> joined)
    {
        _present.Clear();
        foreach (var peer in connected) _present.Add(peer);
        _departed.Clear();
        foreach (var peer in _confirmedClients.Keys)
            if (!_present.Contains(peer) && !_sessions.ContainsKey(peer)) _departed.Add(peer);
        foreach (var peer in _departed) _confirmedClients.Remove(peer);
        _departed.Clear();
        foreach (var prior in _sessions)
        {
            if (_present.Contains(prior.Key)) continue;
            _departed.Add(prior.Key);
        }
        foreach (var peer in _departed)
        {
            var prior = _sessions[peer]; _sessions.Remove(peer);
            _confirmedClients.Remove(peer);
            History?.Depart(peer);
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
            History?.Observe(peer, peer.m_socket?.GetHostName(), name);
            if (!known)
            {
                string client = _confirmedClients.TryGetValue(peer, out var confirmation) ? confirmation :
                    ClientDetectionAvailable ? "vanilla client" : "client detection unavailable";
                Write(id, name, "connected (" + client + ")");
            }

        }
    }

    internal void ConfirmHeimKit(ZNetPeer peer, string version, bool compatible)
    {
        if (_confirmedClients.ContainsKey(peer)) return;
        string protocol = compatible ? "" : ", protocol mismatch";
        string client = $"HeimKit client {CleanName(version)}{protocol}";
        _confirmedClients.Add(peer, client);
        if (_sessions.TryGetValue(peer, out var session))
            Write(session.Id, session.Name, "confirmed " + client);
    }

    internal static string CleanName(string? name) => new string((name ?? "").Take(128)
        .Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
    private void Write(long id, string name, string action)
    {
        string identity = name.Length == 0 ? "Peer " + id : "Player \"" + name.Replace("\"", "\\\"") + "\" (peer " + id + ")";
        DiagnosticLog.Write(_logger, "PlayerPresence", identity + " " + action + ".");
    }
    public void Clear() { History?.ClearOnline(); _sessions.Clear(); _present.Clear(); _departed.Clear(); _confirmedClients.Clear(); }
}
