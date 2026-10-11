using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.MajoritySleep;
using HeimKit.Features.AfkSleepExclusion;
using HeimKit.Features.AfkRaidProtection;
using HeimKit.Shared;

internal static class SleepActivityTests
{
    private static void Check(bool met, string reason) { if (!met) throw new Exception(reason); }
    private static PlayerActivityObservation Actor(float x = 0, float heading = 0, bool busy = false, bool bed = false, bool known = true, bool alive = true, string character = "actor", float health = 100, string equipment = "", int emote = 0)
        => new() { Peer = 1, Character = character, Known = known, Alive = alive, X = x, Heading = heading, Busy = busy, InBed = bed, Health = health, Equipment = equipment, Emote = emote };
    public static void Run()
    {
        for (int n = 1; n <= 100; n++)
        {
            Check(SleepQuorumRules.Required(n, 50, true) == n / 2 + 1, "Strict majority must exceed half");
            Check(SleepQuorumRules.Required(n, 100, true) == n, "100 percent must mean unanimity");
        }
        var sleeper = new PlayerActivityView { Peer = 1, Known = true, Alive = true, InBed = true };
        var idle = new PlayerActivityView { Peer = 2, Known = true, Alive = true, Idle = true, X = 10 };
        Check(!SleepQuorumRules.Evaluate(new[] { sleeper, idle }, 50, true, false).Met, "One of two is insufficient");
        Check(SleepQuorumRules.Evaluate(new[] { sleeper, idle }, 100, false, true).Met, "Exclusion must work without majority");
        Check(!SleepQuorumRules.Evaluate(new[] { idle }, 50, true, true).Met, "All idle cannot skip time");
        Check(!SleepQuorumRules.Evaluate(Array.Empty<PlayerActivityView>(), 50, true, true).Met, "Empty roster cannot skip time");
        sleeper.Idle = true;
        Check(SleepQuorumRules.Evaluate(new[] { sleeper }, 100, false, true).Met, "Bed sleeper cannot be excluded");
        sleeper.Alive = false;
        Check(!SleepQuorumRules.Evaluate(new[] { sleeper }, 50, true, true).Met, "Dead actor cannot be sleeper");
        sleeper.Alive = true; sleeper.Idle = false;
        var unknown = new PlayerActivityView { Peer = 3 };
        Check(SleepQuorumRules.Evaluate(new[] { sleeper, unknown }, 50, true, true).Unknown, "Unknown actor requires native fallback");

        Check(AfkRaidRules.Suppress(0, 0, 100, new[] { idle }), "Idle-only area should be excluded");
        Check(!AfkRaidRules.Suppress(0, 0, 100, Array.Empty<PlayerActivityView>()), "Empty area is not idle-only evidence");
        Check(!AfkRaidRules.Suppress(0, 0, 100, new[] { idle, sleeper }), "Nearby bed occupant preserves native raids");
        var active = new PlayerActivityView { Peer = 4, Known = true, Alive = true, X = 500 };
        Check(AfkRaidRules.Suppress(0, 0, 100, new[] { idle, active }), "Active player elsewhere must not rescue idle area");
        active.X = 100;
        Check(AfkRaidRules.Suppress(0, 0, 100, new[] { idle, active }), "Native radius is strictly less than range");
        active.X = 99;
        Check(!AfkRaidRules.Suppress(0, 0, 100, new[] { idle, active }), "Nearby active player keeps native raids");
        active.Y = 3001;
        Check(AfkRaidRules.Suppress(0, 0, 100, new[] { idle, active }), "Dungeon height must not affect surface raids");
        Check(!AfkRaidRules.Suppress(0, 0, 100, new[] { idle, unknown }), "Unknown position must not suppress raids");
        idle.Alive = false;
        Check(!AfkRaidRules.Suppress(0, 0, 100, new[] { idle }), "Dead nearby player is uncertain, not idle-only");
        idle.Alive = true;
        Check(!AfkRaidRules.Suppress(float.NaN, 0, 100, new[] { idle }), "Invalid geometry must retain native behavior");

        var tracker = new PlayerActivityTracker(new(10, 2, 0.5f, 10, 1));
        PlayerActivityView Step(float time, PlayerActivityObservation? actor = null) => tracker.Update(time, new[] { actor ?? Actor() })[0];
        Check(!Step(0).Idle, "Join grace");
        for (int t = 1; t < 10; t++) Check(!Step(t).Idle, "Timeout not reached");
        Check(Step(10).Idle, "Idle timeout boundary");
        tracker.Signal(1, 10.2f); Check(!Step(11).Idle, "Routed activity wakes idle actor");
        tracker.Clear(); Step(0);
        for (int t = 1; t <= 10; t++) Step(t, Actor(busy: true));
        Check(!Step(11).Idle, "Station/container activity remains active");
        tracker.Clear(); Step(0);
        for (int t = 1; t <= 10; t++) Step(t, Actor(bed: true));
        Check(!Step(11).Idle, "Leaving bed starts fresh activity allowance");
        tracker.Clear(); Step(0);
        Check(!Step(10).Idle, "Missing observation interval resets grace");
        Check(!Step(9).Idle, "Clock rollback resets grace");
        Step(10, Actor(known: false)); Check(!Step(11).Idle, "Loading recovery resets grace");
        Step(12, Actor(alive: false)); Check(!Step(13).Idle, "Death recovery resets grace");
        Step(14, Actor(x: float.NaN)); Check(!Step(15).Idle, "Invalid geometry recovery resets grace");
        Check(!Step(16, Actor(character: "respawn")).Idle, "New character resets grace");
        tracker.Update(17, Array.Empty<PlayerActivityObservation>()); Check(!Step(18).Idle, "Reconnect starts a new session");
        tracker.Clear(); Step(0);
        for (int t = 1; t <= 9; t++) Step(t, Actor(x: t * 0.06f));
        Check(!Step(10, Actor(x: 0.6f)).Idle, "Cumulative slow movement counts as activity");
        tracker.Clear(); Step(0, Actor(heading: 359));
        for (int t = 1; t <= 10; t++) Step(t, Actor(heading: 1));
        Check(Step(11, Actor(heading: 1)).Idle, "Heading wrap must not invent activity");
        Check(!Step(12, Actor(heading: 20)).Idle, "Observed look rotation wakes idle player");
        foreach (var action in new[] { Actor(health: 90), Actor(equipment: "axe"), Actor(emote: 1) })
        {
            tracker.Clear(); Step(0); for (int t = 1; t <= 10; t++) Step(t);
            Check(!Step(11, action).Idle, "Damage/equipment/emote activity wakes idle player");
        }
        tracker.Clear(); Step(0);
        for (int t = 1; t <= 10; t++) Step(t, Actor(health: 100 + t));
        Check(Step(11, Actor(health: 111)).Idle, "Passive regeneration must not prevent idle inference");
        bool invalid = false;
        try { _ = new PlayerActivityOptions(float.NaN, 0, 1, 1, 1); } catch (ArgumentOutOfRangeException) { invalid = true; }
        Check(invalid, "NaN config rejected");

        var packet = new ZPackage(); packet.Write(1L); packet.Write(12L); packet.Write(13L);
        var target = new ZDOID(12, 4); packet.Write(target); packet.Write("RPC_Repair".GetStableHashCode()); packet.Write("native body"); packet.SetPos(0);
        var bytes = packet.GetArray();
        Check(RoutedActivityReader.TryRead(packet, 12, out var parsed, out int method) && parsed == target && RoutedActivityReader.IsActivity(method), "Read authenticated native activity header");
        Check(packet.GetPos() == 0 && bytes.SequenceEqual(packet.GetArray()), "Observer preserves cursor and every byte");
        Check(!RoutedActivityReader.TryRead(packet, 99, out _, out _) && packet.GetPos() == 0, "Forged sender not activity");
        var malformed = new ZPackage(new byte[] { 1, 2 });
        Check(!RoutedActivityReader.TryRead(malformed, 12, out _, out _) && malformed.GetPos() == 0, "Malformed header is unchanged");
        Check(!RoutedActivityReader.IsActivity("RPC_Pong".GetStableHashCode()), "Housekeeping is not activity");

        const BindingFlags native = BindingFlags.Instance | BindingFlags.NonPublic;
        Check(typeof(Game).GetMethod("EverybodyIsTryingToSleep", native)!.ReturnType == typeof(bool), "Native sleep vote shape");
        Check(typeof(Game).GetMethod("UpdateSleeping", native) != null, "Native sleep scheduler remains available");
        Check(typeof(RandEventSystem).GetMethod("UpdateRandomEvent", native)!.GetParameters().Single().ParameterType == typeof(float), "Native automatic event scope shape");
        Check(typeof(RandEventSystem).GetMethod("GetValidEventPoints", native)!.ReturnType == typeof(List<UnityEngine.Vector3>), "Native candidate selection shape");
        var hooks = typeof(AfkRaidProtectionPatches);
        const BindingFlags hidden = BindingFlags.Static | BindingFlags.NonPublic;
        var enter = hooks.GetMethod("Enter", hidden)!; var exit = hooks.GetMethod("Exit", hidden)!; var depth = hooks.GetField("_automaticDepth", hidden)!;
        object?[] outer = { 0 }; enter.Invoke(null, outer); object?[] inner = { 0 }; enter.Invoke(null, inner);
        Check((int)depth.GetValue(null)! == 2, "Nested automatic scopes");
        var fault = new Exception("native failure");
        Check(ReferenceEquals(exit.Invoke(null, new[] { inner[0], fault }), fault) && (int)depth.GetValue(null)! == 1, "Finalizer preserves exception and outer scope");
        exit.Invoke(null, new object?[] { outer[0], null }); Check((int)depth.GetValue(null)! == 0, "Finalizer restores explicit/admin exclusion scope");

        using var log = new ManualLogSource("Sleep activity tests");
        string folder = Path.Combine(AppContext.BaseDirectory, "sleep-activity-" + Guid.NewGuid());
        // Native patches are deliberately installed only in a running dedicated Unity world.
        var modules = ConfigurationLoader.Load(folder, log, config => new IServerModule[] { new MajoritySleep(config, log), new AfkSleepExclusion(config, log), new AfkRaidProtection(config, log) });
        foreach (var module in modules)
        {
            module.Initialize("1.0.17"); Check(module.IsActive, "New modules default enabled");
            module.Shutdown(); module.Shutdown(); module.Initialize("unsupported"); Check(!module.IsActive, "Version guard");
        }
        string configPath = Path.Combine(folder, "heimkit.cfg"); var text = File.ReadAllText(configPath);
        Check(text.Contains("RequiredSleepingPercent = 50") && text.Contains("IdleTimeoutSeconds = 300") && text.Contains("JoinGraceSeconds = 60") && !text.Contains("DisableDebugLogging"), "Complete first use defaults without hidden keys");
        File.WriteAllText(configPath, "[MajoritySleep]\nEnabled = false\nRequiredSleepingPercent = 100\nDisableDebugLogging = true\n[AfkSleepExclusion]\nEnabled = true\n[AfkRaidProtection]\nEnabled = false\n[Diagnostics]\nDebugMode = true\n[PlayerActivity]\nIdleTimeoutSeconds = 600\n");
        modules = ConfigurationLoader.Load(folder, log, config => new IServerModule[] { new MajoritySleep(config, log), new AfkSleepExclusion(config, log), new AfkRaidProtection(config, log) });
        foreach (var module in modules) module.Initialize("1.0.17");
        Check(!modules[0].IsActive && modules[1].IsActive && !modules[2].IsActive, "Independent module enablement");
        text = File.ReadAllText(configPath);
        Check(text.Contains("RequiredSleepingPercent = 100") && text.Contains("IdleTimeoutSeconds = 600") && text.Contains("DisableDebugLogging = true"), "Preserve existing config and hidden override");
        var config = new ConfigFile(configPath, false) { SaveOnConfigSet = false };
        var majority = new MajoritySleepSettings(config, log); var exclusion = new AfkSleepExclusionSettings(config, log); var raid = new AfkRaidProtectionSettings(config, log);
        Check(!majority.DebugLogging.Enabled && exclusion.DebugLogging.Enabled && raid.DebugLogging.Enabled, "New module suppression independence");
        foreach (var module in modules) module.Shutdown();
        System.Console.WriteLine("PASS: strict/unanimous sleep, independent exclusion, local idle raid decisions, conservative activity/recovery, byte-preserving authenticated observer, native hook signatures/scoped finalizers, defaults/config preservation and isolated lifecycles. Unity gameplay remains unverified.");
    }
}
