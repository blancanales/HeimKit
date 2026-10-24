using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Collections.Generic;
using HeimKit.Core.Players.Models;
namespace HeimKit.Core.Players;
public interface IPlayerActivityObservations
{
    bool Reliable { get; }
    IReadOnlyList<PlayerActivityView> Snapshot(float now);
}
