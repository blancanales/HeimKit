using UnityEngine;

namespace HeimKit.Features.SharedMap.Models;

internal sealed class DungeonGateway
{
    public string Path { get; }
    public Vector3 Offset { get; }
    public Vector3 InteriorOffset { get; }
    public DungeonGateway(string path, Vector3 offset, Vector3 interiorOffset = default) { Path = path; Offset = offset; InteriorOffset = interiorOffset; }
}
