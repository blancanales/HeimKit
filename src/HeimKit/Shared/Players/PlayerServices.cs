using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Shared.Players;
// Plugin owns this hub; features own only idempotent leases.
public sealed class PlayerServices : IDisposable
{
    private readonly DebugLoggingPolicy _debug;
    private readonly ManualLogSource _logger;
    private bool _debugReported;
    public IPlayerPresence Presence { get; }
    public PlayerPresenceEvents Events { get; }
    public PlayerActivityService Activity { get; }
    public static bool Ready => PlayerActivityService.Ready;
    public PlayerServices(ConfigFile config, ManualLogSource logger)
    {
        Presence = new PlayerPresence();
        Events = new(logger);
        var settings = new PlayerActivitySettings(config, logger);
        _debug = settings.DebugLogging; _logger = logger;
        Activity = new PlayerActivityService(settings.Options(), logger, Presence, debugPolicy: _debug);
    }
    public void Tick(float now)
    {
        if (!_debugReported) { _debugReported = true; _debug.ReportStartup(_logger, "PlayerActivity", Activity.Consumers > 0); }
        Presence.Refresh();
        DiagnosticLog.ObservePlayers(Presence.HasConnectedPlayers);
        Events.Observe(Presence.HasJoinedPlayers);
        Activity.Tick(now);
    }
    public void Dispose() { try { Events.Dispose(); } finally { try { Activity.Dispose(); } finally { Presence.Clear(); } } }
}
