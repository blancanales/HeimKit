using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Configuration;

internal static class InfiniteTorchesDiagnosticsTests
{
    public static void Run()
    {
        var statusConfig = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false)
        {
            SaveOnConfigSet = false
        };
        using var statusLogger = new ManualLogSource("Diagnostic checks");
        var statusMessages = new List<string>();
        statusLogger.LogEvent += (_, args) =>
        {
            if (args.Level != LogLevel.Debug) throw new Exception("Optional torch diagnostics must use Debug severity.");
            statusMessages.Add(LogCapture.Text(args.Data));
        };
        var status = new InfiniteTorchesDiagnostics(statusConfig, statusLogger);
        status.Report(0f, () => throw new Exception("Debug mode off must suppress diagnostic formatting."));
        status.LogRefillRequest(0f, () => throw new Exception("Default debug-off refill details must not be formatted."));
        if (statusMessages.Count != 0 || status.IsDebugEnabled)
            throw new Exception("Debug mode off did not suppress diagnostics.");
        statusConfig.Bind("Diagnostics", "DebugMode", false).Value = true;
        status.Report(0f, () => "Waiting for world services.");
        status.Report(5f, () => throw new Exception("Throttled message should not be formatted."));
        status.Report(59f, () => "No ready connected players.");
        if (statusMessages.Count != 1) throw new Exception("Status messages flooded during the configured interval.");
        status.Report(60f, () => "All examined torches have enough fuel.");
        if (statusMessages.Count != 2 || !statusMessages[1].Contains("enough fuel"))
            throw new Exception("Status did not report the latest reason at the next interval.");
        statusConfig.Bind("Diagnostics", "DebugMode", false).Value = false;
        status.Report(120f, () => throw new Exception("Disabled diagnostics should not format a message."));
        if (statusMessages.Count != 2) throw new Exception("Disabled status diagnostics emitted a message.");
        statusConfig.Bind("Diagnostics", "DebugMode", false).Value = true;
        status.Reset();
        status.Report(0f, () => "Restarted status reporting.");
        if (statusMessages.Count != 3) throw new Exception("Diagnostic reset retained the old timer.");
        System.Console.WriteLine("PASS: status diagnostics throttle changed reasons, defer formatting, respect their off switch and reset on shutdown.");
        status.Reset();
        int beforeDetails = statusMessages.Count;
        int detailFormats = 0;
        for (int request = 0; request < 32; request++)
            status.LogRefillRequest(0f, () => { detailFormats++; return "object=test; client application unconfirmed."; });
        if (statusMessages.Count != beforeDetails + InfiniteTorchesDiagnostics.RefillDetailLimit || detailFormats != InfiniteTorchesDiagnostics.RefillDetailLimit)
            throw new Exception("A busy refill pass exceeded the detail cap or formatted suppressed logs.");
        status.LogRefillRequest(59f, () => throw new Exception("Detail budget reopened too early."));
        status.LogRefillRequest(60f, () => "Next window.");
        if (statusMessages.Count != beforeDetails + InfiniteTorchesDiagnostics.RefillDetailLimit + 1)
            throw new Exception("Detail budget did not recover at 60 seconds.");
        status.Reset();
        status.LogRefillRequest(0f, () => "Reset window.");
        System.Console.WriteLine("PASS: actual refill-request logs are capped at 4 per minute, defer suppressed formatting, recover after 60 seconds and reset.");
        statusConfig.Bind("Diagnostics", "DebugMode", false).Value = false;
        int beforeDisabled = statusMessages.Count;
        status.LogRefillRequest(1000f, () => throw new Exception("Debug-off refill log must not be formatted."));
        status.Report(1000f, () => throw new Exception("Disabled debug mode must suppress diagnostic output."));
        if (status.IsDebugEnabled || statusMessages.Count != beforeDisabled)
            throw new Exception("Master debug switch failed to suppress enabled diagnostic categories.");
        System.Console.WriteLine("PASS: master DebugMode defaults off and suppresses all optional diagnostics through one setting.");

        statusConfig.Bind("Diagnostics", "DebugMode", false).Value = true;
        status.Reset();
        int outcomeStart = statusMessages.Count;
        for (int i = 0; i < 100; i++) status.Result(0, new HeimKit.Features.InfiniteTorches.Models.RefillResult
            { Object = "1:" + i, Outcome = "fuel_increase", Before = 0, Fuel = 10, Target = 10 });
        if (statusMessages.Count != outcomeStart + 6 || !statusMessages[^1].Contains(".refill:") ||
            !statusMessages[^1].Contains("delivery acknowledgement unavailable"))
            throw new Exception("Refill outcomes are unbounded or overstate delivery evidence.");
        status.Report(0, () => "Pending refill outcomes collected");
        if (!statusMessages[^1].Contains("omitted_messages=refill_results:94")) throw new Exception("Omitted outcome count missing.");
        statusConfig.Bind("Diagnostics", "DebugMode", false).Value = false;
        int offCount = statusMessages.Count;
        status.Result(60, new HeimKit.Features.InfiniteTorches.Models.RefillResult { Outcome = "timeout" });
        if (statusMessages.Count != offCount) throw new Exception("Disabled outcome diagnostics emitted a message.");

    }
}
