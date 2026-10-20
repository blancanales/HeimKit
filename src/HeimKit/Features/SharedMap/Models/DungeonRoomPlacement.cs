using UnityEngine;

namespace HeimKit.Features.SharedMap.Models;

public sealed class DungeonRoomPlacement
{
    public int Hash { get; }
    public Vector3 Position { get; }
    public Vector3 RotationEuler { get; }
    public DungeonRoomPlacement(int hash, Vector3 position, Vector3 rotationEuler)
    { Hash = hash; Position = position; RotationEuler = rotationEuler; }
}
