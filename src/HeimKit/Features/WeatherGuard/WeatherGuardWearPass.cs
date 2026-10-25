using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;

namespace HeimKit.Features.WeatherGuard;

// Count distinct pieces only after their native wear invocation completed successfully.
public sealed class WeatherGuardWearPass
{
    private readonly HashSet<ZDOID> _pieces = new();
    private ZDOID _firstId;
    private string? _firstPrefab;
    public void Record(ZDOID id, bool contributionSuppressed, bool nativeWearSucceeded, string? prefab = null)
    {
        if (contributionSuppressed && nativeWearSucceeded && _pieces.Add(id) && _pieces.Count == 1)
        { _firstId = id; _firstPrefab = prefab; }
    }
    public int CompleteFrame(bool succeeded, bool sweepFinished, Action<ZDOID, string?> confirmed)
    {
        if (succeeded && !sweepFinished) return 0;
        int count = succeeded ? _pieces.Count : 0;
        if (succeeded) foreach (var id in _pieces) confirmed(id, id == _firstId ? _firstPrefab : null);
        _pieces.Clear(); _firstId = ZDOID.None; _firstPrefab = null;
        return count;
    }
    public int Complete(bool succeeded) => Complete(succeeded, out _, out _);
    public int CompleteFrame(bool succeeded, bool sweepFinished, out string? singleId, out string? singlePrefab)
    {
        if (!succeeded || sweepFinished) return Complete(succeeded, out singleId, out singlePrefab);
        singleId = singlePrefab = null;
        return 0;
    }
    public int Complete(bool succeeded, out string? singleId, out string? singlePrefab)
    {
        int count = succeeded ? _pieces.Count : 0;
        singleId = count == 1 ? _firstId.ToString() : null;
        singlePrefab = count == 1 ? _firstPrefab : null;
        _pieces.Clear();
        _firstId = ZDOID.None; _firstPrefab = null;
        return count;
    }
}
