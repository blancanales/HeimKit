using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using UnityEngine;

internal static class PlayerArrivalDiagnosticsTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Presence : IPlayerPresence
    {
        public bool? HasConnectedPlayers => true;
        public bool? HasJoinedPlayers => true;
        public int? ConnectedCount => 1;
        public int? ReadyCount => 1;
        public int? JoinedCount => 1;
        public IReadOnlyList<ZNetPeer> Peers => Array.Empty<ZNetPeer>();
        public void Refresh() { }
        public void Clear() { }
    }
    private static DebugLoggingPolicy Policy(bool enabled, bool suppressed = false)
    {
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false);
        return new(config.Bind("PlayerActivity", "DebugMode", enabled), suppressed);
    }
    private static ZNetPeer Peer(long id = 1)
    {
        var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer));
        peer.m_uid = id; peer.m_playerName = "Arrival player"; return peer;
    }
    private static IList Windows(PlayerArrivalDiagnostics observer) =>
        (IList)typeof(PlayerArrivalDiagnostics).GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(observer)!;
    private static object Field(object value, string field) => value.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    internal static void Run()
    {
        StageBoundaries(); Transitions(); CorrelatedFrame(); Admission(); FailureIsolation();
    }
    private static void StageBoundaries()
    {
        DiagnosticLog.StartSession(); PerformanceStages.CaptureArrivals = true;
        using var log = new ManualLogSource("Arrival stage boundaries");
        var lines = new List<string>(); log.LogEvent += (_, e) => lines.Add(LogCapture.Text(e.Data));
        PerformanceStages.Record("HeimKit", "player_services", 301, 7, 11, 1, "arrival");
        PerformanceStages.Record("HeimKit", "player_services", 2, 1, 1, 0);
        PerformanceStages.Report(log); // A normal minute boundary must not erase arrival correlation.
        var samples = new List<PerformanceStages.ArrivalSample>(); PerformanceStages.EndArrivalFrame(samples.Add);
        var sample = samples.Single(s => s.Module == "HeimKit" && s.Stage == "player_services");
        Check(sample.Calls == 2 && sample.Total == 303 && sample.Maximum == 301 && sample.Items == 7 && sample.Bytes == 11 && sample.Collections == 1,
            "Arrival drain lost the maximum's counts/GC or was cleared by normal reporting.");
        Check(lines.Any(s => s.Contains("player_services") && s.Contains("301.00")), "Arrival capture consumed normal health evidence.");
        samples.Clear(); PerformanceStages.EndArrivalFrame(samples.Add); Check(samples.Count == 0, "A completed arrival frame leaked measurements forward.");
        PerformanceStages.CaptureArrivals = false;
        PerformanceStages.Record("HeimKit", "player_services", 3); PerformanceStages.EndArrivalFrame(samples.Add);
        Check(samples.Count == 0, "Disabled arrival collection retained per-frame samples.");
        DiagnosticLog.StartSession();
        System.Console.WriteLine("PASS: arrival scopes reuse normal health timing, retain correlated maximum counts/GC across a minute flush, drain once and stop collecting when disabled.");
    }
    private static void Transitions()
    {
        DiagnosticLog.StartSession(); using var log = new ManualLogSource("Arrival transitions");
        var presence = new Presence();
        using var observer = new PlayerArrivalDiagnostics(presence, Policy(true), log, new PlayerWeatherContext(), _ => ("Clear", false, true));
        var peer = Peer(); var actor = new ZDOID(1, 1);
        observer.ObservePosition(peer, actor, Vector3.zero, 0);
        observer.ObservePosition(peer, actor, new Vector3(10, 0, 0), .2f);
        Check(Windows(observer).Count == 1, "Ordinary movement created an arrival.");
        observer.ObservePosition(peer, actor, new Vector3(200, 0, 0), .4f);
        Check(Windows(observer).Count == 2 && (string)Field(Windows(observer)[1]!, "Kind") == "position_jump_inferred", "A fast displacement was not separated from first actor observation.");
        var reconnect = Peer(); observer.ObservePosition(reconnect, actor, Vector3.zero, .5f);
        Check((long)Field(Windows(observer)[1]!, "Session") != (long)Field(Windows(observer)[2]!, "Session"), "Reused peer ID inherited another physical session.");
        observer.ObservePosition(peer, new ZDOID(1, 2), Vector3.zero, 1);
        Check((string)Field(Windows(observer)[3]!, "Kind") == "character_changed", "Respawn/character replacement was labeled as teleporting.");
        observer.ObservePosition(peer, new ZDOID(1, 2), new Vector3(1000, 0, 0), 5);
        Check((string)Field(Windows(observer)[4]!, "Kind") == "relocation_after_observation_gap", "A long observation gap became a false portal confirmation.");
        observer.ObservePosition(peer, actor, new Vector3(float.NaN, 0, 0), 6);
        Check(Windows(observer).Count == 5, "Nonfinite actor position created a diagnostic window.");
        for (int i = 0; i < 100; i++) observer.ObservePosition(Peer(i + 10), actor, Vector3.zero, 7);
        Check(Windows(observer).Count == 64, "Arrival window state exceeded its bound.");
        observer.Dispose(); Check(Windows(observer).Count == 0 && !PerformanceStages.CaptureArrivals, "Disposal retained arrival state or collection.");
        using var hidden = new PlayerArrivalDiagnostics(presence, Policy(true, true), log, new PlayerWeatherContext());
        hidden.ObservePosition(peer, actor, Vector3.zero, 0); Check(Windows(hidden).Count == 0, "Hidden suppression allowed arrival tracking.");
        System.Console.WriteLine("PASS: arrivals distinguish first observation, normal movement, inferred jumps, character changes and observation gaps; physical sessions, hidden suppression, capacity and disposal stay isolated.");
    }
    private static void CorrelatedFrame()
    {
        DiagnosticLog.StartSession(); ModuleScheduler.Current.Clear();
        using var log = new ManualLogSource("Arrival correlated frame"); var lines = new List<string>();
        log.LogEvent += (_, e) => lines.Add(LogCapture.Text(e.Data));
        var presence = new Presence();
        using var provider = new WorldObjectProvider(presence, (_, _, _, _) => throw new Exception("Arrival diagnostics triggered world discovery"), observe: false);
        using var observer = new PlayerArrivalDiagnostics(presence, Policy(true), log, new PlayerWeatherContext(), _ => ("Clear", false, true));
        observer.ObservePosition(Peer(), new ZDOID(1, 1), Vector3.zero, 0);
        PerformanceStages.Record("HeimKit", "player_services", 300);
        PerformanceStages.Record("HeimKit", "world_preparation", 20);
        PerformanceStages.Record("HeimKit", "feature_dispatch", 150);
        PerformanceStages.Record("Core", "terrain_prepare", 100, 57, -1, 1);
        observer.Capture(.1f, 480, 33, 1, 7);
        PerformanceStages.Record("HeimKit", "player_services", 2);
        PerformanceStages.Record("HeimKit", "feature_dispatch", 3);
        PerformanceStages.ArrivalProgress("SharedMap.tables", 2, 4, true);
        observer.Capture(2.1f, 5, 500, 0, 8);
        string interim = lines.Single(s => s.Contains("PlayerActivity.arrival"));
        Check(interim.Contains("max_ms=480.00") && interim.Contains("max_frame=7") && interim.Contains("players_ms=300.00") && interim.Contains("features_ms=150.00") &&
            interim.Contains("preceding_interval_ms=33.00") && interim.Contains("interval_max_ms=500.00") && interim.Contains("max_frame_gc0=1"), "The worst update used another frame's phase, interval or GC measurements.");
        Check(interim.Contains("pending=4,active=true") && interim.Contains("cached_zone_records=unavailable") && interim.Contains("client_loading=unobservable") && !interim.Contains("omitted_fields="), "Normal arrival evidence lost backlog/limits, invented coverage or exceeded eight fields.");
        observer.Capture(10.1f, 4, 33, 0, 9); observer.Capture(11, 4, 33, 0, 10);
        Check(lines.Count(s => s.Contains("PlayerActivity.arrival")) == 2 && Windows(observer).Count == 0, "Arrival did not finish once after its ten-second observation window.");
        System.Console.WriteLine("PASS: arrival summaries retain the exact slowest update's phase/GC/interval breakdown, distinguish independent interval maxima, preserve partial backlog/coverage and finish without diagnostic world queries.");
    }
    private static void Admission()
    {
        DiagnosticLog.StartSession(); ModuleScheduler.Current.Clear();
        using var log = new ManualLogSource("Arrival admission"); var lines = new List<string>(); log.LogEvent += (_, e) => lines.Add(LogCapture.Text(e.Data));
        var policy = Policy(true); var fields = new[] { DiagnosticLog.Field("player", "arrival") };
        ModuleScheduler.Current.Combat.SetRaid(true);
        for (int i = 0; i < 100; i++) { ServerPerformance.BeginUpdate(); DiagnosticLog.WriteArrival(log, policy, "arrival", fields); }
        Check(lines.Count == 12 && ModuleScheduler.Current.Combat.Raid, "Arrival output exceeded its ceiling or changed battle policy.");
        DiagnosticLog.StartSession(); ServerPerformance.BeginUpdate(); ServerPerformance.OptionalOutput(Stopwatch.Frequency);
        Check(!DiagnosticLog.ArrivalAvailable(log, policy), "Arrival output bypassed the shared elapsed limit.");
        DiagnosticLog.StartSession(); Check(!DiagnosticLog.ArrivalAvailable(log, Policy(true, true)) && !DiagnosticLog.ArrivalAvailable(log, Policy(false)), "Arrival output ignored suppression/disabled policy.");
        ModuleScheduler.Current.Clear(); DiagnosticLog.StartSession();
        System.Console.WriteLine("PASS: arrival evidence stays available during raids, respects its twelve-record ceiling, shared output-time limit and hidden/global policy, and never changes gameplay admission.");
    }
    private static void FailureIsolation()
    {
        DiagnosticLog.StartSession(); ModuleScheduler.Current.Clear();
        using var log = new ManualLogSource("Arrival failure");
        log.LogEvent += (_, e) => { if (LogCapture.Text(e.Data).Contains("PlayerActivity.arrival")) throw new InvalidOperationException("listener failure"); };
        using var observer = new PlayerArrivalDiagnostics(new Presence(), Policy(true), log, new PlayerWeatherContext(), _ => ("Clear", false, true));
        observer.ObservePosition(Peer(), new ZDOID(1, 1), Vector3.zero, 0);
        ModuleScheduler.Current.Combat.SetRaid(true);
        observer.EndFrame(3, Stopwatch.GetTimestamp());
        Check(Windows(observer).Count == 0 && !PerformanceStages.CaptureArrivals && ModuleScheduler.Current.Combat.Raid, "Diagnostic failure escaped cleanup or altered combat policy.");
        ModuleScheduler.Current.Clear(); DiagnosticLog.StartSession();
        System.Console.WriteLine("PASS: arrival diagnostic listener failures stop only the observer and preserve gameplay/combat state.");
    }
}
