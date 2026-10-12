using System.Collections.Generic;
using HeimKit.Shared.Players.Models;
namespace HeimKit.Shared.Players;
public interface IPlayerActivityObservations
{
    bool Reliable { get; }
    IReadOnlyList<PlayerActivityView> Snapshot(float now);
}
