using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
namespace HeimKit.Shared.Players;
// Occupied=true, empty=false, unavailable=null. Main-thread callbacks share the central census.
public sealed class PlayerPresenceEvents : IDisposable
{
    private readonly ManualLogSource _logger;
    private readonly HashSet<Registration> _registrations = new();
    public bool? Occupied { get; private set; }
    public int Subscribers => _registrations.Count;
    public PlayerPresenceEvents(ManualLogSource logger) => _logger = logger;
    public IDisposable Subscribe(Action<bool?> changed, Action<Exception> failed)
    {
        var registration = new Registration(this, changed, failed);
        _registrations.Add(registration);
        Notify(registration); // Replay current state, including unavailable startup state.
        return registration;
    }
    public void Observe(bool? occupied)
    {
        if (occupied == Occupied) return;
        Occupied = occupied;
        foreach (var registration in _registrations.ToArray()) Notify(registration);
    }
    private void Notify(Registration registration)
    {
        if (!_registrations.Contains(registration)) return;
        try { registration.Changed(Occupied); }
        catch (Exception error)
        {
            registration.Dispose();
            try { registration.Failed(error); }
            catch (Exception failure)
            {
                DiagnosticLog.Write(_logger, "PlayerPresence", DiagnosticEvent.Lifecycle,
                    "Consumer failure handling failed; other subscribers continue: " + failure, level: LogLevel.Error);
            }
        }
    }
    public void Dispose()
    {
        foreach (var registration in _registrations.ToArray()) registration.Dispose();
        Occupied = null;
    }
    private sealed class Registration : IDisposable
    {
        private PlayerPresenceEvents? _owner;
        public readonly Action<bool?> Changed;
        public readonly Action<Exception> Failed;
        public Registration(PlayerPresenceEvents owner, Action<bool?> changed, Action<Exception> failed)
        { _owner = owner; Changed = changed; Failed = failed; }
        public void Dispose() { var owner = _owner; _owner = null; owner?._registrations.Remove(this); }
    }
}
