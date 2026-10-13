using BepInEx.Logging;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.WeatherGuard.Models;

internal static class WeatherGuardDiagnosticsTests
{
    public static void Run()
    {
        using var logger = new ManualLogSource("WeatherGuard diagnostics tests");
        var messages = new List<(LogLevel Level, string Text)>();
        logger.LogEvent += (_, args) => messages.Add((args.Level, args.Data.ToString()!));
        var disabled = new WeatherGuardDiagnostics(logger, false);
        disabled.Report(0, () => throw new Exception("Disabled path collected data"), null, null,
            () => throw new Exception("Disabled path collected geometry"));
        disabled.Sample(0, () => throw new Exception("Disabled sample formatted"));
        if (disabled.TryWearSample(0, "piece", true) || disabled.ReserveCommand(0) || messages.Count != 0)
            throw new Exception("Disabled diagnostics collected or printed optional evidence.");
        disabled.Wear(new WearObservation(), 100, true);
        if (messages.Count != 1 || messages[0].Level != LogLevel.Error)
            throw new Exception("Debug-off suppressed native failure reporting.");
        messages.Clear();
        var medium = new WeatherGuardDiagnostics(logger, true, DebugLogLevel.Medium, false);
        var first = new WeatherGuardStatus { Eligible = 61, Protected = 61, WearPasses = 100,
            Decisions = new() { ["waiting_wet_timer"] = 80, ["native_environment_dry"] = 20 } };
        medium.Report(0, () => first, () => throw new Exception("Medium collected detailed health histories"), null,
            () => new() { ["roof_sample_exposed_now"] = 3, ["roof_sample_covered_now"] = 58, ["player_biomes"] = "Swamp:1" });
        if (messages.Count != 1 || !messages[0].Text.Contains("Protection path") ||
            !messages[0].Text.Contains("eligible_now=61") || !messages[0].Text.Contains("waiting_wet_timer_interval=80") ||
            !messages[0].Text.Contains("native_weather_due_interval=0") || !messages[0].Text.Contains("roof_sample_exposed_now=3"))
            throw new Exception("Medium does not explain the prevention path in one grouped summary.");
        medium.Report(59, () => throw new Exception("Path interval opened early"));
        medium.Report(60, () => new WeatherGuardStatus { Eligible = 61, Protected = 61, WeatherGates = 3,
            WeatherSuppressed = 3, ConfirmedPreventions = 3, WearPasses = 161,
            Decisions = new() { ["waiting_wet_timer"] = 138, ["native_environment_dry"] = 20 } });
        if (messages.Count != 2 || !messages[1].Text.Contains("confirmed_preventions_interval=3") ||
            !messages[1].Text.Contains("waiting_wet_timer_interval=58"))
            throw new Exception("Medium confuses interval steps with lifetime counts or omits prevention.");
        medium.Sample(0, () => throw new Exception("Medium formatted detailed sample"));
        if (medium.TryWearSample(0, "piece", true)) throw new Exception("Medium reserved detailed evidence.");
        messages.Clear();
        var detail = new WeatherGuardDiagnostics(logger, true);
        var watch = new RainEvidence { Identity = new() { ["object"] = "wood-a" }, Eligibility = "eligible",
            Protected = true, FirstHealth = 400, Health = 400, MinimumHealth = 400,
            Fields = new() { ["processor"] = "server", ["cached_roof"] = true, ["environment_wet"] = false } };
        detail.Report(0, () => first, () => new[] { watch });
        if (messages.Count != 2 || !messages[1].Text.Contains("Processing and network counters"))
            throw new Exception("Detailed mode lacks diagnostic counters or repeats routine piece-health results.");
        detail.Report(60, () => first, () => new[] { watch, watch, watch });
        if (messages.Count != 4 || messages.Any(m => m.Text.Contains("no health loss observed")))
            throw new Exception("Detailed output repeats uninformative per-piece results.");
        watch.ObserveState(380, 7, true, 0); watch.ObserveState(400, 7, true, 0);
        detail.Report(120, () => first, () => new[] { watch });
        if (!messages[^1].Text.Contains("Health loss observed; weather cause unconfirmed") ||
            !messages[^1].Text.Contains("health_lost_interval=20") || !messages[^1].Text.Contains("health_after=400"))
            throw new Exception("Health loss followed by repair vanished from useful diagnostics.");
        int afterLoss = messages.Count;
        detail.Report(180, () => first, () => new[] { watch });
        if (messages.Count != afterLoss + 2) throw new Exception("Historical health loss repeated as a new anomaly.");
        detail.Reset(); messages.Clear();
        for (int i = 0; i < 3; i++) detail.Wear(new WearObservation { Owner = true, Protected = true,
            Identity = new() { ["object"] = "wood-" + i }, Fields = new() { ["environment_wet"] = false, ["cached_roof"] = true } }, 400, false);
        if (messages.Count != 1) throw new Exception("Identical native blocking phases printed separately for every piece.");
        detail.Wear(new WearObservation { Owner = true, Protected = true, GateReached = true, Suppressed = true,
            Fields = new() { ["environment_wet"] = true, ["cached_roof"] = false } }, 400, false);
        if (!messages[^1].Text.Contains("native_weather_contribution_due=true") || !messages[^1].Text.Contains("weather_contribution_suppressed=true"))
            throw new Exception("Detailed evidence does not distinguish native due damage from managed exposure.");
        detail.Reset(); messages.Clear(); int formatted = 0;
        for (int i = 0; i < 32; i++) detail.Sample(0, () => { formatted++; return "ownership evidence"; });
        if (formatted != WeatherGuardDiagnostics.SampleLimit || messages.Count != WeatherGuardDiagnostics.SampleLimit)
            throw new Exception("Detailed stream budget fails to bound formatting/output.");
        detail.Sample(59, () => throw new Exception("Sample interval reopened early"));
        detail.Sample(60, () => "new window");
        if (!messages[^1].Text.Contains("new window")) throw new Exception("Detailed sample budget failed to recover.");
        detail.Reset(); messages.Clear();
        detail.Report(0, () => new WeatherGuardStatus { Eligible = 1765, Protected = 1765, WearPasses = 53350,
            Acquired = 1997, Released = 232, ReleaseTotals = new() { ["context lost"] = 171, ["outside retained region"] = 61 } });
        if (messages.Count != 2 || !messages[1].Text.Contains("acquired_interval=1997") ||
            !messages[1].Text.Contains("released_interval=232") ||
            !messages[1].Text.Contains("release_reasons_interval=context lost:171,outside retained region:61"))
            throw new Exception("Acquisitions/releases are not reported as one grouped interval result with reason totals.");
        if (messages[1].Text.Contains("wear_calls_interval") || messages[1].Text.Contains("weather_gates_interval") ||
            messages[0].Text.Contains("eligible_player_pieces_now"))
            throw new Exception("Companion diagnostics repeat identical wear/eligibility counters.");
        detail.Report(60, () => new WeatherGuardStatus { Acquired = 3259, Released = 1494,
            ReleaseTotals = new() { ["context lost"] = 1433, ["outside retained region"] = 61 } });
        if (!messages[^1].Text.Contains("acquired_interval=1262") || !messages[^1].Text.Contains("released_interval=1262") ||
            !messages[^1].Text.Contains("release_reasons_interval=context lost:1262 |"))
            throw new Exception("Ownership churn interval repeats old release reasons or loses new grouped results.");
        System.Console.WriteLine("PASS: Medium prevention steps, Detailed counters and representative blocking phases, quiet unchanged health, retained repaired loss/error evidence, lazy policy and bounded samples.");
    }
}
