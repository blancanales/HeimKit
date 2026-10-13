using System;
using BepInEx.Logging;
namespace HeimKit.Shared.Players;

// Server readiness is announced once; player-dependent transitions are optional Debug evidence.
public sealed class PlayerProcessingNotices
{
    private readonly ManualLogSource _logger;
    private readonly string _name;
    private readonly Func<bool> _debug;
    private bool _running;
    private string? _pauseReason;
    public PlayerProcessingNotices(ManualLogSource logger, string name, Func<bool>? debug = null)
    { _logger = logger; _name = name; _debug = debug ?? (() => false); }
    public bool IsServerReady { get; private set; }
    public bool IsRunning => _running;
    public bool IsEmptyPaused { get; private set; }
    public void ServerReady()
    {
        if (IsServerReady) return;
        IsServerReady = true;
        DiagnosticLog.Write(_logger, _name, DiagnosticEvent.Lifecycle, "Ready.");
    }
    public void EmptyPaused()
    {
        bool changed = !IsEmptyPaused;
        _running = false; _pauseReason = "no players have joined the world"; IsEmptyPaused = true;
        if (changed) Debug("Processing inactive: no players have joined the world.");
    }
    public void Ready()
    {
        if (_running) return;
        _running = true; _pauseReason = null; IsEmptyPaused = false;
        Debug("Processing active.");
    }
    public void Paused(string reason)
    {
        if (!_running && _pauseReason == reason) return;
        _running = false; _pauseReason = reason; IsEmptyPaused = false;
        Debug("Processing inactive: " + reason + ".");
    }
    public void Unavailable(string reason) => Paused(reason);
    private void Debug(string message)
    {
        if (_debug()) DiagnosticLog.Write(_logger, _name, DiagnosticEvent.Lifecycle, message, level: LogLevel.Debug);
    }
    public void Reset() { IsServerReady = false; _running = false; _pauseReason = null; IsEmptyPaused = false; }
}
