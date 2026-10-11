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
        if (messages.Count != 0) throw new Exception("Debug-off diagnostics emitted logs.");

        var diagnostics = new WeatherGuardDiagnostics(logger, true);
        diagnostics.Report(0f, () => new WeatherGuardStatus { Protected = 3, Eligible = 5, Limited = true });
        if (messages.Count != 1 || !messages[0].Contains("protected=3; eligible=5") || !messages[0].Contains("capacity limited=True"))
            throw new Exception("Status snapshot fields were not logged correctly.");
        diagnostics.Report(59f, () => throw new Exception("Throttled snapshot evaluated."));
        diagnostics.Report(60f, () => new WeatherGuardStatus { Protected = 8 });
        if (messages.Count != 2 || !messages[1].Contains("protected=8")) throw new Exception("Summary interval failed.");
        int formats = 0;
        for (int i = 0; i < 32; i++) diagnostics.Sample(0f, () => { formats++; return "acquisition sample"; });
        if (formats != WeatherGuardDiagnostics.SampleLimit || messages.Count != 2 + WeatherGuardDiagnostics.SampleLimit)
            throw new Exception("Sample cap failed or suppressed samples were formatted.");
        diagnostics.Sample(59f, () => throw new Exception("Sample budget reopened early."));
        diagnostics.Sample(60f, () => "Next sample window");
        if (!messages[^1].Contains("Next sample window")) throw new Exception("Sample budget did not recover.");
        int before = messages.Count;
        diagnostics.Reset();
        diagnostics.Report(0f, () => new WeatherGuardStatus());
        diagnostics.Sample(0f, () => "Reset sample window");
        if (messages.Count != before + 2) throw new Exception("Reset retained summary or sample limits.");
        System.Console.WriteLine("PASS: WeatherGuard diagnostics respect debug mode, format status snapshots, limit summaries and samples, defer suppressed formatting and reset.");
    }
}
