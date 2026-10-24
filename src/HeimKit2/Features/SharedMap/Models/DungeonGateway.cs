using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using UnityEngine;

namespace HeimKit.Features.SharedMap.Models;

internal sealed class DungeonGateway
{
    public string Path { get; }
    public Vector3 Offset { get; }
    public Vector3 InteriorOffset { get; }
    public DungeonGateway(string path, Vector3 offset, Vector3 interiorOffset = default) { Path = path; Offset = offset; InteriorOffset = interiorOffset; }
}
