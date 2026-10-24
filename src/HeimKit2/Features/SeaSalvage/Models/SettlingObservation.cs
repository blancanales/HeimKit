using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using UnityEngine;
namespace HeimKit.Features.SeaSalvage.Models;
internal sealed class SettlingObservation
{
    public long Owner;
    public uint Revision;
    public Vector3 Position;
    public float Since, Seen;
}
