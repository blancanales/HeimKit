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
