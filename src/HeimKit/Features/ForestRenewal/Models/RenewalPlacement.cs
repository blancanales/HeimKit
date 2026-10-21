using UnityEngine;

namespace HeimKit.Features.ForestRenewal.Models;

internal sealed class RenewalPlacement
{
    public RenewalRecord Decision = null!;
    public ZDO? Stump;
    public GameObject Prefab = null!;
    public Vector3 Position;
    public long RequiredRevision;
    public float Created;
    public byte PendingState;
    public long PreviousDecisionTicks;
    public bool MutationStarted;
}
