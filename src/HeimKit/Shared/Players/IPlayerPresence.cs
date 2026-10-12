using System.Collections.Generic;
namespace HeimKit.Shared.Players;
// Null population means unavailable; connected includes loading peers.
public interface IPlayerPresence
{
    bool? HasConnectedPlayers { get; }
    int? ConnectedCount { get; }
    int? ReadyCount { get; }
    IReadOnlyList<ZNetPeer> Peers { get; }
    void Refresh();
    void Clear();
}
