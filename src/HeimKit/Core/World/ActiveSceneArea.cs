using System;
using UnityEngine;

namespace HeimKit.Core.World;

// Snapshot of ZNetScene.PointInsideActiveArea's supported native geometry.
// Capture native settings/zone centres once per pass, not once per live object.
internal readonly struct ActiveSceneArea
{
    internal readonly Vector3 Center;
    private readonly float _extent, _radiusSquared;
    private readonly bool _rounded;
    internal ActiveSceneArea(Vector3 center, float zoneSize, int nearDistance, bool classic)
    {
        Center = center;
        _extent = (nearDistance == 1 ? 1f : 1.5f) * zoneSize;
        _rounded = nearDistance == 2 && !classic;
        _radiusSquared = zoneSize * 1.75f * (zoneSize * 1.75f);
    }
    internal bool Contains(Vector3 point)
    {
        float x = point.x - Center.x, z = point.z - Center.z;
        return Math.Max(Math.Abs(x), Math.Abs(z)) <= _extent && (!_rounded || x * x + z * z < _radiusSquared);
    }
}
