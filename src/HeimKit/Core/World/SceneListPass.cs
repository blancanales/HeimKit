using System.Collections.Generic;
namespace HeimKit.Core.World;

// Creation and removal share the original lists; only extension is deduplicated.
internal struct SceneListPass
{
    private int _frame;
    private List<ZDO>? _near;
    private object? _world;
    internal bool Enter(int frame, List<ZDO> near, object? world)
    {
        if (_frame == frame && ReferenceEquals(_near, near) && ReferenceEquals(_world, world)) return false;
        _frame = frame; _near = near; _world = world; return true;
    }
    internal void Clear() { _frame = -1; _near = null; _world = null; }
}
