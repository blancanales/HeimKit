using System.Collections.Generic;
using System.Linq;

namespace HeimKit.Shared;

public sealed class DiagnosticBudget
{
    private readonly Dictionary<string, (float Start, int Count)> _windows = new();
    private readonly Dictionary<string, long> _omitted = new();
    public bool Reserve(float now, string stream, int limit, bool countOmitted = true)
    {
        if (!_windows.TryGetValue(stream, out var window) || now < window.Start || now - window.Start >= 60)
            window = (now, 0);
        if (window.Count >= limit)
        {
            if (countOmitted) { _omitted.TryGetValue(stream, out long count); _omitted[stream] = count + 1; }
            return false;
        }
        _windows[stream] = (window.Start, window.Count + 1);
        return true;
    }
    public string TakeOmitted()
    {
        string counts = string.Join(",", _omitted.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
        _omitted.Clear();
        return counts.Length == 0 ? "none" : counts;
    }
    public void Reset() { _windows.Clear(); _omitted.Clear(); }
}
