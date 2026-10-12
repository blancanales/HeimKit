using System.Globalization;
using BepInEx.Logging;
using HeimKit.Shared;

internal static class DiagnosticLogTests
{
    public static void Run()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string line = DiagnosticLog.Format("WeatherGuard", DiagnosticEvent.Result, "Observed | not assumed\nnext", new (string, object?)[]
                { ("health", 237.9893f), ("managed", true), ("wet_s", null), ("path", @"C:\logs"), ("owner", long.MaxValue) }, 12.5);
            if (!line.StartsWith("WeatherGuard: RESULT | Observed \\| not assumed\\nnext | elapsed_s=12.5") ||
                !line.Contains("health=237.9893") || !line.Contains("managed=true") || !line.Contains("wet_s=unknown") ||
                !line.Contains("owner=9223372036854775807") || line.Contains('\n') || !line.Contains(@"path=C:\\logs"))
                throw new Exception("Log schema lost precision, locale independence, escaping or unknown-state semantics.");
            try { DiagnosticLog.Format("Feature", DiagnosticEvent.Detail, "Invalid", new[] { ("bad|key", (object?)1) }); throw new Exception("Invalid field key accepted."); }
            catch (ArgumentException) { }
        }
        finally { CultureInfo.CurrentCulture = previous; }
        using var logger = new ManualLogSource("Shared log checks");
        var levels = new List<LogLevel>(); logger.LogEvent += (_, args) => levels.Add(args.Level);
        DiagnosticLog.Write(logger, "Feature", DiagnosticEvent.Lifecycle, "Ready");
        DiagnosticLog.Write(logger, "Feature", DiagnosticEvent.Lifecycle, "Limited", level: LogLevel.Warning);
        DiagnosticLog.Write(logger, "Feature", DiagnosticEvent.Lifecycle, "Failed", level: LogLevel.Error);
        if (!levels.SequenceEqual(new[] { LogLevel.Info, LogLevel.Warning, LogLevel.Error })) throw new Exception("Severity was altered by the schema.");
        var messages = new List<string>(); logger.LogEvent += (_, args) => messages.Add(args.Data.ToString()!);
        DiagnosticLog.Write(logger, "Feature", DiagnosticEvent.Lifecycle, "Ready.", new[] { ("metric", (object?)1) });
        DiagnosticLog.Write(logger, "Feature", DiagnosticEvent.Lifecycle, "Could not read C:\\config.\nRetry later.", level: LogLevel.Warning);
        DiagnosticLog.Write(logger, "Feature", DiagnosticEvent.Detail, "Evidence", new[] { ("metric", (object?)1) }, LogLevel.Debug);
        DiagnosticLog.Write(logger, "Feature", DiagnosticEvent.Lifecycle, "Operation failed", level: LogLevel.Error);
        if (messages[0] != "Feature: Ready." || messages[1] != "Feature: Could not read C:\\config. Retry later." ||
            !messages[2].StartsWith("Feature: DETAIL | Evidence | elapsed_s=") || !messages[2].Contains("metric=1") ||
            !messages[3].StartsWith("Feature: LIFECYCLE | Operation failed | elapsed_s="))
            throw new Exception("Info/Warning must be concise; Debug/Error must retain structured evidence.");
        var budget = new DiagnosticBudget();
        for (int i = 0; i < 6; i++) if (!budget.Reserve(0, "ACTION", 6)) throw new Exception("Sample budget missing.");
        for (int i = 0; i < 100; i++) if (budget.Reserve(59, "ACTION", 6)) throw new Exception("Busy stream exceeded its budget.");
        if (!budget.Reserve(59, "RESULT", 6) || budget.TakeOmitted() != "ACTION:100" || budget.TakeOmitted() != "none" ||
            !budget.Reserve(60, "ACTION", 6)) throw new Exception("Independent budgets, omitted counts or recovery failed.");
        budget.Reset();
        if (!budget.Reserve(0, "ACTION", 6)) throw new Exception("Budget reset failed.");
        System.Console.WriteLine("PASS: shared log schema preserves severity, exact numeric values, escaping, unknown values and bounded independent streams.");
    }
}
