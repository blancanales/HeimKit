using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
namespace HeimKit.Features.AFKRaidWatch;

// All candidate exclusions inside one native automatic update share a normal-log entry.
public sealed class AFKRaidWatchSelectionNotices
{
    private readonly ManualLogSource _logger;
    private bool _collecting;
    private long _excluded;
    public AFKRaidWatchSelectionNotices(ManualLogSource logger) => _logger = logger;
    public void Begin() { _collecting = true; _excluded = 0; }
    public void Excluded() { if (_collecting) _excluded++; }
    public void End(bool failed)
    {
        long count = _excluded; bool collected = _collecting; Reset();
        if (!collected || failed || count == 0) return;
        DiagnosticLog.Write(_logger, "AFKRaidWatch", $"Excluded {count} automatic raid {(count == 1 ? "candidate" : "candidates")} at AFK-only locations.");
    }
    public void Reset() { _collecting = false; _excluded = 0; }
}
