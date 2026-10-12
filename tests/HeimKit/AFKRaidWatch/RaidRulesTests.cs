using HeimKit.Features.AFKNightWatch;
using HeimKit.Shared.Players;
using HeimKit.Shared.Players.Models;
using HeimKit.Features.AFKRaidWatch;
using System.Reflection;
internal static class RaidRulesTests
{
    private static void Check(bool met, string reason) { if (!met) throw new Exception(reason); }
    public static void Run()
    {
        var sleeper = new PlayerActivityView { Known = true, Alive = true, InBed = true };
        var idle = new PlayerActivityView { Known = true, Alive = true, Idle = true, X = 10 };
        var unknown = new PlayerActivityView();
        Check(AFKRaidWatchRules.Suppress(0, 0, 100, new[] { idle }), "Idle-only area should be excluded");
        Check(!AFKRaidWatchRules.Suppress(0, 0, 100, Array.Empty<PlayerActivityView>()), "Empty area is not idle-only evidence");
        Check(!AFKRaidWatchRules.Suppress(0, 0, 100, new[] { idle, sleeper }), "Nearby bed occupant preserves native raids");
        var active = new PlayerActivityView { Peer = 4, Known = true, Alive = true, X = 500 };
        Check(AFKRaidWatchRules.Suppress(0, 0, 100, new[] { idle, active }), "Active player elsewhere must not rescue idle area");
        active.X = 100;
        Check(AFKRaidWatchRules.Suppress(0, 0, 100, new[] { idle, active }), "Native radius is strictly less than range");
        active.X = 99;
        Check(!AFKRaidWatchRules.Suppress(0, 0, 100, new[] { idle, active }), "Nearby active player keeps native raids");
        active.Y = 3001;
        Check(AFKRaidWatchRules.Suppress(0, 0, 100, new[] { idle, active }), "Dungeon height must not affect surface raids");
        Check(!AFKRaidWatchRules.Suppress(0, 0, 100, new[] { idle, unknown }), "Unknown position must not suppress raids");
        idle.Alive = false;
        Check(!AFKRaidWatchRules.Suppress(0, 0, 100, new[] { idle }), "Dead nearby player is uncertain, not idle-only");
        idle.Alive = true;
        Check(!AFKRaidWatchRules.Suppress(float.NaN, 0, 100, new[] { idle }), "Invalid geometry must retain native behavior");

        const BindingFlags native = BindingFlags.Instance | BindingFlags.NonPublic;
        Check(typeof(RandEventSystem).GetMethod("UpdateRandomEvent", native)!.GetParameters().Single().ParameterType == typeof(float), "Native automatic event scope shape");
        Check(typeof(RandEventSystem).GetMethod("GetValidEventPoints", native)!.ReturnType == typeof(List<UnityEngine.Vector3>), "Native candidate selection shape");
        var hooks = typeof(AFKRaidWatchPatches);
        const BindingFlags hidden = BindingFlags.Static | BindingFlags.NonPublic;
        var enter = hooks.GetMethod("Enter", hidden)!; var exit = hooks.GetMethod("Exit", hidden)!; var depth = hooks.GetField("_automaticDepth", hidden)!;
        object?[] outer = { 0 }; enter.Invoke(null, outer); object?[] inner = { 0 }; enter.Invoke(null, inner);
        Check((int)depth.GetValue(null)! == 2, "Nested automatic scopes");
        var fault = new Exception("native failure");
        Check(ReferenceEquals(exit.Invoke(null, new[] { inner[0], fault }), fault) && (int)depth.GetValue(null)! == 1, "Finalizer preserves exception and outer scope");
        exit.Invoke(null, new object?[] { outer[0], null }); Check((int)depth.GetValue(null)! == 0, "Finalizer restores explicit/admin exclusion scope");

        System.Console.WriteLine("PASS: raid local-range and uncertainty rules, unrelated active locations, dungeon boundary and nested native automatic/exception scope.");
    }
}
