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
}
