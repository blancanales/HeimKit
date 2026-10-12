using System.Collections.Generic;

namespace HeimKit.Features.WeatherGuard;

// Count distinct pieces only after their native wear invocation completed successfully.
public sealed class WeatherGuardWearPass
{
    private readonly HashSet<string> _pieces = new();
    private string? _firstId, _firstPrefab;
    public void Record(string id, bool contributionSuppressed, bool nativeWearSucceeded, string? prefab = null)
    {
        if (contributionSuppressed && nativeWearSucceeded && _pieces.Add(id) && _pieces.Count == 1)
        { _firstId = id; _firstPrefab = prefab; }
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
        singleId = count == 1 ? _firstId : null;
        singlePrefab = count == 1 ? _firstPrefab : null;
        _pieces.Clear();
        _firstId = _firstPrefab = null;
        return count;
    }
}
