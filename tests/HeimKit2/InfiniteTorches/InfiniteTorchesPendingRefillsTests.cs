using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
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
        var outcomes = new List<RefillResult>();
        if (!pending.TryTrack(torchId, 10L, 0f, 4f, 0f) || pending.TryTrack(torchId, 10L, 0f, 4f, 5f))
            throw new Exception("Duplicate request was not suppressed by object ID.");
        pending.Reconcile(5f, _ => new RefillObservation(10L, 0f), outcomes.Add);
        if (!pending.Contains(torchId) || outcomes.Count != 0) throw new Exception("Pending request expired too early.");
        pending.Reconcile(10f, _ => new RefillObservation(10L, 1f), outcomes.Add);
        if (!pending.Contains(torchId)) throw new Exception("Insufficient partial fuel increase released the request.");
        pending.Reconcile(15f, _ => new RefillObservation(10L, 3.99f), outcomes.Add);
        if (pending.Count != 0 || !outcomes.Exists(m => m.Outcome == "fuel_increase"))
            throw new Exception("Replicated fuel increase was not observed.");
        pending.TryTrack(torchId, 10L, 0f, 4f, 20f);
        pending.Reconcile(49f, _ => new RefillObservation(10L, 0f), outcomes.Add);
        if (!pending.Contains(torchId)) throw new Exception("Retry permitted before the timeout.");
        pending.Reconcile(50f, _ => new RefillObservation(10L, 0f), outcomes.Add);
        if (pending.Contains(torchId) || !outcomes.Exists(m => m.Outcome == "timeout"))
            throw new Exception("Timeout did not permit retry.");
        pending.TryTrack(torchId, 10L, 0f, 4f, 60f);
        pending.TryTrack(otherTorchId, 10L, 0f, 4f, 60f);
        pending.Reconcile(65f, id => id.Equals(torchId) ? new RefillObservation(20L, 0f) : (RefillObservation?)null, outcomes.Add);
        if (pending.Count != 0 || !outcomes.Exists(m => m.Outcome == "owner_changed") || !outcomes.Exists(m => m.Outcome == "object_missing"))
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
        using var log = new ManualLogSource("Torch refill summary checks");
        var records = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, args) => records.Add((args.Level, LogCapture.Text(args.Data)));
        var diagnostics = new InfiniteTorchesDiagnostics(() => false, log);
        for (uint index = 1; index <= 32; index++)
            pending.TryTrack(new ZDOID(4L, index), 10L, 0f, 4f, 120f);
        int updated = pending.Reconcile(125f, _ => new RefillObservation(10L, 3.99f), result => diagnostics.Result(125f, result));
        diagnostics.RefillSummary(updated);
        if (updated != 32 || records.Count != 1 || records[0].Level != LogLevel.Info || records[0].Text != "InfiniteTorches: Fuel increase observed for 32 light sources.")
            throw new Exception("Multiple confirmed fuel updates were not grouped into one debug-off Info entry.");
        diagnostics.RefillSummary(pending.Reconcile(130f, _ => new RefillObservation(10L, 4f), null));
        if (records.Count != 1) throw new Exception("Already reconciled or empty passes repeated a refill summary.");
        pending.TryTrack(torchId, 10L, 0f, 4f, 140f);
        pending.TryTrack(otherTorchId, 10L, 0f, 4f, 140f);
        pending.TryTrack(new ZDOID(5L, 3U), 10L, 0f, 4f, 140f);
        pending.TryTrack(new ZDOID(5L, 4U), 10L, 0f, 4f, 140f);
        updated = pending.Reconcile(170f, id => id.Equals(torchId) ? new RefillObservation(10L, 3.99f) :
            id.Equals(otherTorchId) ? new RefillObservation(20L, 4f) : id.ID == 3U ? (RefillObservation?)null : new RefillObservation(10L, 0f), null);
        diagnostics.RefillSummary(updated);
        if (updated != 1 || records.Count != 2 || records[^1].Text != "InfiniteTorches: Fuel increase observed for 1 light source.")
            throw new Exception("Timeout/missing/owner-change outcomes were counted as updated torches.");
        pending.TryTrack(torchId, 10L, 0f, 4f, 180f);
        diagnostics.RefillSummary(pending.Reconcile(185f, _ => new RefillObservation(10L, 1f), null));
        if (records.Count != 2) throw new Exception("Insufficient progress or a sent request was logged as a refill.");
        pending.Clear(); diagnostics.Reset();
        diagnostics.RefillSummary(pending.Reconcile(190f, _ => new RefillObservation(10L, 4f), null));
        if (records.Count != 2) throw new Exception("Pause/reset retained a stale refill count.");
        System.Console.WriteLine("PASS: one Info batch for 32 observed refill updates without debugging, singular wording, quiet unchanged/empty passes and exclusion of failed/unconfirmed requests.");
        System.Console.WriteLine("PASS: pending refills deduplicate IDs, wait for sufficient fuel, retry only after timeout, handle ownership/deletion, bound memory and clear on shutdown.");
    }
}
