using BepInEx.Logging;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.WeatherGuard.Models;

internal static class WeatherGuardDiagnosticsTests
{
    public static void Run()
    {
        using var logger = new ManualLogSource("WeatherGuard diagnostics tests");
        var messages = new List<string>();
        logger.LogEvent += (_, args) => messages.Add(args.Data.ToString()!);
        var disabled = new WeatherGuardDiagnostics(logger, false);
        disabled.Report(0f, () => throw new Exception("Debug-off snapshot evaluated."));
        disabled.Sample(0f, () => throw new Exception("Debug-off sample formatted."));
        if (disabled.TryWearSample(0, "piece", true) || disabled.ReserveCommand(0)) throw new Exception("Debug-off evidence reserved.");
        disabled.Wear(new WearObservation(), 100, false);
        disabled.Command(new WearObservation(), 100, false);
        if (messages.Count != 0) throw new Exception("Debug-off diagnostics emitted logs.");

        var diagnostics = new WeatherGuardDiagnostics(logger, true);
        diagnostics.Report(0f, () => new WeatherGuardStatus { Protected = 3, Eligible = 5, Limited = true });
        if (messages.Count != 2 || !messages[0].Contains("protected=3; eligible=5") || !messages[0].Contains("capacity limited=True"))
            throw new Exception("Status snapshot fields were not logged correctly.");
        diagnostics.Report(59f, () => throw new Exception("Throttled snapshot evaluated."));
        diagnostics.Report(60f, () => new WeatherGuardStatus { Protected = 8 });
        if (messages.Count != 4 || !messages[2].Contains("protected=8")) throw new Exception("Summary interval failed.");
        int formats = 0;
        for (int i = 0; i < 32; i++) diagnostics.Sample(0f, () => { formats++; return "acquisition sample"; });
        if (formats != WeatherGuardDiagnostics.SampleLimit || messages.Count != 4 + WeatherGuardDiagnostics.SampleLimit)
            throw new Exception("Sample cap failed or suppressed samples were formatted.");
        diagnostics.Sample(59f, () => throw new Exception("Sample budget reopened early."));
        diagnostics.Sample(60f, () => "Next sample window");
        if (!messages[^1].Contains("Next sample window")) throw new Exception("Sample budget did not recover.");
        int before = messages.Count;
        diagnostics.Reset();
        diagnostics.Report(0f, () => new WeatherGuardStatus());
        diagnostics.Sample(0f, () => "Reset sample window");
        if (messages.Count != before + 3) throw new Exception("Reset retained summary or sample limits.");
        // Loading/exclusion storms cannot consume acquisition, wear or command budgets.
        for (int i = 0; i < 100; i++) diagnostics.Sample(0, "context", () => "context blocked");
        diagnostics.Sample(0, "ownership", () => "ownership loss observed");
        if (!messages[^1].Contains("ownership loss observed")) throw new Exception("Streams share a sample budget.");
        foreach (bool protectedPiece in new[] { true, false })
        {
            for (int i = 0; i < WeatherGuardDiagnostics.WearSampleLimit; i++)
                if (!diagnostics.TryWearSample(0, "piece-" + i, protectedPiece)) throw new Exception("Wear samples missing.");
            if (diagnostics.TryWearSample(1, "piece-0", protectedPiece) || diagnostics.TryWearSample(59, "overflow", protectedPiece))
                throw new Exception("Wear evidence duplicated or cap exceeded.");
            if (!diagnostics.TryWearSample(60, "piece-0", protectedPiece)) throw new Exception("Wear window did not recover.");
        }
        for (int i = 0; i < WeatherGuardDiagnostics.SampleLimit; i++)
            if (!diagnostics.ReserveCommand(0)) throw new Exception("Native command budget missing.");
        if (diagnostics.ReserveCommand(59) || !diagnostics.ReserveCommand(60)) throw new Exception("Native command cap/interval failed.");
        diagnostics.Wear(new WearObservation { Object = "object=target", Protected = true, GateReached = false, Warmup = true, Before = 100 }, 100, false);
        if (!messages[^1].Contains("weather damage suppressed=False") || !messages[^1].Contains("warmup=True"))
            throw new Exception("Warmup was reported as suppression.");
        diagnostics.Wear(new WearObservation { Protected = true, GateReached = true, Suppressed = true, Before = 100 }, null, false);
        if (!messages[^1].Contains("object destroyed/invalid") || !messages[^1].Contains("weather damage suppressed=True"))
            throw new Exception("Native destruction or weather gate evidence missing.");
        diagnostics.Reset();
        diagnostics.Report(0, () => new WeatherGuardStatus { OwnerLosses = 2, WeatherGates = 12, WetOpportunities = 5,
            NativeCommands = 3, SupportPasses = 10, Exclusions = new() { ["world-created"] = 4 } });
        if (!messages[^1].Contains("world-created=4") || !messages[^1].Contains("ownership losses=2") ||
            !messages[^1].Contains("weather gates=12") || !messages[^1].Contains("wet opportunities=5") ||
            !messages[^1].Contains("native support passes=10")) throw new Exception("Actionable evidence summary missing.");
        System.Console.WriteLine("PASS: WeatherGuard diagnostics respect debug mode, format status snapshots, limit summaries and samples, defer suppressed formatting and reset.");
    }
}
