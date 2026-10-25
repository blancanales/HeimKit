using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Core.Players;
using HeimKit.Features.InfiniteTorches;
using BepInEx.Configuration;
using System.Reflection;

internal static class PlayerProcessingNoticesTests
{
    private const string EmptyNotice = "PlayerPresence: No players have joined the world; all features are inactive until a player joins.";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void MarkServerReady(HeimKit.Core.Scheduling.IServerModule module)
    {
        var state = (PlayerProcessingNotices)module.GetType().GetField("_processing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        state.ServerReady();
    }
    public static void Run()
    {
        using var logger = new ManualLogSource("Processing state checks");
        var messages = new List<(LogLevel Level, string Text)>();
        logger.LogEvent += (_, args) => messages.Add((args.Level, LogCapture.Text(args.Data)));
        var status = new PlayerProcessingNotices(logger, "WeatherGuard");
        status.Unavailable("player presence unavailable"); status.EmptyPaused();
        Check(messages.Count == 0 && status.IsEmptyPaused, "Processing leaked individual normal status");
        status.ServerReady(); status.ServerReady();
        Check(messages.Count == 1 && messages[0].Text == "WeatherGuard: Ready." && !status.IsRunning,
            "Server readiness requires a player or falsely starts processing");
        status.Ready(); status.EmptyPaused(); status.Ready(); status.Paused("world services unavailable");
        Check(messages.Count == 1, "Join/departure/recovery repeated normal per-feature status");
        status.Reset(); status.ServerReady(); Check(messages.Count == 2, "Explicit reinitialization lost Ready");
        var debugStatus = new PlayerProcessingNotices(logger, "WeatherGuard", () => true);
        debugStatus.EmptyPaused(); debugStatus.EmptyPaused(); debugStatus.Ready(); debugStatus.Ready();
        Check(messages.Count == 4 && messages.Skip(2).All(m => m.Level == LogLevel.Debug),
            "Processing transitions missing from Debug or repeated");
        messages.Clear();
        new HeimKit.Features.WeatherGuard.WeatherGuardDiagnostics(logger, false).InitialAcquisition(61);
        Check(messages.Count == 0, "WeatherGuard acquisition leaked into normal logging");
        new HeimKit.Features.WeatherGuard.WeatherGuardDiagnostics(logger, true, HeimKit.Features.WeatherGuard.Models.DebugLogLevel.Medium).InitialAcquisition(61);
        Check(messages.Count == 1 && messages[^1].Level == LogLevel.Debug && messages[^1].Text.Contains("Acquired initial protection") && messages[^1].Text.Contains("protected=61"), "WeatherGuard acquisition evidence is missing or has wrong severity");
        // Real module ordering: log pause only after its controller/pending-work references are cleared.
        var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        using var hub = new PlayerServices(config, logger);
        var torch = new InfiniteTorches(config, logger, hub);
        var controller = typeof(InfiniteTorches).GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)!;
        torch.Initialize("1.0.20"); MarkServerReady(torch); messages.Clear();
        hub.Events.Observe(false);
        Check(messages.Count == 1 && messages[^1].Text == EmptyNotice && controller.GetValue(torch) == null, "Feature did not confirm actual paused state");
        hub.Events.Observe(false); Check(messages.Count == 1, "Unchanged empty polls repeated module notice");
        hub.Events.Observe(true); Check(!messages.Any(m => m.Text.EndsWith("Resumed.") || m.Text.EndsWith("Ready.")), "Joined player announced resumed before native readiness");
        typeof(InfiniteTorches).GetMethod("ProcessingReady", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(torch, null);
        hub.Events.Observe(false);
        Check(controller.GetValue(torch) == null && messages[^1].Text == EmptyNotice, "Pause retained controller or missed notice");
        hub.Events.Observe(true); typeof(InfiniteTorches).GetMethod("ProcessingReady", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(torch, null);
        hub.Events.ReportProcessing();
        Check(messages[^1].Text == "PlayerPresence: Players have joined the world; all enabled features are active." && !messages.Any(m => m.Text.Contains("InfiniteTorches: Resumed")), "Reconnect leaked individual normal status");
        torch.Shutdown(); torch.Shutdown();
        // All enabled consumers independently acknowledge cleanup before the shared notice.
        config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        using var allPlayers = new PlayerServices(config, logger);
        HeimKit.Core.Scheduling.IServerModule[] modules = {
            new InfiniteTorches(config, logger, allPlayers),
            new HeimKit.Features.WeatherGuard.WeatherGuard(config, logger, allPlayers),
            new HeimKit.Features.AFKNightWatch.AFKNightWatch(config, logger, allPlayers),
            new HeimKit.Features.AFKRaidWatch.AFKRaidWatch(config, logger, allPlayers)
        };
        foreach (var module in modules) { module.Initialize("1.0.20"); if (module.IsActive) MarkServerReady(module); }
        // Replay startup without marking any module ready: occupancy alone must stay quiet.
        foreach (var module in modules) module.Initialize("1.0.20");
        messages.Clear(); allPlayers.Events.Observe(false);
        Check(messages.Count == 0, "Empty presence preceded feature server readiness");
        foreach (var module in modules.Take(3)) MarkServerReady(module);
        allPlayers.Events.ReportProcessing();
        Check(messages.Count == 3 && messages.All(m => m.Text.EndsWith(": Ready.")), "Partial readiness emitted presence status");
        MarkServerReady(modules[3]); allPlayers.Events.ReportProcessing(); allPlayers.Events.Observe(false);
        Check(messages.Count == 5 && messages.Take(4).All(m => m.Text.EndsWith(": Ready.")) && messages[^1].Text == EmptyNotice,
            "Empty startup did not report four Ready entries followed by one shared inactive entry");
        foreach (var module in modules) module.Shutdown();
        Check(allPlayers.Events.Subscribers == 0, "Lifecycle notices retained subscriptions after shutdown");
        // Deferred acknowledgment is checked on polls without repeating callbacks or notices.
        messages.Clear();
        using (var events = new PlayerPresenceEvents(logger))
        {
            bool paused = false; int callbacks = 0;
            using var pending = events.Subscribe(state => callbacks++, error => throw error, () => paused);
            events.Observe(false); Check(messages.Count == 0, "Population substituted for cleanup acknowledgment");
            paused = true; events.Observe(false); events.Observe(false);
            Check(callbacks == 2 && messages.Count == 1 && messages[0].Text == EmptyNotice, "Delayed cleanup restarted consumers or repeated notice");
            events.Observe(true); events.Observe(true);
            Check(messages.Count == 2, "Additional join repeated occupancy");
        }
        // Joined presence waits for every enabled module's actual initialization.
        messages.Clear();
        using (var events = new PlayerPresenceEvents(logger))
        {
            bool firstRunning = false, secondRunning = false;
            using var first = events.Subscribe(_ => { }, error => throw error, () => true, () => firstRunning);
            using var second = events.Subscribe(_ => { }, error => throw error, () => true, () => secondRunning);
            events.Observe(false); messages.Clear();
            events.Observe(true); firstRunning = true; events.ReportProcessing();
            Check(messages.Count == 0, "Joined presence claimed activation before every module initialized");
            secondRunning = true; events.ReportProcessing(); events.ReportProcessing(); events.Observe(true);
            Check(messages.Count == 1 && messages[0].Text == "PlayerPresence: Players have joined the world; all enabled features are active.",
                "Completed activation missing or repeated on extra joins/polls");
            events.Observe(false); firstRunning = false; secondRunning = false; messages.Clear();
            events.Observe(true); first.Dispose(); secondRunning = true; events.ReportProcessing();
            Check(messages.Count == 0, "Removed inactive module produced false activation notice");
        }
        // A failing cleanup cannot vanish from the aggregate by being unsubscribed.
        messages.Clear();
        using (var events = new PlayerPresenceEvents(logger))
        {
            bool stopped = false; int failures = 0;
            using var broken = events.Subscribe(state => {
                if (state == false) throw new InvalidOperationException("cleanup failure");
            }, error => { failures++; HeimKit.Core.Diagnostics.DiagnosticLog.Write(logger, "Broken", HeimKit.Core.Diagnostics.CoreLogTopics.Lifecycle, error.Message, level: LogLevel.Error); }, () => false);
            using var healthy = events.Subscribe(state => stopped = state == false, error => throw error, () => stopped);
            events.Observe(false); events.Observe(false); events.Observe(null); events.Observe(false);
            Check(stopped && failures == 1 && messages.Count == 1 && messages[0].Level == LogLevel.Error, "Failure blocked healthy cleanup, disappeared, or claimed aggregate success");
            events.Observe(true);
            Check(messages[^1].Text == "PlayerPresence: Players have joined the world; all enabled features are active.", "Failed empty cleanup suppressed the next first-player notice");
            events.Observe(false);
            Check(messages[^1].Text == EmptyNotice, "A new occupied/empty interval did not recover notice coordination");
        }
        // Real AFK controller disposal: an observer-removal failure must propagate past
        // the cleanup runner after clearing leases, disable only that module and block success.
        foreach (bool raid in new[] { false, true })
        {
            config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            using var players = new PlayerServices(config, logger);
            HeimKit.Core.Scheduling.IServerModule module = raid
                ? new HeimKit.Features.AFKRaidWatch.AFKRaidWatch(config, logger, players)
                : new HeimKit.Features.AFKNightWatch.AFKNightWatch(config, logger, players);
            module.Initialize("1.0.20"); players.Events.Observe(true);
            using var activity = new PlayerActivityService(new(5, 2, .5f, 10, 1), logger, players.Presence,
                observer: install => { if (!install) throw new InvalidOperationException("observer removal failed"); });
            var type = raid ? typeof(HeimKit.Features.AFKRaidWatch.AFKRaidWatchController) : typeof(HeimKit.Features.AFKNightWatch.AFKNightWatchController);
            var resource = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            void Set(string name, object value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(resource, value);
            Set("_module", module); Set("_logger", logger); Set("_lease", activity.Acquire());
            if (raid) Set("_selectionNotices", new HeimKit.Features.AFKRaidWatch.AFKRaidWatchSelectionNotices(logger));
            else Set("_sleepNotices", new HeimKit.Features.AFKNightWatch.AFKNightWatchSleepNotices(logger, _ => { }));
            module.GetType().GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(module, resource);
            messages.Clear(); players.Events.Observe(false); players.Events.Observe(false);
            Check(!module.IsActive && activity.Consumers == 0 && players.Events.Subscribers == 0,
                "Failed controller cleanup retained active module or leases");
            Check(messages.Any(m => m.Level == LogLevel.Error && m.Text.Contains("observer removal failed")) && !messages.Any(m => m.Text == EmptyNotice),
                "Controller cleanup error was swallowed or reported aggregate success");
            module.Shutdown();
        }
        // Disabled features are never aggregate participants; each enabled subset works independently.
        for (int mask = 0; mask < 16; mask++)
        {
            config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            using var players = new PlayerServices(config, logger);
            modules = new HeimKit.Core.Scheduling.IServerModule[] {
                new InfiniteTorches(config, logger, players),
                new HeimKit.Features.WeatherGuard.WeatherGuard(config, logger, players),
                new HeimKit.Features.AFKNightWatch.AFKNightWatch(config, logger, players),
                new HeimKit.Features.AFKRaidWatch.AFKRaidWatch(config, logger, players)
            };
            string[] sections = { "Torches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch" };
            for (int i = 0; i < modules.Length; i++) config.Bind(sections[i], "Enabled", true).Value = (mask & (1 << i)) != 0;
            foreach (var module in modules) { module.Initialize("1.0.20"); if (module.IsActive) MarkServerReady(module); }
            messages.Clear(); players.Events.Observe(false); players.Events.Observe(false);
            Check(messages.Count == 1 && messages[0].Text == EmptyNotice, "Enabled subset did not combine empty notice: " + mask);
            foreach (var module in modules) module.Shutdown();
            Check(players.Events.Subscribers == 0, "Subset retained subscriptions after shutdown");
        }
        System.Console.WriteLine("PASS: combined acknowledged empty pauses, cleanup failure isolation, deferred acknowledgment, all 16 enabled subsets, player-independent startup Ready before presence, Debug-only processing transitions and quiet repeats.");
    }
}
