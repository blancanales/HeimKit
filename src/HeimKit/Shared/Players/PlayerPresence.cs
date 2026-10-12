using System;
using System.Collections.Generic;
using System.Linq;
namespace HeimKit.Shared.Players;
public sealed class PlayerPresence : IPlayerPresence
{
    private readonly Func<IReadOnlyList<ZNetPeer>?> _read;
    private readonly HashSet<ZNetPeer> _joined = new();
    private ZDOMan? _world;
    public IReadOnlyList<ZNetPeer> Peers { get; private set; } = Array.Empty<ZNetPeer>();
    public int? ConnectedCount { get; private set; }
    public int? ReadyCount { get; private set; }
    public int? JoinedCount { get; private set; }
    public bool? HasConnectedPlayers => ConnectedCount.HasValue ? ConnectedCount.Value > 0 : (bool?)null;
    public bool? HasJoinedPlayers => JoinedCount.HasValue ? JoinedCount.Value > 0 : (bool?)null;
    public PlayerPresence(Func<IReadOnlyList<ZNetPeer>?>? read = null) => _read = read ?? (() => ZNet.instance == null ? null : ZNet.instance.GetPeers().ToArray());
    public void Refresh()
    {
        try
        {
            var peers = _read();
            if (peers == null) { Unavailable(); return; }
            if (!ReferenceEquals(_world, ZDOMan.instance)) { _joined.Clear(); _world = ZDOMan.instance; }
            _joined.RemoveWhere(peer => !peers.Contains(peer) || !peer.IsReady());
            // Native Game.SpawnPlayer registers CharacterID after creating the player's character.
            // Keep that session joined across death/respawn (which temporarily clears CharacterID).
            foreach (var peer in peers)
                if (peer.IsReady() && !peer.m_characterID.IsNone()) _joined.Add(peer);
            Peers = peers; ConnectedCount = peers.Count; ReadyCount = peers.Count(p => p.IsReady());
            JoinedCount = _joined.Count;
        }
        catch { Unavailable(); }
    }
    public void Clear() { Unavailable(); _joined.Clear(); _world = null; }
    private void Unavailable()
    {
        Peers = Array.Empty<ZNetPeer>(); ConnectedCount = null; ReadyCount = null; JoinedCount = null;
        // Unavailable observations pause consumers but do not turn a respawning session into a new login.
    }
}
