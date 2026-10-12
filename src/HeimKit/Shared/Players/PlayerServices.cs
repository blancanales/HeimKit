using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Shared.Players;
// Plugin owns this hub; features own only idempotent leases.
public sealed class PlayerServices : IDisposable
{
    private readonly PlayerPresenceDiagnostics _presenceDiagnostics;
    public IPlayerPresence Presence { get; }
    public PlayerPresenceEvents Events { get; }
    public PlayerActivityService Activity { get; }
    public static bool Ready => PlayerActivityService.Ready;
    public PlayerServices(ConfigFile config, ManualLogSource logger)
    {
        Presence = new PlayerPresence();
        _presenceDiagnostics = new(logger);
        Events = new(logger);
        Activity = new PlayerActivityService(new PlayerActivitySettings(config).Options(), logger, Presence);
    }
    public void Tick(float now)
    {
        Presence.Refresh();
        _presenceDiagnostics.Observe(Presence.HasConnectedPlayers);
        DiagnosticLog.ObservePlayers(Presence.HasConnectedPlayers);
        Events.Observe(Presence.HasConnectedPlayers);
        Activity.Tick(now);
    }
    public void Dispose() { try { Events.Dispose(); } finally { try { Activity.Dispose(); } finally { Presence.Clear(); } } }
}
