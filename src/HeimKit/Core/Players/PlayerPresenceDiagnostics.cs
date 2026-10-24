using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
namespace HeimKit.Core.Players;
// One shared occupancy notice, independent of AFK consumers and debug policy.
public sealed class PlayerPresenceDiagnostics
{
    private readonly ManualLogSource _logger;
    private bool? _lastKnownOccupied;
    private bool _emptyAnnounced, _activeAnnounced;
    public PlayerPresenceDiagnostics(ManualLogSource logger) => _logger = logger;
    public void Observe(bool? occupied, bool allEnabledPaused, bool allEnabledActive = true)
    {
        // Track occupancy even when incomplete cleanup prevents an empty success notice.
        if (!occupied.HasValue) return;
        bool changed = occupied != _lastKnownOccupied;
        _lastKnownOccupied = occupied;
        if (occupied.Value)
        {
            if (changed) _activeAnnounced = false;
            if (!allEnabledActive || _activeAnnounced) return;
            _activeAnnounced = true;
            DiagnosticLog.Write(_logger, "PlayerPresence", "Players have joined the world; all enabled features are active.");
            return;
        }
        if (changed) _emptyAnnounced = false;
        if (!allEnabledPaused || _emptyAnnounced) return;
        _emptyAnnounced = true;
        DiagnosticLog.Write(_logger, "PlayerPresence", "No players have joined the world; all features are inactive until a player joins.");
    }
}
