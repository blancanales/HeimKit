using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Shared.Players;
// Plugin owns this hub; features own only idempotent leases.
public sealed class PlayerServices : IDisposable
{
    public IPlayerPresence Presence { get; }
    public PlayerPresenceEvents Events { get; }
    public PlayerActivityService Activity { get; }
    public static bool Ready => PlayerActivityService.Ready;
    public PlayerServices(ConfigFile config, ManualLogSource logger)
    {
        Presence = new PlayerPresence();
        Events = new(logger);
        Activity = new PlayerActivityService(new PlayerActivitySettings(config).Options(), logger, Presence);
    }
    public void Tick(float now)
    {
        Presence.Refresh();
        DiagnosticLog.ObservePlayers(Presence.HasConnectedPlayers);
        Events.Observe(Presence.HasJoinedPlayers);
        Activity.Tick(now);
    }
    public void Dispose() { try { Events.Dispose(); } finally { try { Activity.Dispose(); } finally { Presence.Clear(); } } }
}
