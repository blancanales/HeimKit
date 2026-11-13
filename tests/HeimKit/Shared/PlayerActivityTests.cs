using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Core.Players;
using HeimKit.Core.Players.Models;
using HeimKit.Features.AFKRaidWatch;
using System.Reflection;
internal static class PlayerActivityTests
{
    private static void Check(bool met, string reason) { if (!met) throw new Exception(reason); }
    private static PlayerActivityObservation Actor(float x = 0, float heading = 0, bool busy = false, bool bed = false, bool known = true, bool alive = true, string character = "actor", float health = 100, string equipment = "", int emote = 0)
        => new() { Peer = 1, Character = character, Known = known, Alive = alive, X = x, Heading = heading, Busy = busy, InBed = bed, Health = health, Equipment = equipment, Emote = emote };
    public static void Run()
    {
        var tracker = new PlayerActivityTracker(new(10, 2, 0.5f, 10, 1));
        PlayerActivityView Step(float time, PlayerActivityObservation? actor = null) => tracker.Update(time, new[] { actor ?? Actor() })[0];
        Check(!Step(0).Idle, "Join grace");
        for (int t = 1; t < 10; t++) Check(!Step(t).Idle, "Timeout not reached");
        Check(Step(10).Idle, "Idle timeout boundary");
        tracker.Signal(1, 10.2f); Check(!Step(11).Idle, "Routed activity wakes idle actor");
        tracker.Clear(); Step(0);
        for (int t = 1; t <= 10; t++) Step(t, Actor(busy: true));
        Check(!Step(11).Idle, "Station/container activity remains active");
        tracker.Clear(); Step(0);
        for (int t = 1; t <= 10; t++) Step(t, Actor(bed: true));
        Check(!Step(11).Idle, "Leaving bed starts fresh activity allowance");
        tracker.Clear(); Step(0);
        Check(!Step(10).Idle, "Missing observation interval resets grace");
        Check(!Step(9).Idle, "Clock rollback resets grace");
        Step(10, Actor(known: false)); Check(!Step(11).Idle, "Loading recovery resets grace");
        Step(12, Actor(alive: false)); Check(!Step(13).Idle, "Death recovery resets grace");
        Step(14, Actor(x: float.NaN)); Check(!Step(15).Idle, "Invalid geometry recovery resets grace");
        Check(!Step(16, Actor(character: "respawn")).Idle, "New character resets grace");
        tracker.Update(17, Array.Empty<PlayerActivityObservation>()); Check(!Step(18).Idle, "Reconnect starts a new session");
        tracker.Clear(); Step(0);
        for (int t = 1; t <= 9; t++) Step(t, Actor(x: t * 0.06f));
        Check(!Step(10, Actor(x: 0.6f)).Idle, "Cumulative slow movement counts as activity");
        tracker.Clear(); Step(0, Actor(heading: 359));
        for (int t = 1; t <= 10; t++) Step(t, Actor(heading: 1));
        Check(Step(11, Actor(heading: 1)).Idle, "Heading wrap must not invent activity");
        Check(!Step(12, Actor(heading: 20)).Idle, "Observed look rotation wakes idle player");
        foreach (var action in new[] { Actor(equipment: "axe"), Actor(emote: 1) })
        {
            tracker.Clear(); Step(0); for (int t = 1; t <= 10; t++) Step(t);
            Check(!Step(11, action).Idle, "Equipment/emote activity wakes idle player");
        }
        tracker.Clear(); Step(0);
        for (int t = 1; t <= 10; t++) Step(t, Actor(health: 100 + t));
        Check(Step(11, Actor(health: 111)).Idle, "Passive regeneration must not prevent idle inference");
        bool invalid = false;
        try { _ = new PlayerActivityOptions(float.NaN, 0, 1, 1, 1); } catch (ArgumentOutOfRangeException) { invalid = true; }
        Check(invalid, "NaN config rejected");

        var packet = new ZPackage(); packet.Write(1L); packet.Write(12L); packet.Write(13L);
        var target = new ZDOID(12, 4); packet.Write(target); packet.Write("RPC_Repair".GetStableHashCode()); packet.Write("native body"); packet.SetPos(0);
        var bytes = packet.GetArray();
        Check(RoutedActivityReader.TryRead(packet, 12, out var parsed, out int method) && parsed == target && RoutedActivityReader.IsActivity(method), "Read authenticated native activity header");
        Check(packet.GetPos() == 0 && bytes.SequenceEqual(packet.GetArray()), "Observer preserves cursor and every byte");
        Check(!RoutedActivityReader.TryRead(packet, 99, out _, out _) && packet.GetPos() == 0, "Forged sender not activity");
        var body = new ZPackage(); body.Write("pickaxe_swing");
        var routed = new ZPackage(); routed.Write((byte)42);
        routed.Write(1L); routed.Write(12L); routed.Write(13L); routed.Write(target);
        routed.Write("SetTrigger".GetStableHashCode()); routed.Write(body); routed.SetPos(1);
        byte[] routedBytes = routed.GetArray();
        Check(RoutedActivityReader.TryReadParameters(routed, 12, out var routedTarget, out _, out var parameters) &&
            routedTarget == target && parameters?.ReadString() == "pickaxe_swing", "Read authenticated bounded native parameters");
        Check(routed.GetPos() == 1 && routed.GetArray().SequenceEqual(routedBytes), "Parameter reader changed traffic/cursor");
        Check(!RoutedActivityReader.TryReadParameters(routed, 99, out _, out _, out _) && routed.GetPos() == 1,
            "Parameter reader accepted a forged sender or changed cursor");
        var truncatedBody = new ZPackage(routedBytes.Take(routedBytes.Length - 1).ToArray()); truncatedBody.SetPos(1);
        Check(!RoutedActivityReader.TryReadParameters(truncatedBody, 12, out _, out _, out _) && truncatedBody.GetPos() == 1,
            "Truncated parameter envelope supplied evidence");
        var malformed = new ZPackage(new byte[] { 1, 2 });
        Check(!RoutedActivityReader.TryRead(malformed, 12, out _, out _) && malformed.GetPos() == 0, "Malformed header is unchanged");
        Check(!RoutedActivityReader.IsActivity("RPC_Pong".GetStableHashCode()), "Housekeeping is not activity");

        System.Console.WriteLine("PASS: shared activity movement/body rotation/equipment/emote/station/bed inference, grace/recovery/bounds and authenticated byte-preserving routed observation.");
    }
}
