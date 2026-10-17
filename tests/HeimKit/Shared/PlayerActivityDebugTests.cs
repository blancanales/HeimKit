using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
using HeimKit.Shared.Players;
using HeimKit.Shared.Players.Models;

internal static class PlayerActivityDebugTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        using var log = new ManualLogSource("PlayerActivity debug policy");
        var records = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, e) => records.Add((e.Level, e.Data.ToString()!));
        string root = Path.Combine(AppContext.BaseDirectory, "player-activity-debug-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool global in new[] { false, true })
            foreach (bool own in new[] { false, true })
            foreach (bool consumer in new[] { false, true })
            foreach (bool suppressed in new[] { false, true })
            {
                DiagnosticLog.StartSession(); records.Clear();
                string path = Path.Combine(root, Guid.NewGuid() + ".cfg");
                File.WriteAllText(path, "[PlayerActivity]\nDisableDebugLogging = " + suppressed.ToString().ToLowerInvariant() + "\n");
                var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
                var settings = new PlayerActivitySettings(config, log);
                Check(!settings.DebugMode.Value, "PlayerActivity DebugMode must default false.");
                settings.DebugMode.Value = own;
                DiagnosticsSettings.Bind(config).Value = global;
                using var service = new PlayerActivityService(new(2, 0, .5f, 10, 1), log, new Presence(), () => true,
                    () => new() { new PlayerActivityObservation { Peer = 1, Character = "same", Known = true, Alive = true, Health = 100 } },
                    _ => { }, settings.DebugLogging);
                service.Tick(0);
                Check(records.Count == 0 && service.Consumers == 0, "Debug settings must not create an inference consumer.");
                using var lease = service.Acquire(() => consumer);
                service.Tick(0);
                Check(records.Count(r => r.Level == LogLevel.Debug) == ((!suppressed && (global || own || consumer)) ? 2 : 0),
                    "Shared DebugMode/global/consumer inheritance or hidden suppression failed.");
                service.Tick(1); service.Tick(2);
                Check(service.Snapshot(2).Single().Idle && records.Count(r => r.Level == LogLevel.Info && r.Text.Contains("became AFK")) == 1,
                    "Debug policy changed inference or suppressed ordinary Info notices.");
                config.Save();
                Check(File.ReadAllText(path).Contains("DisableDebugLogging = " + suppressed.ToString().ToLowerInvariant()), "Manual override was not preserved.");
            }
        }
        finally { DiagnosticLog.StartSession(); Directory.Delete(root, true); }
        System.Console.WriteLine("PASS: PlayerActivity DebugMode defaults, global/consumer inheritance, hidden suppression, lease gating and independent Info AFK notices.");
    }

    private sealed class Presence : IPlayerPresence
    {
        public int? JoinedCount => 1;
        public int? ConnectedCount => 1;
        public int? ReadyCount => 1;
        public bool? HasJoinedPlayers => true;
        public bool? HasConnectedPlayers => true;
        public IReadOnlyList<ZNetPeer> Peers => Array.Empty<ZNetPeer>();
        public void Refresh() { }
        public void Clear() { }
    }
}
