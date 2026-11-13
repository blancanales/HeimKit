using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Core.Players;
// Plugin owns this hub; features own only idempotent leases.
public sealed class PlayerServices : IDisposable
{
    private readonly DebugLoggingPolicy _debug;
    private readonly ManualLogSource _logger;
    private bool _debugReported;
    public IPlayerPresence Presence { get; }
    public PlayerPresenceEvents Events { get; }
    public PlayerActivityService Activity { get; }
    public PlayerRoutedMessages RoutedMessages { get; } = new();
    private readonly CombatActivity _combat;
    internal PlayerContextDiagnostics Context { get; }
    internal PlayerArrivalDiagnostics Arrivals { get; }
    public static bool Ready => PlayerActivityService.Ready;
    public PlayerServices(ConfigFile config, ManualLogSource logger)
    {
        Presence = new PlayerPresence(connections: new PlayerConnectionNotices(logger));
        Events = new(logger);
        var settings = new PlayerActivitySettings(config, logger);
        _debug = settings.DebugLogging; _logger = logger;
        Activity = new PlayerActivityService(settings.Options(), logger, Presence, debugPolicy: _debug, routedMessages: RoutedMessages);
        Context = new PlayerContextDiagnostics(Presence, RoutedMessages, _debug, logger);
        Arrivals = new PlayerArrivalDiagnostics(Presence, _debug, logger, Context.Weather);
        _combat = new CombatActivity(Presence, RoutedMessages, logger, Context);
    }
    public void Tick(float now)
    {
        if (!_debugReported) { _debugReported = true; _debug.ReportStartup(_logger, "PlayerActivity", Activity.Consumers > 0); }
        Presence.Refresh();
        DiagnosticLog.ObservePlayers(Presence.HasConnectedPlayers);
        Arrivals.Observe(now);
        _combat.Tick(now);
        Events.Observe(Presence.HasJoinedPlayers);
        Activity.Tick(now);
    }
    public void Dispose() { try { Arrivals.Dispose(); } finally { try { Context.Dispose(); } finally { try { _combat.Dispose(); } finally { try { Events.Dispose(); } finally { try { Activity.Dispose(); } finally { try { RoutedMessages.Dispose(); } finally { Presence.Clear(); } } } } } } }
}
