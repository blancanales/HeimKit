using BepInEx.Logging;
using BepInEx.Configuration;
using HeimKit.Configuration;
using HeimKit.Core.Diagnostics;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.WeatherGuard.Models;

internal static class WeatherGuardDiagnosticsTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        DiagnosticLog.StartSession();
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        DiagnosticsSettings.Bind(config); var verbosity = config.Bind("Diagnostics", "DebugVerbosity", DebugVerbosity.Normal);
        using var logger = new ManualLogSource("WeatherGuard diagnostics tests"); var messages = new List<(LogLevel Level, string Text)>();
        logger.LogEvent += (_, args) => messages.Add((args.Level, LogCapture.Text(args.Data)));
        var disabled = new WeatherGuardDiagnostics(logger, false);
        disabled.Report(0, () => throw new Exception("Disabled status collection"), exposure: () => throw new Exception("Disabled exposure collection"));
        disabled.Sample(0, () => throw new Exception("Disabled sample formatting"));
        Check(!disabled.TryWearSample(0, "piece", true) && !disabled.ReserveCommand(0) && messages.Count == 0, "Disabled diagnostics collected optional evidence.");
        disabled.Wear(new WearObservation(), 100, true); Check(messages.Count == 1 && messages[0].Level == LogLevel.Error, "Debug-off hid a native failure.");
        messages.Clear(); var medium = new WeatherGuardDiagnostics(logger, true, DebugLogLevel.Medium, false);
        var first = new WeatherGuardStatus { Eligible = 61, Protected = 61, WearPasses = 100, Decisions = new() { ["waiting_wet_timer"] = 80 } };
        medium.Report(0, () => first, () => throw new Exception("Medium health history"), exposure: () => throw new Exception("Normal built Verbose fields"));
        Check(messages.Count == 1 && messages[0].Text.Contains(".scope:") && messages[0].Text.Contains("eligible=61") && messages[0].Text.Contains("wear_calls=100") && !messages[0].Text.Contains("native_weather_due="), "Normal summary lost scope, omission or its field cap.");
        medium.Report(59, () => throw new Exception("Cadence opened early"));
        medium.Report(60, () => new WeatherGuardStatus { Eligible = 61, Protected = 61, WearPasses = 161, WeatherGates = 3, WeatherSuppressed = 3, ConfirmedPreventions = 3 });
        Check(messages.Count == 2 && messages[1].Text.Contains("wear_calls=61") && messages[1].Text.Contains("confirmed_preventions=3"), "Summary confused interval and lifetime counters.");
        medium.Sample(0, () => throw new Exception("Medium detailed sample")); Check(!medium.TryWearSample(0, "piece", true), "Medium reserved detailed wear.");
        messages.Clear(); var detailed = new WeatherGuardDiagnostics(logger, true);
        var watch = new RainEvidence { Identity = new() { ["object"] = "wood-a" }, Eligibility = "eligible", Protected = true, FirstHealth = 400, Health = 400, MinimumHealth = 400 };
        detailed.Report(0, () => first, () => new[] { watch });
        Check(messages.Count == 2 && messages[1].Text.Contains("network activity"), "Detailed mode lost normal counter evidence.");
        detailed.Report(60, () => first, () => new[] { watch }); Check(messages.Count == 4, "Unchanged health emitted a piece result.");
        watch.ObserveState(380, 7, true, 0); watch.ObserveState(400, 7, true, 0);
        detailed.Report(120, () => first, () => new[] { watch });
        Check(messages[^1].Text.Contains("weather cause is unconfirmed") && messages[^1].Text.Contains("health_lost=20") && messages[^1].Text.Contains("health_after=400"), "Repaired health loss vanished or was attributed to weather.");
        int before = messages.Count; detailed.Report(180, () => first, () => new[] { watch }); Check(messages.Count == before + 2, "Historical loss repeated as a new anomaly.");
        detailed.Reset(); messages.Clear();
        for (int i = 0; i < 3; i++) detailed.Wear(new WearObservation { Owner = true, Protected = true, Identity = new() { ["object"] = "wood-" + i }, Fields = new() { ["environment_wet"] = false, ["cached_roof"] = true } }, 400, false);
        Check(messages.Count == 1, "Identical native blocking phases repeated by object.");
        detailed.Wear(new WearObservation { Owner = true, Protected = true, GateReached = true, Suppressed = true }, 400, false);
        Check(messages[^1].Text.Contains("Removed a due native weather contribution") && !messages[^1].Text.Contains("native_weather_contribution_due="), "Normal wear did not explain its observed outcome.");
        verbosity.Value = DebugVerbosity.Verbose; detailed.Reset(); messages.Clear();
        detailed.Wear(new WearObservation { Owner = true, Protected = true, GateReached = true, Suppressed = true }, 400, false);
        Check(messages.Count == 2 && messages[^1].Text.Contains("native_weather_contribution_due=true") && messages[^1].Text.Contains("weather_contribution_suppressed=true"), "Verbose wear lost supporting native evidence.");
        Check(messages[^1].Text.Contains(": Native wear evidence for the preceding record."), "Verbose wear without identity lost its fallback subject.");
        detailed.Reset(); messages.Clear();
        var identity = new Dictionary<string, object?> { ["object"] = "1:5", ["prefab"] = "wood_floor" };
        for (int i = 3; i <= 8; i++) identity["field_" + i] = i;
        detailed.Wear(new WearObservation { Owner = true, Protected = true, Identity = identity }, 400, true, new InvalidOperationException("boom"));
        var failure = messages.Find(m => m.Level == LogLevel.Error).Text ?? "";
        var continued = messages.Find(m => m.Text.Contains("Additional evidence for wood_floor (piece 1:5).")).Text ?? "";
        Check(failure.Contains("field_5=5") && !failure.Contains("field_6=") && failure.Contains("error_type=System.InvalidOperationException"), "Error wear exceeded its five evidence fields or lost the exception.");
        Check(continued.Contains("field_6=6 field_7=7 field_8=8") && !continued.Contains("field_5="), "Verbose did not continue exactly where the Error record stopped.");
        Check(messages.Exists(m => m.Text.Contains("Native wear evidence for wood_floor (piece 1:5).")), "Verbose wear evidence did not name its piece.");
        messages.Clear();
        detailed.Command(new WearObservation { Protected = true, Identity = new() { ["object"] = "1:7", ["prefab"] = "wood_pole2" }, Fields = new() { ["command"] = "RPC_Damage" }, Before = 400 }, 0, false);
        Check(messages[0].Text.Contains(": Native damage handler ran on a protected piece;") && messages[0].Text.Contains("command=RPC_Damage") && messages[0].Text.Contains("health_after=0"), "Native receiver record hid its handler or a destroyed piece's zero health: " + messages[0].Text);
        Check(!messages[1].Text.Contains("command="), "Verbose command evidence repeated the handler.");
        verbosity.Value = DebugVerbosity.Normal; detailed.Reset(); messages.Clear(); int formatted = 0;
        for (int i = 0; i < 32; i++) detailed.Sample(0, () => { formatted++; return "Observed ownership."; });
        Check(formatted == WeatherGuardDiagnostics.SampleLimit && messages.Count == WeatherGuardDiagnostics.SampleLimit, "Existing sample cap or lazy formatting changed.");
        detailed.Sample(59, () => throw new Exception("Early sample recovery")); detailed.Sample(60, () => "A new sample window opened.");
        Check(messages[^1].Text.Contains("new sample window"), "Sample window failed to recover.");
        detailed.Reset(); messages.Clear();
        detailed.Report(0, () => new WeatherGuardStatus { Acquired = 1997, Released = 232, ReleaseTotals = new() { ["context lost"] = 171, ["outside retained region"] = 61 } });
        Check(messages.Count == 2 && messages[1].Text.Contains("acquired=1997") && messages[1].Text.Contains("released=232") && messages[1].Text.Contains("release_reasons=\"context lost:171,outside retained region:61\""), "Grouped ownership deltas or logfmt quoting changed.");
        detailed.Report(60, () => new WeatherGuardStatus { Acquired = 3259, Released = 1494, ReleaseTotals = new() { ["context lost"] = 1433, ["outside retained region"] = 61 } });
        Check(messages[^1].Text.Contains("acquired=1262") && messages[^1].Text.Contains("released=1262") && messages[^1].Text.Contains("release_reasons=\"context lost:1262\""), "Ownership interval repeated old release reasons.");
        System.Console.WriteLine("PASS: Normal/Verbose protection evidence, interval deltas, quiet unchanged health, repaired-loss uncertainty, phase deduplication and unchanged independent stream caps.");
    }
}
