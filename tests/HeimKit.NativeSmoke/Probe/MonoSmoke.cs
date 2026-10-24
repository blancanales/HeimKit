using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HeimKit.Core.Native;
using HeimKit.Core.World;
using HeimKit.Features.TameTravel;
using HeimKit.Features.WeatherGuard;
using UnityEngine;
public static class MonoSmoke
{
    public static string Run()
    {
        if (Type.GetType("Mono.Runtime") == null) throw new Exception("This check must run in Mono.");
        bool rejected = false;
        try { _ = new DynamicMethod("old_fieldref", typeof(bool).MakeByRefType(), new[] { typeof(object) }, typeof(MonoSmoke).Module, true); }
        catch (ArgumentException e) { rejected = e.Message.Contains("byref"); }
        if (!rejected) throw new Exception("The original ref-return failure was not reproduced.");
        System.Console.WriteLine("PASS: installed game Mono reproduces the original raw DynamicMethod ref-return rejection.");
        NativeAccessTests.Run();
        _ = NativeAccess.Field<WearNTear, bool>("m_rainWet");
        _ = NativeAccess.Field<WearNTear, bool>("m_haveRoof");
        _ = NativeAccess.Field<WearNTear, int>("m_shieldChangeID");
        _ = NativeAccess.Field<WearNTear, float>("m_rainTimer");
        _ = NativeAccess.Field<WearNTear, float>("m_createTime");
        _ = NativeAccess.Field<WearNTear, float>("m_updateCoverTimer");
        _ = NativeAccess.Field<Piece, long>("m_creator");
        _ = NativeAccess.Field<Piece, int>("m_creatorPlatformUserIDIndex");
        WorldObjectProvider.CheckLiveInstanceCompatibility();
        TerrainContext.CheckCompatibility();
        RuntimeHelpers.RunClassConstructor(typeof(TameTravelPlacement).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(WeatherGuardPatches).TypeHandle);
        return "PASS: installed game Mono resolves WeatherGuard, TameTravel placement, live-scene and terrain accessors without starting gameplay or server hooks.";
    }
}
