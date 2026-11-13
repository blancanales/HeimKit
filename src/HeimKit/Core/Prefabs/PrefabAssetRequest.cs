using System;
using HeimKit.Core.Diagnostics;
using SoftReferenceableAssets;
using UnityEngine;

namespace HeimKit.Core.Prefabs;

// One native async reference per consumer, always released on replacement/disposal.
// Poll and metadata inspection stay on the main thread; never synchronously wait.
internal sealed class PrefabAssetRequest : IDisposable
{
    private SoftReference<GameObject> _reference;
    private bool _held;
    private float _started;
    private readonly Func<SoftReference<GameObject>, bool>? _valid, _loaded, _loading;
    private readonly Action<SoftReference<GameObject>>? _start, _release;
    private readonly Func<SoftReference<GameObject>, GameObject?>? _read;
    internal PrefabAssetRequest() { }
    internal PrefabAssetRequest(Func<SoftReference<GameObject>, bool> valid, Action<SoftReference<GameObject>> start,
        Func<SoftReference<GameObject>, bool> loaded, Func<SoftReference<GameObject>, bool> loading,
        Func<SoftReference<GameObject>, GameObject?> read, Action<SoftReference<GameObject>> release)
    { _valid = valid; _start = start; _loaded = loaded; _loading = loading; _read = read; _release = release; }
    internal bool Pending => _held;
    internal string LastOutcome { get; private set; } = "none";
    internal float LastWaitSeconds { get; private set; }
    internal bool TryGet(SoftReference<GameObject> reference, float now, out GameObject? asset, string? subject = null)
    {
        asset = null;
        if (_held && !_reference.Equals(reference)) Dispose();
        if (!_held)
        {
            LastWaitSeconds = 0;
            if (!(_valid == null ? reference.IsValid : _valid(reference))) { LastOutcome = "unavailable"; return true; }
            _reference = reference; _started = now;
            using var timing = PerformanceStages.Measure("HeimKit", "asset_async_start", 1, subject: subject);
            _held = true;
            try { if (_start == null) _reference.LoadAsync(); else _start(_reference); }
            catch { Dispose(); throw; }
            LastWaitSeconds = 0;
            LastOutcome = "loading";
            return false; // Even an already-loaded asset is inspected in another admitted unit.
        }
        LastWaitSeconds = Math.Max(0, now - _started);
        if (_loaded == null ? _reference.IsLoaded : _loaded(_reference))
        {
            asset = _read == null ? _reference.Asset : _read(_reference); LastOutcome = asset != null ? "ready" : "empty"; return true;
        }
        if ((_loading == null ? _reference.IsLoading : _loading(_reference)) && now >= _started && LastWaitSeconds < 120) return false;
        LastOutcome = now < _started ? "clock_reset" : LastWaitSeconds >= 120 ? "timeout" : "failed";
        return true;
    }
    public void Dispose()
    {
        if (!_held) return;
        _held = false;
        try { if (_release == null) _reference.Release(); else _release(_reference); }
        finally { _reference = default; }
    }
}
