using System;

namespace HeimKit.Shared;

public static class RoutedActivityReader
{
    public static bool TryRead(ZPackage packet, long authenticatedPeer, out ZDOID target, out int method)
    {
        int cursor = packet.GetPos(); target = ZDOID.None; method = 0;
        try
        {
            packet.ReadLong(); long sender = packet.ReadLong(); packet.ReadLong();
            target = packet.ReadZDOID(); method = packet.ReadInt();
            return sender == authenticatedPeer;
        }
        catch (Exception) { target = ZDOID.None; method = 0; return false; }
        finally { packet.SetPos(cursor); }
    }
    public static bool IsActivity(int method) => method == "Say".GetStableHashCode() || method == "ChatMessage".GetStableHashCode() ||
        method == "SetTrigger".GetStableHashCode() || method == "RPC_Damage".GetStableHashCode() || method == "RPC_Repair".GetStableHashCode() ||
        method == "RPC_Remove".GetStableHashCode() || method == "RPC_RequestOpen".GetStableHashCode() || method == "RPC_RequestStack".GetStableHashCode() ||
        method == "RPC_RequestTakeAll".GetStableHashCode();
    public static string ActivityName(int method)
    {
        foreach (string name in Names) if (method == name.GetStableHashCode()) return name;
        return "unrecognized";
    }
    private static readonly string[] Names = { "Say", "ChatMessage", "SetTrigger", "RPC_Damage", "RPC_Repair", "RPC_Remove", "RPC_RequestOpen", "RPC_RequestStack", "RPC_RequestTakeAll" };
}
