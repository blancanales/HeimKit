using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;

internal static class ModuleReapplyTests
{
    private const string Joined = "Players have joined the world; all enabled features are active.";
    private sealed class Module : IServerModule, IModuleSwitches
    {
        internal Module(ConfigFile config, string name, SwitchApplyMode mode, Func<bool>? applyInPlace = null)
        {
            Name = name;
            ApplyMode = mode;
            Switches = new[]
            {
                new SwitchBinding("Enabled", config.Bind(name, "Enabled", true)),
                new SwitchBinding("Option", config.Bind(name, "Option", false), applyInPlace: applyInPlace),
            };
        }

        public string Name { get; }
        public SwitchApplyMode ApplyMode { get; }
        public IReadOnlyList<SwitchBinding> Switches { get; }
        public bool IsActive { get; private set; }
        internal bool Throw, CannotStart;
        internal readonly List<string> Calls = new();

        public void Initialize(string gameVersion)
        {
            Calls.Add("initialize");
            if (Throw) throw new InvalidOperationException("Fake initialization failure.");
            IsActive = !CannotStart && Switches[0].Entry.Value;
        }

        public void Shutdown() { Calls.Add("shutdown"); IsActive = false; }
        public void Tick(float now) { }
    }

    public static void Run()
    {
        using var log = new ManualLogSource("Module reapply");
        int joined = 0;
        log.LogEvent += (_, e) => { if (LogCapture.Text(e.Data).Contains(Joined)) joined++; };
        using var presence = new PlayerPresenceEvents(log);
        presence.Observe(false);
        using var blocker = presence.Subscribe(_ => { }, _ => { }, active: () => false);
        presence.Observe(true);
        using (presence.AdministrativeChange())
        {
            using (presence.AdministrativeChange())
                presence.Subscribe(_ => { }, _ => { }, active: () => false).Dispose();
            presence.Subscribe(_ => { }, _ => { }, active: () => false).Dispose();
            blocker.Dispose();
        }
        presence.ReportProcessing();
        Check(joined == 1, "Administrative disposal poisoned aggregate notice");
        bool active = false;
        using var registration = presence.Subscribe(_ => { }, _ => { }, active: () => active);
        presence.ReportProcessing();
        active = true;
        presence.ReportProcessing();
        Check(joined == 1, "Aggregate notice repeated during occupied interval");

        presence.Observe(false);
        active = false;
        presence.Observe(true);
        registration.Dispose();
        presence.ReportProcessing();
        Check(joined == 1, "Ordinary disposal failed to prevent misleading success");

        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        using var host = new ModuleHost(log);
        host.AttachPresence(presence);
        using var protection = new ModuleFailureProtection(log);
        var restart = new Module(config, "Restart", SwitchApplyMode.Reinitialize);
        var self = new Module(config, "Self", SwitchApplyMode.SelfReconciling);
        var broken = new Module(config, "Broken", SwitchApplyMode.Reinitialize);
        var inPlace = new Module(config, "InPlace", SwitchApplyMode.Reinitialize,
            () => config.TryGetEntry<bool>("InPlace", "Option", out var configured) && configured.Value);
        var fallback = new Module(config, "Fallback", SwitchApplyMode.Reinitialize, () => false);
        var callbackFailure = new Module(config, "CallbackFailure", SwitchApplyMode.Reinitialize,
            () => throw new InvalidOperationException("Fake in-place application failure."));
        var held = new Module(config, "Held", SwitchApplyMode.Reinitialize);
        var unavailable = new Module(config, "Unavailable", SwitchApplyMode.Reinitialize);
        foreach (var module in new[] { restart, self, broken, held, unavailable, inPlace, fallback, callbackFailure }) host.Register(() => module, module.Name);
        held.Switches[0].Entry.Value = false;
        host.Initialize("test");
        restart.Calls.Clear();
        self.Calls.Clear();
        held.Calls.Clear();
        Check(host.Reapply("Restart", () => { }) && restart.Calls.SequenceEqual(new[] { "shutdown", "initialize" }), "Restart lifecycle");
        Check(host.Reapply("Self", () => { }) && self.Calls.Count == 0, "Self-reconciling lifecycle");
        inPlace.Calls.Clear();
        var option = inPlace.Switches[1];
        Check(host.Reapply(inPlace.Name, () => option.Entry.Value = true, option) && option.Entry.Value &&
            inPlace.Calls.Count == 0, "Successful in-place change restarted the module");
        fallback.Calls.Clear();
        Check(host.Reapply(fallback.Name, () => { }, fallback.Switches[1]) &&
            fallback.Calls.SequenceEqual(new[] { "shutdown", "initialize" }), "In-place fallback did not restart the module");
        Check(!host.Reapply(callbackFailure.Name, () => { }, callbackFailure.Switches[1]) &&
            !callbackFailure.IsActive && restart.IsActive && inPlace.IsActive, "In-place callback failure escaped isolation");
        inPlace.Shutdown();
        inPlace.Calls.Clear();
        int inactiveCalls = 0;
        var inactiveOption = new SwitchBinding("Option", option.Entry, applyInPlace: () => { inactiveCalls++; return true; });
        host.Reapply(inPlace.Name, () => option.Entry.Value = false, inactiveOption);
        inPlace.Switches[0].Entry.Value = false;
        host.Reapply(inPlace.Name, () => option.Entry.Value = true, inactiveOption);
        Check(inactiveCalls == 0 && inPlace.Calls.Count == 0, "In-place callback ran while inactive or disabled");
        broken.Throw = true;
        Check(!host.Reapply("Broken", () => { }) && restart.IsActive && self.IsActive, "Module failure escaped isolation");
        ModuleFailureProtection.Record("Held", new InvalidOperationException("Held"), "test");
        Check(!host.Reapply("Held", () => held.Switches[0].Entry.Value = true) && !held.Calls.Contains("initialize"), "Failure protection bypassed");

        presence.Observe(false);
        var waiting = presence.Subscribe(_ => { }, _ => { }, active: () => false);
        presence.Observe(true);
        unavailable.CannotStart = true;
        Check(!host.Reapply("Unavailable", () => { }), "Enabled inactive module reported success");
        using (presence.AdministrativeChange()) waiting.Dispose();
        presence.ReportProcessing();
        Check(joined == 1, "Enabled inactive module did not mark activation failure");
    }

    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
}
