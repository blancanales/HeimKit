using System;
using System.Collections.Generic;
using HeimKit.Core.Diagnostics;

namespace HeimKit.Core.World;

// Shared dependency readiness, built through the existing candidate index.
// Only membership and native binding transitions invalidate a result; ordinary
// health/support/ownership writes that preserve membership do not repeat discovery.
internal sealed class SectorReadiness : IDisposable
{
    private readonly CandidateSubscription _selection;
    private readonly Func<ZDO, bool> _loaded;
    private readonly Func<long> _bindings;
    private readonly Func<float> _clock;
    private float _fallbackExpires;
    private readonly List<ZDO> _records = new();
    private readonly Dictionary<Vector2s, bool> _ready = new();
    private long _membership = -1, _binding = -1;
    internal long Checks { get; private set; }
    internal long Reuses { get; private set; }
    internal SectorReadiness(WorldObjectProvider provider, string owner, Func<ZDO, bool> required,
        Func<ZDO, bool> loaded, Func<long>? bindings = null, Func<float>? clock = null)
    {
        _selection = provider.Subscribe(owner, required, ScanKind.Nearby);
        _loaded = loaded; _bindings = bindings ?? (() => SceneInstances.BindingRevision);
        _clock = clock ?? (() => UnityEngine.Time.unscaledTime);
    }
    internal bool Ready(Vector2s sector)
    {
        _selection.Refresh();
        long binding = _bindings();
        bool indexed = _selection.Indexed;
        if (_membership != _selection.Revision || _binding != binding) Reset();
        if (!indexed && _ready.Count > 0 && _clock() >= _fallbackExpires) _ready.Clear();
        if (binding >= 0 && _ready.TryGetValue(sector, out bool cached))
        { Reuses++; return cached; }
        using var timing = PerformanceStages.Measure(_selection.Owner, "context_readiness");
        Checks++;
        _records.Clear();
        _selection.Query(sector, new SimulationDistance(1, 0), _records);
        bool ready = true;
        foreach (var record in _records)
            if (!record.IsValid() || !_loaded(record)) { ready = false; break; }
        // Seeding can discover members in other cells. Do not stamp results
        // computed before that discovery as if they used the new membership.
        binding = _bindings();
        if (_membership != _selection.Revision || _binding != binding) Reset();
        _membership = _selection.Revision; _binding = binding;
        if (binding >= 0)
        {
            if (_ready.Count >= 1024) _ready.Clear();
            if (!_selection.Indexed && _ready.Count == 0) _fallbackExpires = _clock() + 1f;
            _ready[sector] = ready;
        }
        return ready;
    }
    internal void Reset() { _ready.Clear(); _membership = _binding = -1; }
    public void Dispose() { _selection.Dispose(); _records.Clear(); Reset(); }
}
