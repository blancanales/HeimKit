using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
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
