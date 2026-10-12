using System;
using System.Collections.Generic;
using System.Linq;
namespace HeimKit.Shared.Players;
public sealed class PlayerPresence : IPlayerPresence
{
    private readonly Func<IReadOnlyList<ZNetPeer>?> _read;
    public IReadOnlyList<ZNetPeer> Peers { get; private set; } = Array.Empty<ZNetPeer>();
    public int? ConnectedCount { get; private set; }
    public int? ReadyCount { get; private set; }
    public bool? HasConnectedPlayers => ConnectedCount.HasValue ? ConnectedCount.Value > 0 : (bool?)null;
    public PlayerPresence(Func<IReadOnlyList<ZNetPeer>?>? read = null) => _read = read ?? (() => ZNet.instance == null ? null : ZNet.instance.GetPeers().ToArray());
    public void Refresh()
    {
        try
        {
            var peers = _read();
            if (peers == null) { Clear(); return; }
            Peers = peers; ConnectedCount = peers.Count; ReadyCount = peers.Count(p => p.IsReady());
        }
        catch { Clear(); }
    }
    public void Clear() { Peers = Array.Empty<ZNetPeer>(); ConnectedCount = null; ReadyCount = null; }
}
