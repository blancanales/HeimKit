using UnityEngine;
namespace HeimKit.Features.SeaSalvage.Models;
internal sealed class SettlingObservation
{
    public long Owner;
    public uint Revision;
    public Vector3 Position;
    public float Since, Seen;
}
