using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using UnityEngine;
namespace HeimKit.Features.SeaSalvage.Models;
internal sealed class SalvageSource
{
    public ZDO Record = null!;
    public ZDOID Identity;
    public Vector3 Position;
    public uint Revision;
    public long Owner;
    public byte[] Original = null!, Empty = null!;
}
