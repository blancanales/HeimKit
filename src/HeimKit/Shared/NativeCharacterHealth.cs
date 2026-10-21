namespace HeimKit.Shared;

// Native Character omits saved health at full health. Do not manufacture or save
// a replacement value: native GetHealth resolves its actual maximum when needed.
internal static class NativeCharacterHealth
{
    public static bool IsAlive(ZDO record) => !record.GetBool(ZDOVars.s_dead) &&
        (!record.GetFloat(ZDOVars.s_health, out float health) || PlayerWorldScope.Finite(health) && health > 0);
}
