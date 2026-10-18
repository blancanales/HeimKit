using System.Collections.Generic;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// Cancel identical records first, preserving duplicate multiplicity without quadratic pin searches.
public static class SharedMapPinChanges
{
    public static MapPinChanges Compare(IReadOnlyList<MapPin> before, IReadOnlyList<MapPin> after)
    {
        var prior = new Dictionary<(string Id, bool Checked, string Author), Queue<MapPin>>();
        foreach (var pin in before)
        {
            var key = (SharedMapPinRemovals.Identity(pin), pin.Checked, pin.Author);
            if (!prior.TryGetValue(key, out var queue)) prior.Add(key, queue = new Queue<MapPin>());
            queue.Enqueue(pin);
        }
        var unmatched = new List<(string Id, MapPin Pin)>();
        foreach (var pin in after)
        {
            string id = SharedMapPinRemovals.Identity(pin);
            if (prior.TryGetValue((id, pin.Checked, pin.Author), out var queue) && queue.Count > 0) queue.Dequeue();
            else unmatched.Add((id, pin));
        }
        var remaining = new Dictionary<string, Queue<MapPin>>();
        foreach (var entry in prior)
        {
            if (entry.Value.Count == 0) continue;
            if (!remaining.TryGetValue(entry.Key.Id, out var queue)) remaining.Add(entry.Key.Id, queue = new Queue<MapPin>());
            foreach (var pin in entry.Value) queue.Enqueue(pin);
        }
        var changes = new MapPinChanges();
        foreach (var entry in unmatched)
        {
            if (remaining.TryGetValue(entry.Id, out var queue) && queue.Count > 0) changes.Updated.Add((queue.Dequeue(), entry.Pin));
            else changes.Added.Add(entry.Pin);
        }
        foreach (var queue in remaining.Values) changes.Removed.AddRange(queue);
        return changes;
    }
}
