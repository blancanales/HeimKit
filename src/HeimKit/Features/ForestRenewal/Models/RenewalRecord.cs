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
