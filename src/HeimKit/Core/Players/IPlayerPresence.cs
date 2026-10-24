using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Collections.Generic;
namespace HeimKit.Core.Players;
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
