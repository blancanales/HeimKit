using BepInEx.Logging;
using HeimKit.Features.WeatherGuard;
internal static class WeatherGuardStandardNoticesTests
{
    public static void Run()
    {
        using var log = new ManualLogSource("Standard notice checks");
        var messages = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, args) => messages.Add((args.Level, args.Data.ToString()!));
        var weather = new WeatherGuardDiagnostics(log, false);
        weather.CapacityState(false); weather.CapacityState(true);
        for (int i = 0; i < 32; i++) weather.CapacityState(true);
        weather.CapacityState(false); weather.CapacityState(false);
        if (messages.Count != 2 || messages[0].Level != LogLevel.Warning || messages[1].Level != LogLevel.Info || messages[1].Text != "WeatherGuard: Processing capacity limit cleared.")
            throw new Exception("Capacity warning/recovery repeated or required debug.");
        weather.Reset(); weather.CapacityState(false);
        if (messages.Count != 2) throw new Exception("Reset fabricated capacity recovery.");
        messages.Clear();
        weather.WeatherDamageSuppressed(0); weather.WeatherDamageSuppressed(61); weather.WeatherDamageSuppressed(0); weather.WeatherDamageSuppressed(1, "1:341004", "wood_stair");
        if (messages.Count != 2 || messages.Any(m => m.Level != LogLevel.Info) ||
            messages[0].Text != "WeatherGuard: Prevented weather damage to 61 pieces." ||
            messages[1].Text != "WeatherGuard: Prevented weather damage to wood_stair (piece 1:341004).")
            throw new Exception("Grouped damage prevention depends on Debug, reports empty passes or has incorrect wording.");
        weather.WeatherDamageSuppressed(1, "1:341005");
        if (messages[^1].Text != "WeatherGuard: Prevented weather damage to piece 1:341005.")
            throw new Exception("Missing prefab data lost the known individual piece identity.");
        weather.WeatherDamageSuppressed(2, "1:341004", "wood_stair");
        if (messages[^1].Text != "WeatherGuard: Prevented weather damage to 2 pieces.")
            throw new Exception("Grouped prevention incorrectly identifies one piece as the whole batch.");
        System.Console.WriteLine("PASS: grouped WeatherGuard damage prevention, quiet unchanged passes and capacity warning/recovery independent of debug settings.");
    }
}
