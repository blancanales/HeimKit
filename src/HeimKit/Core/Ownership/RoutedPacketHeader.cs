namespace HeimKit.Core.Ownership;

internal readonly struct RoutedPacketHeader
{
    internal readonly long Message, Sender, TargetPeer;
    internal readonly ZDOID Target;
    internal readonly int Method, End;
    internal RoutedPacketHeader(ZPackage packet)
    {
        Message = packet.ReadLong(); Sender = packet.ReadLong(); TargetPeer = packet.ReadLong();
        Target = packet.ReadZDOID(); Method = packet.ReadInt(); End = packet.GetPos();
    }
}
