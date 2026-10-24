using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.HeimMender.Models;
internal sealed class RepairRequest
{
    public ZDO Record = null!;
    public float Sent;
    public float Maximum;
}
