using BepInEx.Logging;
namespace HeimKit.Shared.Players;
// One shared occupancy notice, independent of AFK consumers and debug policy.
public sealed class PlayerPresenceDiagnostics
{
    private readonly ManualLogSource _logger;
    private bool? _lastKnownOccupied;
    public PlayerPresenceDiagnostics(ManualLogSource logger) => _logger = logger;
    public void Observe(bool? occupied)
    {
        // Unavailable population is not an empty server or a new connection.
        if (!occupied.HasValue || occupied == _lastKnownOccupied) return;
        _lastKnownOccupied = occupied;
        DiagnosticLog.Write(_logger, "PlayerPresence", DiagnosticEvent.Lifecycle,
            occupied.Value ? "Server has connected players." : "Server is empty.");
    }
}
