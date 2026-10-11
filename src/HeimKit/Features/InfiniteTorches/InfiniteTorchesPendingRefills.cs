using HeimKit.Features.InfiniteTorches.Models;
using System;
using System.Collections.Generic;


namespace HeimKit.Features.InfiniteTorches;

public sealed class InfiniteTorchesPendingRefills
{
    public const float TimeoutSeconds = 30f;
    public const int Capacity = 2048;
    private readonly Dictionary<ZDOID, RefillRequest> _requests = new();
    private readonly List<ZDOID> _completed = new();
    public int Count => _requests.Count;

    public bool Contains(ZDOID id) => _requests.ContainsKey(id);

    public bool TryTrack(ZDOID id, long owner, float before, float target, float now)
    {
        if (_requests.ContainsKey(id) || _requests.Count >= Capacity) return false;
        _requests.Add(id, new RefillRequest(owner, before, target, now));
        return true;
    }

    public void Remove(ZDOID id) => _requests.Remove(id);

    // Examine all pending IDs, including those outside current player areas, so stale entries cannot accumulate.
    public void Reconcile(float now, Func<ZDOID, RefillObservation?> observe, Action<string>? report)
    {
        _completed.Clear();
        foreach (var item in _requests)
        {
            var request = item.Value;
            var observation = observe(item.Key);
            string? reason = null;
            if (!observation.HasValue)
                reason = "object no longer available in server data; tracking cleared";
            else if (observation.Value.Owner != request.Owner)
                reason = $"owner changed from {request.Owner} to {observation.Value.Owner}; old request tracking cleared";
            else if (!float.IsNaN(observation.Value.Fuel) && !float.IsInfinity(observation.Value.Fuel) &&
                observation.Value.Fuel > request.Before + 0.001f && observation.Value.Fuel >= request.Target * 0.75f)
                reason = $"observed fuel increase {request.Before:F3} -> {observation.Value.Fuel:F3} after request, target={request.Target:F3}; not a delivery acknowledgement";
            else if (now - request.SentAt >= TimeoutSeconds)
                reason = $"no sufficient fuel increase observed within {TimeoutSeconds}s; retry allowed if still eligible";
            if (reason == null) continue;
            _completed.Add(item.Key);
            report?.Invoke($"Infinite Torches pending refill: object={item.Key}; requested owner={request.Owner}; {reason}.");
        }
        foreach (var id in _completed) _requests.Remove(id);
        _completed.Clear();
    }

    public void Clear() { _requests.Clear(); _completed.Clear(); }
}
