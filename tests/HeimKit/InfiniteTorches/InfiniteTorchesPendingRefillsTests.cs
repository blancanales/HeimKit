using HeimKit.Features.InfiniteTorches.Models;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Configuration;

internal static class InfiniteTorchesPendingRefillsTests
{
    public static void Run()
    {
        var pending = new InfiniteTorchesPendingRefills();
        var torchId = new ZDOID(1L, 1U);
        var otherTorchId = new ZDOID(1L, 2U);
        var outcomes = new List<string>();
        if (!pending.TryTrack(torchId, 10L, 0f, 4f, 0f) || pending.TryTrack(torchId, 10L, 0f, 4f, 5f))
            throw new Exception("Duplicate request was not suppressed by object ID.");
        pending.Reconcile(5f, _ => new RefillObservation(10L, 0f), outcomes.Add);
        if (!pending.Contains(torchId) || outcomes.Count != 0) throw new Exception("Pending request expired too early.");
        pending.Reconcile(10f, _ => new RefillObservation(10L, 1f), outcomes.Add);
        if (!pending.Contains(torchId)) throw new Exception("Insufficient partial fuel increase released the request.");
        pending.Reconcile(15f, _ => new RefillObservation(10L, 3.99f), outcomes.Add);
        if (pending.Count != 0 || !outcomes.Exists(m => m.Contains("observed fuel increase")))
            throw new Exception("Replicated fuel increase was not observed.");
        pending.TryTrack(torchId, 10L, 0f, 4f, 20f);
        pending.Reconcile(49f, _ => new RefillObservation(10L, 0f), outcomes.Add);
        if (!pending.Contains(torchId)) throw new Exception("Retry permitted before the timeout.");
        pending.Reconcile(50f, _ => new RefillObservation(10L, 0f), outcomes.Add);
        if (pending.Contains(torchId) || !outcomes.Exists(m => m.Contains("retry allowed")))
            throw new Exception("Timeout did not permit retry.");
        pending.TryTrack(torchId, 10L, 0f, 4f, 60f);
        pending.TryTrack(otherTorchId, 10L, 0f, 4f, 60f);
        pending.Reconcile(65f, id => id.Equals(torchId) ? new RefillObservation(20L, 0f) : (RefillObservation?)null, outcomes.Add);
        if (pending.Count != 0 || !outcomes.Exists(m => m.Contains("owner changed")) || !outcomes.Exists(m => m.Contains("no longer available")))
            throw new Exception("Owner changes and removed objects were not cleared.");
        for (uint index = 1; index <= InfiniteTorchesPendingRefills.Capacity; index++)
            if (!pending.TryTrack(new ZDOID(2L, index), 10L, 0f, 4f, 70f)) throw new Exception("Unexpected tracking capacity.");
        if (pending.TryTrack(new ZDOID(3L, 1U), 10L, 0f, 4f, 70f)) throw new Exception("Pending tracking is unbounded.");
        pending.Reconcile(100f, _ => new RefillObservation(10L, 0f), null);
        if (pending.Count != 0) throw new Exception("Out-of-area timed-out requests leaked tracking state.");
        pending.TryTrack(torchId, 10L, 0f, 4f, 110f);
        pending.Clear();
        pending.Clear();
        if (pending.Count != 0) throw new Exception("Shutdown did not clear pending tracking.");
        System.Console.WriteLine("PASS: pending refills deduplicate IDs, wait for sufficient fuel, retry only after timeout, handle ownership/deletion, bound memory and clear on shutdown.");
    }
}
