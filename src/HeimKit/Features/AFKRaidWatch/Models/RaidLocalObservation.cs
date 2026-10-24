using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
using HeimKit.Core.Players.Models;
using System.Collections.Generic;
namespace HeimKit.Features.AFKRaidWatch.Models;
public sealed class RaidLocalObservation
{
    public float Range { get; set; }
    public int Unknown, Dungeon, Outside, NearbyIdle, NearbyActive, NearbyBed, NearbyDead;
    public static RaidLocalObservation Read(float x, float z, float range, IEnumerable<PlayerActivityView> players)
    {
        var result = new RaidLocalObservation { Range = range };
        foreach (var p in players)
        {
            if (!p.Known || !PlayerActivityTracker.Finite(p.X) || !PlayerActivityTracker.Finite(p.Y) || !PlayerActivityTracker.Finite(p.Z)) { result.Unknown++; continue; }
            if (p.Y > 3000) { result.Dungeon++; continue; }
            double dx = p.X - x, dz = p.Z - z;
            if (dx * dx + dz * dz >= (double)range * range) { result.Outside++; continue; }
            if (!p.Alive) result.NearbyDead++;
            else if (p.InBed) result.NearbyBed++;
            else if (p.Idle) result.NearbyIdle++;
            else result.NearbyActive++;
        }
        return result;
    }
}
