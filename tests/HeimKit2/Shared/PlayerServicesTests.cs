using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.AFKRaidWatch;
using HeimKit.Core.Players;
using HeimKit.Core.Players.Models;
using System.Runtime.CompilerServices;
internal static class PlayerServicesTests
{
    private static readonly ConditionalWeakTable<ConfigFile, PlayerServices> Hubs = new();
    public static PlayerServices For(ConfigFile config, ManualLogSource log) => Hubs.GetValue(config, c => new(c, log));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        DiagnosticLog.StartSession();
        using var log = new ManualLogSource("Shared activity checks");
        var records = new List<(LogLevel Level, string Text)>(); log.LogEvent += (_, e) => records.Add((e.Level, LogCapture.Text(e.Data)));
        var rangeConfig = new ConfigFile(Path.Combine(AppContext.BaseDirectory, "activity-range-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var ranges = new PlayerActivitySettings(rangeConfig);
        ranges.IdleTimeoutSeconds.Value = 99999; ranges.JoinGraceSeconds.Value = -1; ranges.MovementThresholdMeters.Value = 10; ranges.LookThresholdDegrees.Value = 100; ranges.SampleIntervalSeconds.Value = .01f;
        Check(ranges.Options().Timeout == 3600 && ranges.Options().Grace == 0 && ranges.Options().Movement == 5 && ranges.Options().Look == 90 && ranges.Options().Interval == .2f, "Shared validation ranges changed");
        int presenceReads = 0; bool unavailable = false;
        var loading = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer));
        var connected = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); connected.m_uid = 123;
        IReadOnlyList<ZNetPeer> currentPeers = new[] { loading, connected };
        var central = new PlayerPresence(() => { presenceReads++; return unavailable ? null : currentPeers; });
        central.Refresh();
        Check(central.ConnectedCount == 2 && central.ReadyCount == 1 && central.HasConnectedPlayers == true, "Loading connection lost or treated as character-ready");
        _ = central.Peers; _ = central.ReadyCount; _ = central.HasConnectedPlayers;
        Check(presenceReads == 1, "Consumers repeated central peer enumeration");
        Check(central.JoinedCount == 0 && central.HasJoinedPlayers == false, "Login handshake resumed features before character spawn");
        connected.m_characterID = new ZDOID(123, 1); central.Refresh();
        Check(central.JoinedCount == 1 && central.HasJoinedPlayers == true, "Character spawn did not establish joined occupancy");
        connected.m_characterID = ZDOID.None; central.Refresh();
        Check(central.JoinedCount == 1, "Death/respawn was mistaken for logout");
        currentPeers = new[] { loading }; central.Refresh();
        Check(central.JoinedCount == 0 && central.ConnectedCount == 1 && central.HasJoinedPlayers == false, "Remaining login attempt prevented last-player pause");
        var replacement = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); replacement.m_uid = 123;
        currentPeers = new[] { replacement }; central.Refresh();
        Check(central.JoinedCount == 0, "New connection inherited a prior player's joined session");
        replacement.m_characterID = new ZDOID(123, 2); central.Refresh();
        Check(central.JoinedCount == 1, "Reconnect failed after fresh character spawn");
        DiagnosticLog.ServerActivity.Observe(300, false); DiagnosticLog.ServerActivity.Observe(301, central.HasConnectedPlayers);
        Check(DiagnosticLog.ServerActivity.Allowed, "Connecting peer failed to resume shared Debug");
        unavailable = true; central.Refresh(); Check(central.ConnectedCount == null && central.ReadyCount == null && central.JoinedCount == null, "Unavailable presence conflated with empty");
        var brokenPresence = new PlayerPresence(() => throw new InvalidOperationException("native peers unavailable")); brokenPresence.Refresh();
        Check(brokenPresence.HasConnectedPlayers == null, "Presence failure retained stale population");
        DiagnosticLog.StartSession();
        foreach (bool sleepEnabled in new[] { false, true }) foreach (bool majority in new[] { false, true })
        foreach (bool exclusion in new[] { false, true }) foreach (bool raidEnabled in new[] { false, true })
        {
            var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, "services-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            var settings = new AFKNightWatchSettings(config, log); settings.Enabled.Value = sleepEnabled; settings.MajoritySleepEnabled.Value = majority; settings.ExcludeAFKFromSleep.Value = exclusion;
            var rs = new AFKRaidWatchSettings(config, log); rs.Enabled.Value = raidEnabled;
            var hub = new PlayerServices(config, log); var sleep = new AFKNightWatch(config, log, hub); var raid = new AFKRaidWatch(config, log, hub);
            sleep.Initialize(HeimKit.Configuration.GameVersionCompatibility.AuditedVersion); raid.Initialize(HeimKit.Configuration.GameVersionCompatibility.AuditedVersion);
            Check(sleep.IsActive == (sleepEnabled && (majority || exclusion)) && raid.IsActive == raidEnabled, "Feature independence");
            Check(hub.Events.Subscribers == (sleep.IsActive ? 1 : 0) + (raidEnabled ? 1 : 0), "Disabled/inactive modules subscribed or enabled modules missed presence");
            sleep.Shutdown(); sleep.Shutdown(); Check(hub.Events.Subscribers == (raidEnabled ? 1 : 0), "Sleep shutdown lost another presence subscription"); Check(raid.IsActive == raidEnabled, "Sleep shutdown stopped raid module"); raid.Shutdown(); raid.Shutdown(); hub.Dispose(); hub.Dispose();
            var presence = new Presence { Connected = 1, Ready = 1 };
            int installs = 0, removals = 0, reads = 0;
            using var service = new PlayerActivityService(new(5, 2, .5f, 10, 1), log, presence, () => true,
                () => { reads++; return new() { new PlayerActivityObservation { Peer = 1, Character = "same", Known = true, Alive = true, Health = 100 } }; },
                on => { if (on) installs++; else removals++; });
            var sl = settings.RequiresActivity ? service.Acquire() : null; var rl = rs.Enabled.Value ? service.Acquire() : null;
            int expected = (sleepEnabled && exclusion ? 1 : 0) + (raidEnabled ? 1 : 0);
            Check(service.Consumers == expected && installs == (expected > 0 ? 1 : 0), "Duplicate activity registrations or unnecessary majority-only lease");
            service.Tick(0); var first = service.Snapshot(0); service.Snapshot(.2f); service.Snapshot(.8f);
            Check(reads == (expected > 0 ? 1 : 0), "Consumers resampled the same interval");
            if (expected > 0)
            {
                for (int t = 1; t <= 5; t++) service.Tick(t);
                Check(service.Snapshot(5).Single().Idle, "Daytime tracking did not accumulate idle history");
                presence.Connected = 0; presence.Ready = 0; int before = reads;
                service.Tick(6); Check(service.Snapshot(6).Count == 0 && reads == before && !service.Reliable, "Empty server performed player sampling");
                presence.Connected = null; service.Tick(7); Check(reads == before, "Unknown presence sampled actors");
                presence.Connected = 1; presence.Ready = 0; service.Tick(8);
                Check(!service.Snapshot(8).Single().Idle, "Reconnect retained old idle state");
            }
            sl?.Dispose(); sl?.Dispose(); Check(service.Consumers == (raidEnabled ? 1 : 0), "Releasing one consumer lost another");
            rl?.Dispose(); rl?.Dispose(); Check(service.Consumers == 0 && removals == (expected > 0 ? 1 : 0), "Last-release cleanup was not idempotent");
        }
        var p = new Presence { Connected = 1, Ready = 1 }; bool fail = true, ready = true; int attempts = 0, unhooks = 0;
        using (var service = new PlayerActivityService(new(5, 2, .5f, 10, 1), log, p, () => ready,
            () => { attempts++; if (fail) throw new InvalidOperationException("read failed"); return new() { new PlayerActivityObservation { Peer = 1, Known = true, Alive = true, Character = "a", Health = 100 } }; }, on => { if (!on) unhooks++; }))
        {
            using var a = service.Acquire(); using var b = service.Acquire(); records.Clear();
            service.Tick(0); service.Snapshot(0); Check(attempts == 1 && !service.Reliable && service.Consumers == 2, "Sampling failure retried within interval or disabled consumers");
            service.Tick(1); Check(records.Count(r => r.Level == LogLevel.Error) == 1, "Repeated exception evidence flooded");
            fail = false; service.Tick(2); Check(service.Reliable && !service.Snapshot(2).Single().Idle, "Sampling recovery failed to restore grace");
            ready = false; service.Tick(3); Check(!service.Reliable && service.Snapshot(3).Count == 0, "Lost world readiness retained inference");
            ready = true; service.Tick(4); Check(service.Reliable && !service.Snapshot(4).Single().Idle, "World recovery reused stale idle state");
            service.Dispose(); service.Dispose(); Check(unhooks == 1, "Hub shutdown repeated observer cleanup");
        }
        records.Clear(); bool sleepDebug = false, raidDebug = true;
        using (var service = new PlayerActivityService(new(5, 2, .5f, 10, 1), log, p, () => true,
            () => new() { new PlayerActivityObservation { Peer = 1, Known = true, Alive = true, Character = "a", Health = 100 } }, on => { }))
        {
            using var a = service.Acquire(() => sleepDebug); var b = service.Acquire(() => raidDebug);
            service.Tick(0); Check(records.Count == 2 && records.All(r => r.Text.StartsWith("PlayerActivity.")), "Shared diagnostic stream duplicated or borrowed feature prefix");
            b.Dispose(); records.Clear(); service.Tick(60); Check(records.Count == 0, "Released raid debug enabled sleep diagnostics");
            sleepDebug = true; service.Tick(120); Check(records.Count == 2, "Remaining consumer could not enable shared diagnostic output");
            DiagnosticLog.ServerActivity.Observe(300, false); records.Clear(); service.Tick(121); Check(records.Count == 0, "Shared output bypassed empty-server timeout");
            DiagnosticLog.ServerActivity.Observe(301, true); service.Tick(180); Check(records.Count == 2, "Shared output failed to resume after connection");
        }
        DiagnosticLog.StartSession();
        int rollback = 0;
        using (var service = new PlayerActivityService(new(5, 2, .5f, 10, 1), log, p, () => true, () => new(), on => { if (on) throw new InvalidOperationException("install failed"); rollback++; }))
        {
            bool rejected = false; try { service.Acquire(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && service.Consumers == 0 && rollback == 1, "Failed acquisition retained a registration");
        }
        var states = new ActivitySamples(); states.Capture(0, new[] { new PlayerActivityView { Peer = 1, Session = 1, StateRevision = 1 } });
        Check(states.Drain(6).Count == 1, "Initial shared state omitted"); states.Capture(60, new[] { new PlayerActivityView { Peer = 1, Session = 1, StateRevision = 1 } });
        Check(states.Drain(6).Count == 0, "Unchanged actor rotated into optional logs");
        Check(typeof(PlayerActivityService).Assembly.GetType("HeimKit.Features.AFKNightWatch.AFKNightWatchConfigurationMigration") == null, "Migration code remains");
        System.Console.WriteLine("PASS: 16 independent enable/behavior combinations, shared leases and one sample per interval, daylight idle history, empty/unknown gating, reconnect/world grace, isolated failures, acquisition rollback and repeatable cleanup.");
    }
    private sealed class Presence : IPlayerPresence
    {
        public int? JoinedCount => ConnectedCount;
        public bool? HasJoinedPlayers => HasConnectedPlayers;
        public int? Connected, Ready;
        public int? ConnectedCount => Connected;
        public int? ReadyCount => Ready;
        public bool? HasConnectedPlayers => Connected.HasValue ? Connected > 0 : null;
        public IReadOnlyList<ZNetPeer> Peers => Array.Empty<ZNetPeer>();
        public void Refresh() { }
        public void Clear() { Connected = null; Ready = null; }
    }
}
