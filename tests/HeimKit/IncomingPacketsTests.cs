using System.Reflection;
using System.Runtime.CompilerServices;
using HeimKit.Core.Ownership;
using HeimKit.Core.Players;

internal static class IncomingPacketsTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run()
    {
        var slot = typeof(ZNet).GetField("m_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var prior = slot.GetValue(null); slot.SetValue(null, null);
        try { Ordering(); Nested(); Headers(); Allocation(); RetainedIds(slot); }
        finally { slot.SetValue(null, prior); }
    }
    private static void Ordering()
    {
        var channel = new IncomingPackets.Channel(false, install: false); var trace = new List<string>();
        using var first = channel.Register<int>((IncomingPackets.Context c, ref ZPackage p, out int state) => { state = 41; trace.Add("first"); p.ReadInt(); return false; },
            state => trace.Add("post:" + state), (error, state) => { trace.Add("final:" + state); return error; });
        using var skipped = channel.Register<int>((IncomingPackets.Context c, ref ZPackage p, out int state) => { state = 99; trace.Add("later"); return true; },
            state => trace.Add("skipped-post:" + state));
        using var observer = channel.Register<byte>((IncomingPackets.Context c, ref ZPackage p, out byte state) => { state = 0; trace.Add("observer"); return true; }, affectsOriginal: false);
        var packet = new ZPackage(); packet.Write(123); packet.SetPos(0);
        Check(!IncomingPackets.Before(channel, null!, ref packet, out var call) && packet.GetPos() == 0, "Skip-original or per-handler cursor restoration changed.");
        IncomingPackets.Applied(call); var native = new IOException("native failure");
        Check(ReferenceEquals(IncomingPackets.Finished(native, call), native) &&
            trace.SequenceEqual(new[] { "first", "later", "observer", "post:41", "skipped-post:99", "final:41" }),
            "Registration order, all-prefix admission, later-prefix state or native exception preservation changed.");
        int failures = 0, survivors = 0;
        var isolated = new IncomingPackets.Channel(false, install: false);
        using var bad = isolated.Register<byte>((IncomingPackets.Context c, ref ZPackage p, out byte state) => { state = 0; throw new IOException("handler failure"); }, failed: _ => failures++);
        using var good = isolated.Register<byte>((IncomingPackets.Context c, ref ZPackage p, out byte state) => { state = 0; survivors++; return true; }, affectsOriginal: false);
        Check(!IncomingPackets.Before(isolated, null!, ref packet, out call), "Escaping filter failure admitted native mutation."); IncomingPackets.Finished(null, call);
        Check(failures == 1 && survivors == 1, "A failing handler prevented an independent pure observer or bypassed its own failure path.");
        System.Console.WriteLine("PASS: incoming dispatch preserves order, skip-original semantics, all later prefixes, cursor state, typed postfix state, native exceptions and local failure handling.");
    }
    private static void Nested()
    {
        var channel = new IncomingPackets.Channel(false, install: false); int depth = 0; var ended = new List<int>();
        using var handler = channel.Register<int>((IncomingPackets.Context c, ref ZPackage p, out int state) =>
        {
            state = ++depth;
            if (state == 1)
            {
                var inner = new ZPackage();
                Check(IncomingPackets.Before(channel, null!, ref inner, out var nested), "Nested packet was skipped.");
                IncomingPackets.Applied(nested); IncomingPackets.Finished(null, nested);
                Check(ReferenceEquals(c.Packet, p), "Nested dispatch replaced the outer call context.");
            }
            return true;
        }, finalizer: (error, state) => { ended.Add(state); depth--; return error; });
        var packet = new ZPackage(); IncomingPackets.Before(channel, null!, ref packet, out var outer);
        IncomingPackets.Applied(outer); IncomingPackets.Finished(null, outer);
        Check(ended.SequenceEqual(new[] { 2, 1 }) && depth == 0, "Nested packets overwrote typed per-call state.");
        System.Console.WriteLine("PASS: nested incoming packets restore independent call context and finalizer states.");
    }
    private static ZPackage Routed(long targetPeer = 7)
    {
        var packet = new ZPackage(); packet.Write(1L); packet.Write(8L); packet.Write(targetPeer); packet.Write(new ZDOID(8, 2)); packet.Write(123);
        var args = new ZPackage(); args.Write(55); packet.Write(args); packet.SetPos(0); return packet;
    }
    private static void Headers()
    {
        var channel = new IncomingPackets.Channel(true, install: false); int seen = 0;
        using var replace = channel.Register<byte>((IncomingPackets.Context c, ref ZPackage p, out byte state) =>
        {
            state = 0;
            Check(IncomingPackets.TryHeader(p, out var header, out var error) && error == null && header.TargetPeer == 7 && header.Message == 1 && header.Sender == 8,
                "The routing header was not shared with its handler.");
            var data = ZdoPackets.ReadRouted(p); data.m_targetPeerID = 9;
            var output = new ZPackage(); data.Serialize(output); output.SetPos(0); p = output; return true;
        });
        using var reader = channel.Register<byte>((IncomingPackets.Context c, ref ZPackage p, out byte state) =>
        {
            state = 0;
            Check(IncomingPackets.TryHeader(p, out var header, out var error) && error == null && header.TargetPeer == 9 && p.GetPos() == 0,
                "A replacement packet retained a stale routing header.");
            Check(RoutedActivityReader.TryReadParameters(p, 8, out var target, out int method, out var args) && target == new ZDOID(8, 2) && method == 123 && args!.ReadInt() == 55 && p.GetPos() == 0,
                "Shared routing metadata changed authenticated parameters or cursor restoration.");
            seen++; return true;
        });
        var packet = Routed(); Check(IncomingPackets.Before(channel, null!, ref packet, out var call), "A valid replacement packet was skipped.");
        IncomingPackets.Applied(call); IncomingPackets.Finished(null, call); Check(seen == 1 && ZdoPackets.ReadRouted(packet).m_targetPeerID == 9, "Routed replacement did not reach native input.");
        var invalid = new IncomingPackets.Channel(true, install: false);
        using var observer = invalid.Register<byte>((IncomingPackets.Context c, ref ZPackage p, out byte state) =>
        { state = 0; Check(!RoutedActivityReader.TryRead(p, 8, out _, out _), "Malformed routing metadata became authenticated input."); return true; }, affectsOriginal: false);
        packet = new ZPackage(new byte[] { 1 }); IncomingPackets.Before(invalid, null!, ref packet, out call); IncomingPackets.Finished(null, call);
        System.Console.WriteLine("PASS: cached routed headers follow replacements, preserve sender/parameters/cursors and reject malformed authenticated observations.");
    }
    private static void Allocation()
    {
        var channel = new IncomingPackets.Channel(false, install: false);
        using var handler = channel.Register<int>(static (IncomingPackets.Context c, ref ZPackage p, out int state) => { state = 7; return true; });
        var packet = new ZPackage();
        for (int i = 0; i < 100; i++) { IncomingPackets.Before(channel, null!, ref packet, out var call); IncomingPackets.Applied(call); IncomingPackets.Finished(null, call); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { IncomingPackets.Before(channel, null!, ref packet, out var call); IncomingPackets.Applied(call); IncomingPackets.Finished(null, call); }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Warmed shared dispatch allocated or boxed per-packet typed state.");
        System.Console.WriteLine("PASS: warmed incoming dispatch reuses typed invocation/context storage without per-packet allocations.");
    }
    private static void RetainedIds(FieldInfo slot)
    {
        var net = (ZNet)RuntimeHelpers.GetUninitializedObject(typeof(ZNet)); var rpc = (ZRpc)RuntimeHelpers.GetUninitializedObject(typeof(ZRpc));
        var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); peer.m_uid = 8; peer.m_rpc = rpc;
        typeof(ZNet).GetField("m_peers", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(net, new List<ZNetPeer> { peer }); slot.SetValue(null, net);
        try
        {
            var channel = new IncomingPackets.Channel(false, install: false); var id = new ZDOID(8, 11); int first = 0, second = 0, predicate = 0;
            using var a = channel.Register<byte>((IncomingPackets.Context c, ref ZPackage p, out byte state) =>
            { state = 0; Check(ReferenceEquals(c.Peer, peer) && PlayerPeerLookup.Ready(Array.Empty<ZNetPeer>(), rpc) == null, "Shared peer provenance widened the original presence-roster authentication."); if (IncomingPackets.NeedsDataFilter) first++; return true; }, retained: current => { predicate++; return current == id; });
            using var b = channel.Register<byte>((IncomingPackets.Context c, ref ZPackage p, out byte state) =>
            { state = 0; if (IncomingPackets.NeedsDataFilter) second++; return true; }, retained: _ => false);
            var packet = new ZPackage(); packet.Write(1); packet.Write(id); packet.Write(id); packet.Write((ushort)1); packet.Write(2U); packet.Write(8L); packet.Write(UnityEngine.Vector3.zero); packet.Write(new ZPackage()); packet.Write(ZDOID.None); packet.SetPos(0);
            IncomingPackets.Before(channel, rpc, ref packet, out var call); IncomingPackets.Applied(call); IncomingPackets.Finished(null, call);
            Check(first == 1 && second == 0 && predicate == 2 && packet.GetPos() == 0, "Retained-ID selection repeated the packet walk, lost invalidation IDs or ran an irrelevant filter.");
            packet = new ZPackage(new byte[] { 1 }); IncomingPackets.Before(channel, rpc, ref packet, out call); IncomingPackets.Finished(null, call);
            Check(first == 2 && second == 1, "Malformed preinspection bypassed original filter failure policies.");
        }
        finally { slot.SetValue(null, null); }
        System.Console.WriteLine("PASS: shared ZDO inspection walks invalidation/record IDs once, dispatches only matching filters and retains each malformed-input policy.");
    }
}
