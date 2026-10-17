using HeimKit.Features.AFKRaidWatch;
using HeimKit.Features.AFKRaidWatch.Models;
using HeimKit.Shared.Players;
using HeimKit.Shared.Players.Models;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.AFKNightWatch.Models;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;

internal static class SleepActivityTests
{
    private static void Check(bool met, string reason) { if (!met) throw new Exception(reason); }
    public static void Run()
    {
        for (int n = 1; n <= 100; n++)
        {
            Check(AFKNightWatchSleepQuorumRules.Required(n, 50, true) == n / 2 + 1, "Strict majority must exceed half");
            Check(AFKNightWatchSleepQuorumRules.Required(n, 100, true) == n, "100 percent must mean unanimity");
        }
        var sleeper = new PlayerActivityView { Peer = 1, Known = true, Alive = true, InBed = true };
        var idle = new PlayerActivityView { Peer = 2, Known = true, Alive = true, Idle = true, X = 10 };
        Check(!AFKNightWatchSleepQuorumRules.Evaluate(new[] { sleeper, idle }, 50, true, false).Met, "One of two is insufficient");
        Check(AFKNightWatchSleepQuorumRules.Evaluate(new[] { sleeper, idle }, 100, false, true).Met, "Exclusion must work without majority");
        Check(!AFKNightWatchSleepQuorumRules.Evaluate(new[] { idle }, 50, true, true).Met, "All idle cannot skip time");
        Check(!AFKNightWatchSleepQuorumRules.Evaluate(Array.Empty<PlayerActivityView>(), 50, true, true).Met, "Empty roster cannot skip time");
        sleeper.Idle = true;
        Check(AFKNightWatchSleepQuorumRules.Evaluate(new[] { sleeper }, 100, false, true).Met, "Bed sleeper cannot be excluded");
        sleeper.Alive = false;
        Check(!AFKNightWatchSleepQuorumRules.Evaluate(new[] { sleeper }, 50, true, true).Met, "Dead actor cannot be sleeper");
        sleeper.Alive = true; sleeper.Idle = false;
        var unknown = new PlayerActivityView { Peer = 3 };
        Check(AFKNightWatchSleepQuorumRules.Evaluate(new[] { sleeper, unknown }, 50, true, true).Unknown, "Unknown actor requires native fallback");

        const BindingFlags native = BindingFlags.Instance | BindingFlags.NonPublic;
        Check(typeof(Game).GetMethod("EverybodyIsTryingToSleep", native)!.ReturnType == typeof(bool), "Native sleep vote shape");
        Check(typeof(Game).GetMethod("UpdateSleeping", native) != null, "Native sleep scheduler remains available");
        using var log = new ManualLogSource("Sleep activity tests");
        string folder = Path.Combine(AppContext.BaseDirectory, "sleep-activity-" + Guid.NewGuid());
        // Native patches are deliberately installed only in a running dedicated Unity world.
        AFKNightWatch Load() => ConfigurationLoader.Load(folder, log, config => new AFKNightWatch(config, log, PlayerServicesTests.For(config, log)));
        var module = Load(); module.Initialize("1.0.20"); Check(module.IsActive && module.Name == "AFKNightWatch", "Sleep defaults enabled");
        module.Shutdown(); module.Shutdown(); module.Initialize("unsupported"); Check(!module.IsActive, "Version guard");
        string configPath = Path.Combine(folder, "heimkit.cfg"); var text = File.ReadAllText(configPath);
        Check(text.Contains("RequiredSleepingPercent = 50") && text.Contains("IdleTimeoutSeconds = 300") && !text.Contains("DisableDebugLogging") && !text.Contains("[MajoritySleep]"), "Fresh defaults without hidden/retired keys");
        foreach (bool enabled in new[] { false, true }) foreach (bool majority in new[] { false, true }) foreach (bool exclusion in new[] { false, true })
        {
            File.WriteAllText(configPath, $"[AFKNightWatch]\nEnabled = {enabled}\nMajoritySleepEnabled = {majority}\nExcludeAFKFromSleep = {exclusion}\n");
            module = Load(); module.Initialize("1.0.20"); Check(module.IsActive == (enabled && (majority || exclusion)), "Independent sleep behavior matrix"); module.Shutdown(); module.Shutdown();
        }
        File.WriteAllText(configPath, "[AFKNightWatch]\nRequiredSleepingPercent = 70\nIdleTimeoutSeconds = 900\nProtectAFKFromRaids = false\n[PlayerActivity]\nIdleTimeoutSeconds = 600\n");
        var file = ConfigurationLoader.Load(folder, log, config => { _ = new AFKNightWatch(config, log, PlayerServicesTests.For(config, log)); _ = new AFKRaidWatch(config, log, PlayerServicesTests.For(config, log)); return config; });
        Check(new PlayerActivitySettings(file).IdleTimeoutSeconds.Value == 600 && !file.ContainsKey(new ConfigDefinition("AFKNightWatch", "ProtectAFKFromRaids")), "Fresh layout binds no legacy raid key");
        Check(File.ReadAllText(configPath).Contains("ProtectAFKFromRaids = false"), "Ordinary loader preserves unbound keys without cleanup");
        System.Console.WriteLine("PASS: preserved sleep quorum and uncertainty, native sleep signatures, fresh defaults, sleep behavior matrix and repeatable shutdown.");
    }
}
