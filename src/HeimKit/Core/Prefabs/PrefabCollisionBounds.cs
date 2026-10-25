using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using UnityEngine;
namespace HeimKit.Core.Prefabs;

// Conservative native prefab geometry; inactive variants are included.
internal static class PrefabCollisionBounds
{
    public static Bounds? Read(GameObject prefab)
    {
        var result = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
        {
            if (collider.isTrigger) continue;
            Bounds local;
            if (collider is BoxCollider box) local = new Bounds(box.center, box.size);
            else if (collider is SphereCollider sphere) local = new Bounds(sphere.center, Vector3.one * sphere.radius * 2);
            else if (collider is CapsuleCollider capsule)
            {
                var size = Vector3.one * capsule.radius * 2;
                size[capsule.direction] = Math.Max(capsule.height, capsule.radius * 2);
                local = new Bounds(capsule.center, size);
            }
            else if (collider is MeshCollider mesh && mesh.sharedMesh != null) local = mesh.sharedMesh.bounds;
            else return null;
            Corners(local, corner => result.Encapsulate(prefab.transform.InverseTransformPoint(collider.transform.TransformPoint(corner))));
        }
        return result;
    }
    public static void Corners(Bounds bounds, Action<Vector3> visit)
    {
        for (int i = 0; i < 8; i++)
            visit(Corner(bounds, i));
    }
    public static Vector3 Corner(Bounds bounds, int i) => new Vector3((i & 1) == 0 ? bounds.min.x : bounds.max.x,
        (i & 2) == 0 ? bounds.min.y : bounds.max.y, (i & 4) == 0 ? bounds.min.z : bounds.max.z);
}
