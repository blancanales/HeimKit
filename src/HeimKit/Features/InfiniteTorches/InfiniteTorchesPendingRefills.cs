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
    public int Reconcile(float now, Func<ZDOID, RefillObservation?> observe, Action<RefillResult>? report)
    {
        _completed.Clear();
        int fuelUpdates = 0;
        foreach (var item in _requests)
        {
            var request = item.Value;
            var observation = observe(item.Key);
            string? reason = null;
            if (!observation.HasValue)
                reason = "object_missing";
            else if (observation.Value.Owner != request.Owner)
                reason = "owner_changed";
            else if (!float.IsNaN(observation.Value.Fuel) && !float.IsInfinity(observation.Value.Fuel) &&
                observation.Value.Fuel > request.Before + 0.001f && observation.Value.Fuel >= request.Target * 0.75f)
                reason = "fuel_increase";
            else if (now - request.SentAt >= TimeoutSeconds)
                reason = "timeout";
            if (reason == null) continue;
            _completed.Add(item.Key);
            if (reason == "fuel_increase") fuelUpdates++;
            report?.Invoke(new RefillResult { Object = item.Key.ToString(), Outcome = reason,
                RequestedOwner = request.Owner, ObservedOwner = observation?.Owner, Fuel = observation?.Fuel,
                Before = request.Before, Target = request.Target, WaitSeconds = now - request.SentAt });
        }
        foreach (var id in _completed) _requests.Remove(id);
        _completed.Clear();
        return fuelUpdates;
    }

    public void Clear() { _requests.Clear(); _completed.Clear(); }
}
