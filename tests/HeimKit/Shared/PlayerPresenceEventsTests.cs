using BepInEx.Configuration;
using HeimKit.Features.InfiniteTorches;
using System.Reflection;
using BepInEx.Logging;
using HeimKit.Shared.Players;
internal static class PlayerPresenceEventsTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Stop : IDisposable
    {
        private Action? _stop;
        public Stop(Action stop) => _stop = stop;
        public void Dispose() { var stop = _stop; _stop = null; stop?.Invoke(); }
    }
    public static void Run()
    {
        using var log = new ManualLogSource("Presence lifecycle checks");
        using var events = new PlayerPresenceEvents(log);
        var first = new List<bool?>(); var second = new List<bool?>();
        var a = events.Subscribe(first.Add, error => throw new Exception("Healthy consumer failed", error));
        var b = events.Subscribe(second.Add, error => throw new Exception("Healthy consumer failed", error));
        Check(first.SequenceEqual(new bool?[] { null }) && second.SequenceEqual(first), "Current unknown state was not replayed");
        events.Observe(false); events.Observe(false); events.Observe(true); events.Observe(true); events.Observe(true);
        Check(first.SequenceEqual(new bool?[] { null, false, true }) && first.SequenceEqual(second), "Repeated polls or additional players notified consumers");
        events.Observe(null); events.Observe(true); events.Observe(false); events.Observe(true);
        Check(first.SequenceEqual(new bool?[] { null, false, true, null, true, false, true }), "Empty/unavailable/reconnect lifecycle transitions lost");
        a.Dispose(); a.Dispose(); int stopped = first.Count; events.Observe(false);
        Check(first.Count == stopped && second[^1] == false && events.Subscribers == 1, "Repeatable unsubscribe affected another consumer");
        bool replayed = false;
        using var late = events.Subscribe(state => replayed = state == false, error => { }); Check(replayed, "Late subscription did not receive current occupancy");
        int failures = 0;
        using var broken = events.Subscribe(state => { if (state == true) throw new InvalidOperationException("consumer failed"); }, error => failures++);
        events.Observe(true); Check(failures == 1 && second[^1] == true && events.Subscribers == 2, "One subscriber failure blocked another or retained failed subscription");
        events.Observe(false); events.Observe(true); Check(failures == 1, "Failed consumer automatically restarted");
        using var badHandler = events.Subscribe(state => throw new InvalidOperationException("callback failure"), error => throw new Exception("failure-handler failure"));
        events.Observe(false); Check(second[^1] == false, "Failure-handler error blocked healthy subscriber");
        // Exercise player-dependent lifecycle with fake resources without claiming Unity hook execution.
        int starts = 0, stops = 0; IDisposable? activityLease = null;
        bool worldReady = false;
        using var playerWork = events.Subscribe(occupied => {
            if (occupied == true && worldReady && activityLease == null) { activityLease = new Stop(() => stops++); starts++; }
            if (occupied != true) { activityLease?.Dispose(); activityLease = null; }
        }, error => throw error);
        events.Observe(true); Check(starts == 0, "Connection bypassed world readiness");
        worldReady = true; events.Observe(false); events.Observe(true); events.Observe(true);
        Check(starts == 1 && stops == 0, "Extra connection recreated resources");
        events.Observe(false); Check(stops == 1 && activityLease == null, "Empty event retained player-dependent resources");
        events.Observe(true); Check(starts == 2, "First reconnect failed to restart resources");
        events.Observe(null); Check(stops == 2, "Unknown presence continued dependent processing");
        b.Dispose(); events.Dispose(); events.Dispose(); Check(events.Subscribers == 0, "Hub cleanup retained subscriptions");
        var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, "torch-presence-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        using var hub = new PlayerServices(config, log);
        var torch = new InfiniteTorches(config, log, hub);
        var controller = typeof(InfiniteTorches).GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)!;
        torch.Initialize("1.0.20");
        Check(hub.Events.Subscribers == 1 && controller.GetValue(torch) == null, "Torch initialized player work on an unavailable server");
        hub.Events.Observe(false); hub.Events.Observe(true); var running = controller.GetValue(torch);
        Check(running != null && hub.Activity.Consumers == 0, "Torch presence failed to create a controller or acquired unnecessary AFK tracking");
        hub.Events.Observe(true); Check(ReferenceEquals(running, controller.GetValue(torch)), "Additional player restarted torch controller");
        hub.Events.Observe(false); Check(controller.GetValue(torch) == null && torch.IsActive, "Empty server retained torch processing or disabled configuration");
        hub.Events.Observe(true); Check(controller.GetValue(torch) != null && !ReferenceEquals(running, controller.GetValue(torch)), "Reconnect reused disposed torch controller");
        hub.Events.Observe(null); Check(controller.GetValue(torch) == null, "Unknown presence retained torch processing");
        torch.Shutdown(); torch.Shutdown(); Check(hub.Events.Subscribers == 0, "Torch shutdown retained its subscription");
        System.Console.WriteLine("PASS: replayed and shared occupancy notifications, quiet extra players, empty/unknown/reconnect transitions, independent subscription cleanup isolated callback/failure-handler errors and real torch controller pause/recreation without AFK tracking.");
    }
}
