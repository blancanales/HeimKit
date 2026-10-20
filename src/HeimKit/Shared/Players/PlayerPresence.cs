using System;
using System.Collections.Generic;
using System.Linq;
namespace HeimKit.Shared.Players;
public sealed class PlayerPresence : IPlayerPresence
{
    private readonly Func<IReadOnlyList<ZNetPeer>?> _read;
    private readonly HashSet<ZNetPeer> _joined = new();
    private readonly List<ZNetPeer> _departed = new();
    private ZDOMan? _world;
    private readonly PlayerConnectionNotices? _connections;
    public IReadOnlyList<ZNetPeer> Peers { get; private set; } = Array.Empty<ZNetPeer>();
    public int? ConnectedCount { get; private set; }
    public int? ReadyCount { get; private set; }
    public int? JoinedCount { get; private set; }
    public bool? HasConnectedPlayers => ConnectedCount.HasValue ? ConnectedCount.Value > 0 : (bool?)null;
    public bool? HasJoinedPlayers => JoinedCount.HasValue ? JoinedCount.Value > 0 : (bool?)null;
    public PlayerPresence(Func<IReadOnlyList<ZNetPeer>?>? read = null, PlayerConnectionNotices? connections = null)
    {
        _read = read ?? ReadPeers;
        _connections = connections;
    }
    private IReadOnlyList<ZNetPeer>? ReadPeers()
    {
        if (ZNet.instance == null) return null;
        var peers = ZNet.instance.GetPeers();
        // Keep a stable snapshot while reusing it across frames with unchanged membership.
        if (peers.Count == Peers.Count)
        {
            bool same = true;
            for (int i = 0; i < peers.Count; i++)
                if (!ReferenceEquals(peers[i], Peers[i])) { same = false; break; }
            if (same) return Peers;
        }
        return peers.ToArray();
    }
    public void Refresh()
    {
        try
        {
            var peers = _read();
            if (peers == null) { Unavailable(); return; }
            if (!ReferenceEquals(_world, ZDOMan.instance)) { _joined.Clear(); _connections?.Clear(); _world = ZDOMan.instance; }
            _departed.Clear();
            foreach (var peer in _joined)
                if (!peers.Contains(peer) || !peer.IsReady()) _departed.Add(peer);
            foreach (var peer in _departed) _joined.Remove(peer);
            // Native Game.SpawnPlayer registers CharacterID after creating the player's character.
            // Keep that session joined across death/respawn (which temporarily clears CharacterID).
            int ready = 0;
            foreach (var peer in peers)
                if (peer.IsReady())
                { ready++; if (!peer.m_characterID.IsNone()) _joined.Add(peer); }
            Peers = peers; ConnectedCount = peers.Count; ReadyCount = ready;
            JoinedCount = _joined.Count;
            _connections?.Observe(peers, _joined);
        }
        catch { Unavailable(); }
    }
    public void Clear() { Unavailable(); _joined.Clear(); _departed.Clear(); _connections?.Clear(); _world = null; }
    private void Unavailable()
    {
        Peers = Array.Empty<ZNetPeer>(); ConnectedCount = null; ReadyCount = null; JoinedCount = null;
        // Unavailable observations pause consumers but do not turn a respawning session into a new login.
    }
}
