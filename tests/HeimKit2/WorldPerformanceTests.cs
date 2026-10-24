using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Reflection;
using System.Runtime.CompilerServices;
using HeimKit.Features.DoorKeeper;
using HeimKit.Features.ForestRenewal;
using HeimKit.Features.ForestRenewal.Models;
using HeimKit.Features.TameTravel;
using UnityEngine;

internal static class WorldPerformanceTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        PacketInspection(); RenewalIndexes(); IdleHooks(); NativeDelegates();
    }
    private static void PacketInspection()
    {
        var animal = new ZDOID(20, 1); var unrelated = new ZDOID(20, 2);
        var packet = new ZPackage(); packet.Write(1); packet.Write(unrelated);
        packet.Write(animal); packet.Write((ushort)7); packet.Write(9U); packet.Write(20L); packet.Write(new Vector3(1, 2, 3));
        var payload = new ZPackage(); payload.Write(new byte[4096]); packet.Write(payload); packet.Write(ZDOID.None); packet.SetPos(0);
        var bytes = packet.GetArray();
        Check(ZdoPackets.ContainsData(packet, id => id == animal), "Retained animal payload was missed.");
        Check(ZdoPackets.ContainsData(packet, id => id == unrelated), "Retained sector invalidation was missed.");
        Check(!ZdoPackets.ContainsData(packet, _ => false) && packet.GetPos() == 0 && bytes.SequenceEqual(packet.GetArray()), "Unrelated packet or cursor changed.");
        Func<ZDOID, bool> none = _ => false;
        for (int i = 0; i < 100; i++) ZdoPackets.ContainsData(packet, none);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) ZdoPackets.ContainsData(packet, none);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "Opaque packet inspection allocated " + allocated + " bytes.");
        foreach (var malformed in new[] { new ZPackage(bytes[..^1]), new ZPackage(bytes.Concat(new byte[] { 1 }).ToArray()) })
        {
            bool rejected = false;
            try { ZdoPackets.ContainsData(malformed, _ => true); } catch (Exception error) when (error is IOException or InvalidDataException) { rejected = true; }
            Check(rejected && malformed.GetPos() == 0, "Malformed packet passed inspection or lost its cursor.");
        }
        System.Console.WriteLine("PASS: packet inspection finds data/invalidation IDs, preserves opaque bytes/cursors, rejects malformed tails and allocates zero bytes over 1000 warmed unrelated scans.");
    }
    private static void RenewalIndexes()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(ForestRenewalController);
        var controller = RuntimeHelpers.GetUninitializedObject(type);
        var pending = new HashSet<RenewalRecord>();
        type.GetField("_pending", flags)!.SetValue(controller, pending);
        var cells = new Dictionary<(int X, int Z), HashSet<RenewalRecord>>();
        type.GetField("_cooldowns", flags)!.SetValue(controller, cells);
        var consume = type.GetMethod("Consume", flags)!;
        var cooldown = type.GetMethod("CoolingDown", flags)!;
        var record = new RenewalRecord { Source = new ZDOID(30, 1), State = 3, Position = new Vector3(-.1f, 100, -.1f) };
        pending.Add(record); type.GetField("_pendingCount", flags)!.SetValue(controller, 1);
        consume.Invoke(controller, new object[] { record, 100L, (byte)2 });
        Check(pending.Count == 0 && cells.Count == 1 && (int)type.GetField("_pendingCount", flags)!.GetValue(controller)! == 0, "Consumption indexes diverged.");
        var next = new RenewalRecord { Source = new ZDOID(30, 2), Position = new Vector3(.1f, -100, .1f) };
        bool Cooling(long ticks) => (bool)cooldown.Invoke(controller, new object[] { next, ticks, 100L })!;
        Check(Cooling(199) && !Cooling(200) && Cooling(90), "Cooldown lost expiry, horizontal cell-boundary coverage or clock rollback.");
        next.Position = new Vector3(4.9f, 0, -.1f); Check(!Cooling(150), "Exactly five metres incorrectly blocked renewal.");
        consume.Invoke(controller, new object[] { record, 120L, (byte)3 });
        Check(pending.Contains(record) && cells.Count == 0 && !Cooling(150), "Restored pending decision retained a consumed cooldown.");
        consume.Invoke(controller, new object[] { record, 130L, (byte)1 });
        Check(pending.Count == 0 && cells.Count == 0, "Excluded decision remained pending.");
        System.Console.WriteLine("PASS: renewal indexes preserve pending/consumed transitions, horizontal negative-cell boundaries, exact cooldown expiry and native-world clock rollback.");
    }
    private static void IdleHooks()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        Check((bool)typeof(ObjectTransferAuthority).GetMethod("Pause", flags)!.Invoke(null, new object?[] { null })!, "Idle physics hook accessed its component.");
        Check((bool)typeof(TameTravelFollowing).GetMethod("Follow", flags)!.Invoke(null, new object?[] { null, null, 0f })!, "Idle following hook accessed its components.");
        foreach (string hook in new[] { "Data", "Routed" })
            Check((bool)typeof(ObjectAuthority).GetMethod(hook, flags)!.Invoke(null, new object?[] { null, null })!, "Idle shared authority inspected packet traffic.");
        System.Console.WriteLine("PASS: idle physics/follow and shared authority hooks bypass component and packet access.");
    }
    private static void NativeDelegates()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        _ = typeof(Door).GetMethod("CanInteract", flags)!.CreateDelegate(typeof(Func<Door, bool>));
        _ = typeof(Door).GetMethod("RPC_UseDoor", flags)!.CreateDelegate(typeof(Action<Door, long, bool>));
        _ = typeof(ZoneSystem).GetMethod("CreateLocalZones", flags)!.CreateDelegate(typeof(Func<ZoneSystem, Vector3, bool>));
        _ = typeof(Character).GetMethod("ResetGroundContact", flags)!.CreateDelegate(typeof(Action<Character>));
        System.Console.WriteLine("PASS: cached native door, zone and character delegates bind to installed assembly signatures.");
    }
}
