using System.Collections.Generic;
using System.Linq;
namespace HeimKit.Shared;
public sealed class HookCounters
{
    private readonly Dictionary<string, long> _counts = new();
    public void Hit(string key) => Add(key, 1);
    public void Add(string key, long amount) { _counts.TryGetValue(key, out var count); _counts[key] = count + amount; }
    public long Get(string key) => _counts.TryGetValue(key, out var count) ? count : 0;
    public string Format() => _counts.Count == 0 ? "none_observed" : string.Join(",", _counts.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));
}
