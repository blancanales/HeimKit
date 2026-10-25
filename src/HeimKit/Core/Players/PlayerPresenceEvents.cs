using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
namespace HeimKit.Core.Players;
// Occupied=true when a player has spawned in the world, false when none have joined, unavailable=null. Main-thread callbacks share the central census.
public sealed class PlayerPresenceEvents : IDisposable
{
    private readonly ManualLogSource _logger;
    private readonly PlayerPresenceDiagnostics _notices;
    private bool _emptyPauseFailed, _activationFailed;
    private readonly HashSet<Registration> _registrations = new();
    private Registration[] _snapshot = Array.Empty<Registration>();
    public bool? Occupied { get; private set; }
    public int Subscribers => _registrations.Count;
    public PlayerPresenceEvents(ManualLogSource logger) { _logger = logger; _notices = new(logger); }
    public IDisposable Subscribe(Action<bool?> changed, Action<Exception> failed, Func<bool>? emptyPaused = null, Func<bool>? active = null, Func<bool>? serverReady = null)
    {
        var registration = new Registration(this, changed, failed, emptyPaused, active, serverReady);
        _registrations.Add(registration); _snapshot = new Registration[_registrations.Count]; _registrations.CopyTo(_snapshot);
        Notify(registration); // Replay current state, including unavailable startup state.
        return registration;
    }
    public void Observe(bool? occupied)
    {
        // Observe transitions without claiming completed activation or cleanup.
        _notices.Observe(occupied, false, false);
        if (occupied != Occupied)
        {
            if (occupied == true) _emptyPauseFailed = false;
            if (occupied == false) _activationFailed = false;
            Occupied = occupied;
            foreach (var registration in _snapshot) Notify(registration);
        }
        ReportProcessing();
    }
    // Also called after module ticks so delayed native initialization can complete the join notice.
    public void ReportProcessing()
    {
        // Startup ordering: every enabled module's one-time Ready precedes presence status.
        bool paused = !_emptyPauseFailed, active = !_activationFailed;
        foreach (var registration in _registrations)
        {
            if (registration.ServerReady != null && !registration.ServerReady()) return;
            if (registration.EmptyPaused != null && !registration.EmptyPaused()) paused = false;
            if (registration.Active != null && !registration.Active()) active = false;
        }
        _notices.Observe(Occupied, paused, active);
    }
    private void Notify(Registration registration)
    {
        if (!_registrations.Contains(registration)) return;
        try { registration.Changed(Occupied); }
        catch (Exception error)
        {
            if (Occupied != true) _emptyPauseFailed = true;
            else _activationFailed = true;
            registration.Dispose();
            try { registration.Failed(error); }
            catch (Exception failure)
            {
                DiagnosticLog.Write(_logger, "PlayerPresence", CoreLogTopics.Lifecycle, "Consumer failure handling failed; other subscribers continue.", level: LogLevel.Error, error: failure);
            }
        }
    }
    public void Dispose()
    {
        foreach (var registration in _snapshot) registration.Dispose();
        Occupied = null;
    }
    private sealed class Registration : IDisposable
    {
        private PlayerPresenceEvents? _owner;
        public readonly Action<bool?> Changed;
        public readonly Action<Exception> Failed;
        public readonly Func<bool>? EmptyPaused, Active, ServerReady;
        public Registration(PlayerPresenceEvents owner, Action<bool?> changed, Action<Exception> failed, Func<bool>? emptyPaused = null, Func<bool>? active = null, Func<bool>? serverReady = null)
        { _owner = owner; Changed = changed; Failed = failed; EmptyPaused = emptyPaused; Active = active; ServerReady = serverReady; }
        public void Dispose()
        {
            var owner = _owner; _owner = null;
            if (owner == null) return;
            // A failed/inactive consumer must not disappear into a successful activation claim.
            if (owner.Occupied == true && Active != null && !Active()) owner._activationFailed = true;
            owner._registrations.Remove(this); owner._snapshot = new Registration[owner._registrations.Count]; owner._registrations.CopyTo(owner._snapshot);
        }
    }
}
