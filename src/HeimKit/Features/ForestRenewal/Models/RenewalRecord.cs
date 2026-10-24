using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using UnityEngine;
namespace HeimKit.Features.ForestRenewal.Models;
internal sealed class RenewalRecord
{
    public ZDOID Source;
    public Vector3 Position;
    public int Sapling;
    public long DueTicks, DecisionTicks;
    // 0 pending confirmed removal, 1 excluded/chance rejected, 2 consumed, 3 pending standing stump.
    public byte State;
}
