using System.Collections.Generic;
namespace HeimKit.Shared.Players;
// Null population means unavailable. Connected counts transport peers; joined counts spawned sessions, retained across respawn.
public interface IPlayerPresence
{
    bool? HasConnectedPlayers { get; }
    int? ConnectedCount { get; }
    int? ReadyCount { get; }
    int? JoinedCount { get; }
    bool? HasJoinedPlayers { get; }
    IReadOnlyList<ZNetPeer> Peers { get; }
    void Refresh();
    void Clear();
}
