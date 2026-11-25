using System;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Scheduling;

namespace HeimKit.Core.Lifecycle;

// Each store owns its own baseline. Its normal deadline and synchronous final flush take priority.
internal sealed class SnapshotPreparation
{
    private readonly AdaptiveOperation _work;
    internal SnapshotPreparation(ManualLogSource log, DebugLoggingPolicy policy, string store)
    {
        _work = new(log, policy, "SharedMap", store);
    }

    internal bool Ready(ScheduleToken deadline, float now, float interval, bool dirty)
    {
        if (interval <= 0 || deadline.Ready(now)) return true;
        if (!dirty || !deadline.Ready(now + Math.Min(1, interval / 4))) return false;
        var scheduler = ModuleScheduler.Current;
        if (scheduler.Battle.Active || !scheduler.EarlySnapshotAvailable || !_work.CanStartEarly(now)) return false;
        scheduler.ReserveEarlySnapshot();
        return true;
    }

    internal static void Queue(SnapshotPreparation? preparation, float now, SnapshotFile file,
        Func<byte[]> capture, long generation)
    {
        using var work = preparation?._work.Begin(now);
        byte[] payload = capture();
        work?.Count(bytes: payload.Length);
        file.Queue(payload, generation);
    }
}
