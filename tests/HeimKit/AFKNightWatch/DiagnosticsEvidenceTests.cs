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
internal static class DiagnosticsEvidenceTests
{
    private static void Check(bool okay, string reason) { if (!okay) throw new Exception(reason); }
    private static PlayerActivityObservation Actor(bool known = true, bool alive = true, float x = 0, float health = 100)
        => new() { Peer = 1, Character = "same", Known = known, Alive = alive, Health = health, X = x };
    public static void Run()
    {
        var tracker = new PlayerActivityTracker(new(5, 2, .5f, 10, 1));
        PlayerActivityView Step(float time, PlayerActivityObservation? actor = null) => tracker.Update(time, new[] { actor ?? Actor() })[0];
        var first = Step(0); long session = first.Session;
        Check(first.Recovery == "join" && first.GraceRemaining == 2, "Join evidence and grace");
        for (int t = 1; t <= 5; t++) Step(t);
        long revision = first.StateRevision; Check(first.Idle, "Idle state observed");
        tracker.Signal(1, 5.2f, "RPC_Repair");
        Check(!first.Idle && first.StateRevision > revision && first.LastSignal == "RPC_Repair", "Routed signal wakes idle actor with exact method name");
        Step(6); Check(first.LastSignal == "RPC_Repair", "Last activity signal survives a quiet sample");
        Step(7, Actor(health: 90)); Check(first.LastSignal == "RPC_Repair" && first.LastActivityAt == 5.2f, "Health loss must not replace player activity evidence or reset its time");
        Step(8, Actor(known: false)); Step(9);
        Check(first.Recovery == "unreliable_actor_recovery" && first.GraceRemaining == 2, "Unknown recovery evidence");
        Step(15); Check(first.Recovery == "observation_gap", "Observation gap evidence");
        Step(14); Check(first.Recovery == "clock_rollback", "Rollback recovery evidence");
        tracker.Clear(); var next = Step(16); Check(next.Session != session, "Reset starts distinct diagnostic session");
        var samples = new ActivitySamples(); samples.Capture(16, new[] { next }); samples.Drain(6);
        for (int t = 17; t <= 21; t++) Step(t);
        samples.Capture(21, new[] { next }); tracker.Signal(1, 21.2f, "ChatMessage"); samples.Capture(22, new[] { next });
        Check(samples.Pending == 1 && samples.TransitionsInterval >= 2 && samples.ReplacedPendingInterval > 0, "Transient idle then active counted despite bounded pending replacement");
        Check(samples.Drain(6).Single().LastSignal == "ChatMessage", "Latest changed state has priority");
        samples.Capture(23, Array.Empty<PlayerActivityView>());
        Check(samples.Drain(6).Single().Reason == "disconnected_or_outside_observed_scope", "Departed actor state observed");
        samples.Reset(); Check(samples.Pending == 0 && samples.Drain(6).Count == 0, "Reset removes stale states");

        var closed = new NativeSleepObservation { Server = true, Sleeping = false, SkippingBefore = false, SkippingAfter = false, Afternoon = false, Night = false, CooldownRemaining = 0 };
        Check(closed.Gate == "outside_sleep_time" && closed.Outcome == "no_time_skip_state_change", "No-change native gate");
        closed.Night = true; closed.CooldownRemaining = 8; Check(closed.Gate == "cooldown_pending", "Cooldown gate");
        closed.CooldownRemaining = 0; Check(closed.Gate == "time_gates_open_vote_required", "Open gates do not assert successful vote");
        closed.SkippingAfter = true; Check(closed.Outcome == "time_skip_became_active", "Observed state transition");
        closed.Failed = true; Check(closed.Outcome == "native_exception", "Exception is not success");
        var ended = new NativeSleepObservation { Server = true, Sleeping = true, SleepingAfter = false, SkippingBefore = false, SkippingAfter = false };
        Check(ended.Outcome == "sleep_session_ended_server_state", "Native sleep-session end observation");
        Check(new NativeSleepObservation().Outcome == "unknown", "Missing native evidence is unknown");
        var outcome = new RaidStartObservation { Requested = "raid", Current = "raid", StateChanged = false };
        Check(!outcome.Started, "Unchanged event is not a new start"); outcome.StateChanged = true;
        Check(outcome.Started, "Completed setter with new matching state is observed start");
        outcome.Failed = true; Check(!outcome.Started, "Failed setter is not confirmed start"); outcome.Failed = false; outcome.Current = "other";
        Check(!outcome.Started, "Different resulting event is not requested success");
        var local = RaidLocalObservation.Read(0, 0, 100, new[] {
            new PlayerActivityView { Known = false }, new PlayerActivityView { Known = true, Alive = true, Idle = true, X = 10 },
            new PlayerActivityView { Known = true, Alive = true, X = 100 }, new PlayerActivityView { Known = true, Alive = true, InBed = true },
            new PlayerActivityView { Known = true, Alive = false }, new PlayerActivityView { Known = true, Alive = true, Y = 3001 } });
        Check(local.Unknown == 1 && local.NearbyIdle == 1 && local.Outside == 1 && local.NearbyBed == 1 && local.NearbyDead == 1 && local.Dungeon == 1, "Native-range population breakdown preserves boundary and dungeon exclusions");
        var counts = new ObservationCounters(); counts.Hit("get_valid_event_points_invocations"); counts.Hit("empty_native_candidate_results"); counts.Add("native_records_rejected_before_afk", 4);
        Check(counts.Get("get_valid_event_points_invocations") == 1 && counts.Get("empty_native_candidate_results") == 1 && counts.Get("native_records_rejected_before_afk") == 4 && counts.Get("unknown") == 0, "Named hook/empty/rejection counts distinct");

        using var logger = new ManualLogSource("Diagnostic evidence checks"); var messages = new List<string>();
        var levels = new List<LogLevel>(); logger.LogEvent += (_, e) => { messages.Add(e.Data.ToString()!); levels.Add(e.Level); };
        var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, "evidence-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var debug = DiagnosticsSettings.Bind(config); var policy = new DebugLoggingPolicy(debug, false);
        var raid = new AFKRaidWatchDiagnostics(logger, policy); var majority = new AFKNightWatchDiagnostics(logger, policy);
        raid.Sample(0, "raid", default, false, "unknown_actor_data", () => throw new Exception("Suppressed probe ran"));
        majority.Native(closed); Check(messages.Count == 0, "Debug-off collection skipped");
        debug.Value = true;
        for (int i = 0; i < 50; i++) raid.Sample(0, "raid", new UnityEngine.Vector3(i, 0, 0), false, "nearby_active_player", () => local);
        Check(messages.Count == 6, "Local candidate stream capped at six");
        raid.Sample(1, "raid", default, false, "nearby_active_player", () => throw new Exception("Unchanged probe rerun"));
        raid.Report(1, () => new RaidWatchStatus { Connected = 6, Candidates = 50, Hooks = counts });
        Check(messages.Any(m => m.Contains("omitted_messages_interval=RAID_CANDIDATE:44") && m.Contains("unchanged_candidate_samples_interval=1")), "Omitted and unchanged attempts retained");
        messages.Clear();
        var probe = new NativeSleepObservation { ObservedAt = 0, Server = true, Sleeping = false, Night = true, Afternoon = false, SkippingBefore = false, SkippingAfter = false, CooldownRemaining = 0 };
        majority.Native(probe); majority.Native(probe); Check(messages.Count == 1, "Unchanged native evidence suppressed");
        majority.Report(30, () => new NightWatchStatus { Players = 1, Vote = new SleepDecision { Participants = 1 }, Sleep = probe });
        Check(messages.Any(m => m.Contains("native_probe_age_s=30") && m.Contains("unchanged_native_samples_interval=1")), "Age retained without claiming a current outcome");
        Check(levels.All(level => level == LogLevel.Debug), "Optional evidence retains Debug severity");
        raid.Warning(0, new Exception("Observation unavailable")); Check(levels[^1] == LogLevel.Warning, "Warning severity preserved");
        const BindingFlags methods = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        foreach (string name in new[] { "HaveGlobalKeys", "InValidBiome", "CheckBase", "SetRandomEvent", "GetPossibleRandomEvents", "StartRandomEvent" })
            Check(typeof(RandEventSystem).GetMethod(name, methods) != null, "Native observational hook target missing: " + name);
        Check(typeof(EnvMan).GetMethod("SkipToMorning", methods) != null && typeof(Game).GetField("m_sleeping", methods)!.FieldType == typeof(bool) && typeof(Game).GetField("m_lastSleepTime", methods)!.FieldType == typeof(double), "Native sleep observational signatures");
        System.Console.WriteLine("PASS: activity signals/transitions/recovery/reset, native sleep gates/no-change/failure/end evidence, local raid population/start boundaries, named counters, lazy suppression, sample caps/omissions and observational native signatures. Unity delivery remains unverified.");
    }
    private sealed class LazyEnumerable : IEnumerable<PlayerActivityView>
    {
        public IEnumerator<PlayerActivityView> GetEnumerator() => throw new Exception("Suppressed actor enumeration ran");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
