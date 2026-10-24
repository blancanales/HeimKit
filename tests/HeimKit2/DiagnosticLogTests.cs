using System.Globalization;
using BepInEx.Logging;
using BepInEx.Configuration;
using HeimKit.Configuration;
using HeimKit.Core.Diagnostics;
using UnityEngine;

internal static class DiagnosticLogTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Gate : FeatureDiagnostics
    {
        public Gate(ManualLogSource log, Func<bool> enabled) => ConfigureDiagnostics(enabled, log, "Gate");
        public void Probe() { if (OptionalEnabled) throw new Exception("Disabled diagnostic executed."); }
    }
    public static void Run()
    {
        var culture = CultureInfo.CurrentCulture; var clock = DiagnosticLog.WallClock;
        try
        {
            DiagnosticLog.StartSession(); CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            DiagnosticLog.WallClock = () => new DateTime(2026, 10, 24, 21, 14, 32);
            using var logger = new ManualLogSource("Log grammar checks"); var lines = new List<(LogLevel Level, string Text)>();
            logger.LogEvent += (_, entry) => lines.Add((entry.Level, entry.Data.ToString()!));
            DiagnosticLog.Write(logger, "WeatherGuard", "Ready.");
            DiagnosticLog.Write(logger, "WeatherGuard", "Limited.\nTry again.", LogLevel.Warning);
            DiagnosticLog.Write(logger, "WeatherGuard", CoreLogTopics.Scope, "Observed protection.", new (string, object?)[] { ("health", 237.9893f), ("managed", true) }, LogLevel.Debug);
            Exception error; try { throw new InvalidOperationException("bad\ninput"); } catch (Exception caught) { error = caught; }
            DiagnosticLog.Write(logger, "WeatherGuard", CoreLogTopics.Lifecycle, "Operation failed.", level: LogLevel.Error, error: error);
            Check(lines[0] == (LogLevel.Info, "21:14:32 WeatherGuard: Ready."), "Info grammar changed.");
            Check(lines[1] == (LogLevel.Warning, "21:14:32 WeatherGuard: Limited. Try again."), "Warning grammar or sentence escaping changed.");
            Check(lines[2].Text == "21:14:32 WeatherGuard.scope: Observed protection. health=237.989 managed=true", "Debug grammar or invariant rounding changed.");
            Check(lines[3].Level == LogLevel.Error && lines[3].Text.StartsWith("21:14:32 WeatherGuard.lifecycle: Operation failed. error_type=System.InvalidOperationException error=\"bad\\ninput\" stack="), "Exception fields or Error grammar changed.");
            Check(lines.All(line => !line.Text.Contains('\n') && !line.Text.Contains('\r') && !line.Text.Contains("elapsed_s=")), "A record spans physical lines or contains elapsed time.");
            var nested = new TypeInitializationException("Placement", new MissingFieldException("Native field lookup failed."));
            string nestedLine = DiagnosticLog.Format("TameTravel", CoreLogTopics.Lifecycle, "Placement initialization failed.", level: LogLevel.Error, error: nested);
            Check(nestedLine.Contains("System.TypeInitializationException") && nestedLine.Contains("System.MissingFieldException") && nestedLine.Contains("Native field lookup failed.") && !nestedLine.Contains('\n'), "First-failure evidence lost or split an inner exception.");
            foreach (string invalid in new[] { "", "Scope", "1scope", "scope.bad", "bad-key", "bad key" })
            { bool rejected = false; try { _ = new LogTopic(invalid); } catch (ArgumentException) { rejected = true; } Check(rejected, "Invalid topic accepted: " + invalid); }
            Check(new LogTopic("scope_2").Name == "scope_2", "Valid topic rejected.");
            LogFields fields = new LogField[] { ("empty", ""), ("unknown", "unknown"), ("nothing", null), ("zero", 0), ("off", false),
                DiagnosticLog.FieldAlways("kept_zero", 0), DiagnosticLog.FieldAlways("kept_off", false), DiagnosticLog.FieldAlways("kept_empty", ""),
                ("distance_m", 12.345f), ("duration_ms", .02326937799), ("wait_s", 1.26d), ("pos", new Vector3(926.98f, 31.74f, 485.61f)),
                ("missing_object", ZDOID.None), ("dropped", new DiagnosticBudget().TakeOmitted()), ("object", new ZDOID(1, 23)), ("tail", 99) };
            string rounded = DiagnosticLog.Format("TameTravel", new LogTopic("travel"), "Observed movement.", fields);
            Check(rounded.Contains("kept_zero=0 kept_off=false kept_empty=\"\" distance_m=12.3 duration_ms=0.02 wait_s=1.3 pos=927.0,31.7,485.6 object=1:23 omitted_fields=1"), "Omission, units, position, identity or cap changed: " + rounded);
            Check(!rounded.Contains(" unknown=") && !rounded.Contains(" nothing=") && !rounded.Contains(" zero=") && !rounded.Contains(" off=") &&
                !rounded.Contains(" missing_object=") && !rounded.Contains(" dropped="), "Default omission changed or kept an absent identity or empty drop count.");
            var budget = new DiagnosticBudget(); budget.Reserve(0, "refill_results", 0);
            Check(budget.TakeOmitted() == "refill_results:1" && budget.TakeOmitted() == "", "Drop counts lost their stream name or were not cleared.");
            string keptDictionary = DiagnosticLog.Format("SharedMap", new LogTopic("dungeons"), "Observed pickups.", new Dictionary<string, object?> {
                ["remaining_percent"] = DiagnosticLog.Kept(0), ["changed"] = DiagnosticLog.Kept(false), ["picked_pickups"] = 0, ["missing"] = null });
            string keptTuple = DiagnosticLog.Format("InfiniteTorches", new LogTopic("refill"), "Observed fuel.", new (string, object?)[] { ("fuel_before", DiagnosticLog.Kept(0f)), ("wait_s", 0f) });
            Check(keptDictionary.EndsWith("remaining_percent=0 changed=false") && keptTuple.EndsWith("fuel_before=0"), "Kept zero values were omitted from dictionary or tuple fields: " + keptDictionary + " | " + keptTuple);
            string escaped = DiagnosticLog.Format("HeimKit", CoreLogTopics.Config, "Read configuration.", new (string, object?)[] {
                ("name", "Onem Hlodvirsso"), ("path", "C:\\logs\\x=1\".txt\nnext\r\ttab"), ("simple", "abc:123"), ("bad|key", 1), ("simple", "duplicate") });
            Check(escaped.Contains("name=\"Onem Hlodvirsso\"") && escaped.Contains("path=\"C:\\\\logs\\\\x=1\\\".txt\\nnext\\r\\ttab\"") && escaped.Contains("simple=abc:123") && !escaped.Contains("duplicate") && !escaped.Contains("bad|key"), "Logfmt escaping, key validation or uniqueness changed: " + escaped);
            var many = Enumerable.Range(1, 12).Select(i => ("field_" + i, (object?)i)).ToArray();
            string capped = DiagnosticLog.Format("HeimKit", CoreLogTopics.Scope, "Normal evidence.", many);
            string verbose = DiagnosticLog.Format("HeimKit", CoreLogTopics.Scope, "Verbose evidence.", many, verbose: true);
            Check(capped.Contains("omitted_fields=4") && !capped.Contains("field_9=") && verbose.Contains("field_12=12") && !verbose.Contains("omitted_fields"), "Normal cap or Verbose field shape changed.");
            Check(typeof(DiagnosticLog).Assembly.GetType("HeimKit.Core.Diagnostics.Diagnostic" + "Event") == null, "Retired taxonomy type remains.");
            var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            var debug = DiagnosticsSettings.Bind(config); var gate = new Gate(logger, () => debug.Value);
            Check(DiagnosticsSettings.Verbosity == DebugVerbosity.Normal && !gate.VerboseEnabled, "Verbose is enabled by default.");
            config.Bind("Diagnostics", "DebugVerbosity", DebugVerbosity.Normal).Value = DebugVerbosity.Verbose;
            Check(!gate.VerboseEnabled, "Verbosity enabled feature debugging."); debug.Value = true; Check(gate.VerboseEnabled, "Verbose needs both switches.");
            DiagnosticLog.StartSession(); lines.Clear();
            for (int i = 0; i < 30; i++)
            {
                Check(gate.VerboseEnabled, "Shared ceiling closed before sixty records.");
                DiagnosticLog.Write(logger, "Gate", CoreLogTopics.Scope, "Normal observation.", level: LogLevel.Debug);
                DiagnosticLog.Write(logger, "Gate", CoreLogTopics.Scope, "Additional evidence.", level: LogLevel.Debug, verbose: true);
            }
            Check(lines.Count == 60 && !gate.VerboseEnabled, "Normal and Verbose do not share the sixty-record ceiling.");
            DiagnosticLog.Write(logger, "Gate", CoreLogTopics.Scope, "Excess observation.", level: LogLevel.Debug);
            DiagnosticLog.Write(logger, "Gate", CoreLogTopics.Scope, "Excess evidence.", level: LogLevel.Debug, verbose: true);
            Check(lines.Count == 60, "Optional debug ceiling was bypassed.");
            gate.Probe(); long cappedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) gate.Probe();
            Check(GC.GetAllocatedBytesForCurrentThread() == cappedBefore, "A budget-suppressed diagnostic allocated.");
            DiagnosticLog.Write(logger, "Gate", CoreLogTopics.Lifecycle, "An operation failed.", level: LogLevel.Error);
            Check(lines.Count == 61 && lines[60].Level == LogLevel.Error, "Optional debug ceiling suppressed a failure.");
            DiagnosticLog.StartSession();
            debug.Value = false; gate.Probe(); long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) gate.Probe();
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "A disabled diagnostic allocated.");
            // Warm buffers and compare only formatter allocation with equal-length output strings.
            LogFields fixedFields = new (string, object?)[] { ("health", 12.345f), ("duration_ms", 1.23456d) };
            string sample = DiagnosticLog.Format("HeimKit", CoreLogTopics.Scope, "Observed state.", fixedFields);
            for (int i = 0; i < 20; i++) DiagnosticLog.Format("HeimKit", CoreLogTopics.Scope, "Observed state.", fixedFields);
            before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) GC.KeepAlive(new string('x', sample.Length));
            long strings = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) GC.KeepAlive(DiagnosticLog.Format("HeimKit", CoreLogTopics.Scope, "Observed state.", fixedFields));
            Check(GC.GetAllocatedBytesForCurrentThread() - before == strings, "Warmed formatting allocates beyond its final string.");
            System.Console.WriteLine("PASS: timestamp/topic grammar, logfmt, units, omission, cap, Verbose policy, deleted taxonomy, zero disabled allocations and final-string-only warmed formatting.");
        }
        finally { CultureInfo.CurrentCulture = culture; DiagnosticLog.WallClock = clock; LogCapture.ResetVerbosity(); DiagnosticLog.StartSession(); }
    }
}
