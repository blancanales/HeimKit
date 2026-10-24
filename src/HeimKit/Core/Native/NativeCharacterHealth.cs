using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Core.Native;

// Native Character omits saved health at full health. Do not manufacture or save
// a replacement value: native GetHealth resolves its actual maximum when needed.
internal static class NativeCharacterHealth
{
    public static bool IsAlive(ZDO record) => !record.GetBool(ZDOVars.s_dead) &&
        (!record.GetFloat(ZDOVars.s_health, out float health) || PlayerWorldScope.Finite(health) && health > 0);
}
