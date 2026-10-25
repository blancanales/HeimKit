using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Features.WeatherGuard;

internal static class WeatherGuardStandardNoticesTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var clock = DiagnosticLog.WallClock;
        var now = new DateTime(2026, 10, 24, 21, 0, 0);
        DiagnosticLog.WallClock = () => now;
        try
        {
            using var log = new ManualLogSource("Standard notice checks"); var messages = new List<(LogLevel Level, string Text)>();
            log.LogEvent += (_, args) => messages.Add((args.Level, LogCapture.Text(args.Data)));
            var weather = new WeatherGuardDiagnostics(log, false);
            weather.CapacityState(false); weather.CapacityState(true);
            for (int i = 0; i < 32; i++) weather.CapacityState(true);
            weather.CapacityState(false); weather.CapacityState(false);
            Check(messages.Count == 2 && messages[0].Level == LogLevel.Warning && messages[1] == (LogLevel.Info, "WeatherGuard: Processing capacity limit cleared."), "Capacity warning/recovery changed.");
            weather.Reset(); messages.Clear();
            var first = new ZDOID(1, 341004);
            weather.WeatherDamageSuppressed(first, "wood_stair"); weather.WeatherDamageSuppressed(first, "wood_stair");
            now = now.AddMinutes(9); weather.PollPreventionRollup(); Check(messages.Count == 0, "Roll-up emitted before ten minutes.");
            now = now.AddMinutes(1); weather.PollPreventionRollup();
            Check(messages.Count == 1 && messages[0] == (LogLevel.Info, "WeatherGuard: Prevented weather damage to wood_stair (piece 1:341004) in the last 10 minutes."), "Single-piece roll-up lost identity or distinctness.");
            weather.PollPreventionRollup(); Check(messages.Count == 1, "Roll-up repeated within one window.");
            weather.WeatherDamageSuppressed(first, "wood_stair"); weather.WeatherDamageSuppressed(new ZDOID(1, 341005));
            now = now.AddMinutes(3); weather.Dispose(); weather.Dispose();
            Check(messages.Count == 2 && messages[1].Text == "WeatherGuard: Prevented weather damage to 2 pieces since 21:10.", "Disposal did not flush a pending window exactly once.");
            var next = new WeatherGuardDiagnostics(log, false); next.Dispose(); Check(messages.Count == 2, "A new controller inherited the old count.");
            var bounded = new WeatherGuardDiagnostics(log, false);
            for (uint id = 1; id <= 8193; id++) bounded.WeatherDamageSuppressed(new ZDOID(2, id));
            bounded.WeatherDamageSuppressed(new ZDOID(2, 1));
            bounded.WeatherDamageSuppressed(new ZDOID(2, 8193));
            var set = (HashSet<ZDOID>)typeof(WeatherGuardDiagnostics).GetField("_prevented", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bounded)!;
            Check(set.Count == 8192, "Distinct-piece cache exceeded its bound.");
            bounded.Dispose();
            Check(messages[^1].Text.Contains("8194 pieces since"), "Bounded roll-up dropped overflow confirmations or counted a tracked duplicate.");
            System.Console.WriteLine("PASS: WeatherGuard ten-minute distinct-piece windows, single naming, bounded overflow, disposal flush, no carry-over and unchanged capacity notices.");
        }
        finally { DiagnosticLog.WallClock = clock; }
    }
}
