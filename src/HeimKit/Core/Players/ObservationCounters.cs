using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players.Models;
using System.Collections.Generic;
using System.Linq;
namespace HeimKit.Core.Players;
public sealed class ObservationCounters
{
    private readonly Dictionary<string, long> _counts = new();
    public void Hit(string key) => Add(key, 1);
    public void Add(string key, long amount) { _counts.TryGetValue(key, out var count); _counts[key] = count + amount; }
    public long Get(string key) => _counts.TryGetValue(key, out var count) ? count : 0;
    public string Format() => _counts.Count == 0 ? "none_observed" : string.Join(",", _counts.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));
}
