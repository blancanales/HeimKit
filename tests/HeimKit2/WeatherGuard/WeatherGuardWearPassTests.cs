using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.WeatherGuard;

internal static class WeatherGuardWearPassTests
{
    private static ZDOID Id(string text)
    {
        var parts = text.Split(':');
        return parts.Length == 2 ? new ZDOID(long.Parse(parts[0]), uint.Parse(parts[1])) : new ZDOID(text.GetStableHashCode(), 1);
    }
    public static void Run()
    {
        var pass = new WeatherGuardWearPass();
        pass.Record(Id("covered"), false, true);
        pass.Record(Id("failed"), true, false);
        pass.Record(Id("wood-a"), true, true);
        pass.Record(Id("wood-a"), true, true);
        pass.Record(Id("wood-b"), true, true);
        if (pass.Complete(true) != 2 || pass.Complete(true) != 0)
            throw new Exception("Wear prevention pass includes unconfirmed pieces, duplicates identities or repeats a completed batch.");
        pass.Record(Id("wood-a"), true, true);
        if (pass.Complete(false) != 0 || pass.Complete(true) != 0)
            throw new Exception("Failed native pass emits successful prevention or retains pending entries.");
        pass.Record(Id("wood-a"), true, true);
        if (pass.Complete(true) != 1) throw new Exception("Later actual damage opportunity was suppressed from the log.");
        pass.Record(Id("1:341004"), true, true, "wood_stair");
        pass.Record(Id("1:341004"), true, true, "duplicate");
        if (pass.Complete(true, out var id, out var prefab) != 1 || id != "1:341004" || prefab != "wood_stair")
            throw new Exception("A single prevention or duplicate record lost the captured piece identity.");
        pass.Record(Id("wood-a"), true, true, "wood_stair"); pass.Record(Id("wood-b"), true, true, "woodwall");
        if (pass.Complete(true, out id, out prefab) != 2 || id != null || prefab != null)
            throw new Exception("Multiple-piece prevention exposes a misleading single identity.");
        pass.Record(Id("failed"), true, true, "wood_stair");
        if (pass.Complete(false, out id, out prefab) != 0 || id != null || prefab != null)
            throw new Exception("Failed pass retained an apparent successful piece identity.");
        if (pass.Complete(true, out id, out prefab) != 0 || id != null || prefab != null)
            throw new Exception("Completed pass retained stale single-piece metadata.");
        pass.Record(Id("frame-one"), true, true, "wood_stair");
        if (pass.CompleteFrame(true, false, out id, out prefab) != 0 || id != null || prefab != null)
            throw new Exception("Partial native sweep emitted a separate one-piece notice.");
        pass.Record(Id("frame-two"), true, true, "woodwall");
        if (pass.CompleteFrame(true, false, out id, out prefab) != 0)
            throw new Exception("Second partial frame emitted a prevention notice.");
        pass.Record(Id("frame-three"), true, true, "wood_pole_log_4");
        if (pass.CompleteFrame(true, true, out id, out prefab) != 3 || id != null || prefab != null)
            throw new Exception("Full native sweep failed to group prevention across frames.");
        pass.Record(Id("before-error"), true, true, "wood_stair");
        pass.CompleteFrame(true, false, out id, out prefab);
        if (pass.CompleteFrame(false, false, out id, out prefab) != 0 || pass.Complete(true) != 0)
            throw new Exception("A failed later frame retained prevention from an incomplete sweep.");
        pass.Record(Id("one-piece-sweep"), true, true, "wood_stair");
        if (pass.CompleteFrame(true, true, out id, out prefab) != 1 || id != Id("one-piece-sweep").ToString() || prefab != "wood_stair")
            throw new Exception("Single-piece sweep lost identification.");
        System.Console.WriteLine("PASS: native wear-pass grouping counts confirmed distinct suppressions, excludes failed/untouched pieces, discards failed batches and permits later real prevention.");
    }
}
