using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using UnityEngine;
namespace HeimKit.Features.TameTravel.Models;
internal sealed class TransferSnapshot
{
    public string Reason = "";
    public bool SameRecord;
    public long? Owner;
    public uint? DataRevision;
    public ushort? OwnerRevision;
    public Vector3? Position;
    public string? Follow;
    public float? Health;
    public bool? HealthSaved;
    public bool? Tamed;
    public long? Rider;
    public float PhaseAge;
    public bool BarrierEchoed;
}
