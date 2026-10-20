using HeimKit.Features.WeatherGuard;
using UnityEngine;

internal static class WeatherGuardNetworkFilterTests
{
    private static void Reject(ZPackage packet, Action<ZPackage> read)
    {
        int cursor = packet.GetPos();
        var bytes = packet.GetArray();
        bool rejected = false;
        try { read(packet); }
        catch (System.IO.InvalidDataException) { rejected = true; }
        if (!rejected || packet.GetPos() != cursor || !bytes.SequenceEqual(packet.GetArray()))
            throw new Exception("Malformed embedded package was accepted or modified the input.");
    }

    public static void Run()
    {
        var id = new ZDOID(1L, 10U);
        foreach (int length in new[] { -1, 1, WeatherGuardNetworkFilter.MaximumPacketBytes + 1, int.MaxValue })
        {
            var data = new ZPackage();
            data.Write(0); data.Write(id); data.Write((ushort)1); data.Write(1U);
            data.Write(1L); data.Write(new Vector3()); data.Write(length); data.SetPos(0);
            int callbacks = 0;
            Reject(data, p => WeatherGuardNetworkFilter.FilterData(p, _ => true, out _, (_, _, _) => callbacks++));
            Reject(data, p => WeatherGuardNetworkFilter.FilterData(p, _ => false, out _));
            if (callbacks != 0) throw new Exception("Invalid packet changed authority metadata.");

            var routed = new ZPackage();
            routed.Write(1L); routed.Write(2L); routed.Write(3L); routed.Write(id);
            routed.Write("RPC_Repair".GetStableHashCode()); routed.Write(length); routed.SetPos(0);
            Reject(routed, p => WeatherGuardNetworkFilter.ReadRouted(p));
        }

        var parameters = new ZPackage(); parameters.Write(123L); parameters.Write("opaque payload");
        var expected = new ZRoutedRpc.RoutedRPCData
        {
            m_msgID = 1, m_senderPeerID = 2, m_targetPeerID = 3, m_targetZDO = id,
            m_methodHash = "RPC_Damage".GetStableHashCode(), m_parameters = parameters
        };
        var packet = new ZPackage(); expected.Serialize(packet); packet.SetPos(0);
        var bytes = packet.GetArray();
        var parsed = WeatherGuardNetworkFilter.ReadRouted(packet);
        var output = new ZPackage(); parsed.Serialize(output);
        if (packet.GetPos() != 0 || !bytes.SequenceEqual(output.GetArray()))
            throw new Exception("Bounded routed decoding changed native header or opaque parameters.");
        Reject(new ZPackage(bytes.Concat(new byte[] { 0 }).ToArray()), p => WeatherGuardNetworkFilter.ReadRouted(p));

        // Decoding must also restore a caller's nonzero cursor.
        var offset = new ZPackage(); offset.Write(777); expected.Serialize(offset); offset.SetPos(4);
        parsed = WeatherGuardNetworkFilter.ReadRouted(offset);
        if (offset.GetPos() != 4 || parsed.m_targetZDO != id) throw new Exception("Nonzero routing cursor lost.");
        var state = new ZPackage(); state.Write(777); state.Write(1); state.Write(id); state.Write(ZDOID.None); state.SetPos(4);
        var same = WeatherGuardNetworkFilter.FilterData(state, _ => false, out int blocked);
        if (!ReferenceEquals(same, state) || blocked != 0 || state.GetPos() != 4)
            throw new Exception("Unchanged state packet lost its nonzero cursor or allocated a replacement.");
        var filtered = WeatherGuardNetworkFilter.FilterData(state, _ => true, out blocked);
        if (blocked != 1 || state.GetPos() != 4 || filtered.ReadInt() != 0 || !filtered.ReadZDOID().IsNone() || filtered.GetPos() != filtered.Size())
            throw new Exception("Invalidation-only filtering included the caller prefix or lost the terminator.");
        System.Console.WriteLine("PASS: embedded ZDO/routed lengths rejected before allocation, no invalid-packet metadata callbacks, native routing bytes and cursors preserved.");
    }
}
