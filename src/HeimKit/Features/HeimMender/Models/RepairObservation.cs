using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using UnityEngine;
namespace HeimKit.Features.HeimMender.Models;
internal sealed class RepairObservation
{
    public ZDO Record = null!;
    public ZDOID Identity;
    public Vector3 Position;
    public float Health;
    public float Maximum;
    public float Seen;
    public float Damaged;
    // Fixed random share (0..1) of the repair spread this piece waits after its last observed damage.
    public float Share;
}
