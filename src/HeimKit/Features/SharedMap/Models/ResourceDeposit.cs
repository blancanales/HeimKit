using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.SharedMap.Models;

// Identity belongs to the discovery store, not a native ZDO's session-specific identity.
public sealed class ResourceDeposit
{
    public int Identity { get; }
    public ResourceKind Kind { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public bool Depleted { get; internal set; }
    public bool SilverDiscovered { get; internal set; }
    public MiningProgress? Progress { get; internal set; }
    public ResourceDeposit(int identity, ResourceKind kind, float x, float y, float z, bool depleted = false,
        MiningProgress? progress = null, bool silverDiscovered = false)
    { Identity = identity; Kind = kind; X = x; Y = y; Z = z; Depleted = depleted; Progress = progress; SilverDiscovered = silverDiscovered; }
}
