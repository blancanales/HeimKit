using System;

namespace HeimKit.Core.Native;

// Read the physical transport, not just the completed login handshake. PlayFab
// keeps IsConnected true while TX is suspended and during initial connection.
internal static class NativeConnection
{
    private static class PlayFab
    {
        static PlayFab() { }
        internal static readonly Func<ZPlayFabSocket, bool> Resetting = NativeAccess.Method<Func<ZPlayFabSocket, bool>>(typeof(ZPlayFabSocket), "PartyResetInProgress");
        internal static readonly Func<object, int> State = NativeAccess.ObjectField<int>(typeof(ZPlayFabSocket), "m_state");
        internal static readonly int Connected = NativeAccess.EnumFieldValue(typeof(ZPlayFabSocket), "m_state", "CONNECTED");
    }
    internal static void CheckCompatibility()
    {
        try { _ = PlayFab.Resetting; _ = PlayFab.State; _ = PlayFab.Connected; }
        catch (Exception error) { throw new MissingMemberException("Native PlayFab connection/send readiness APIs are unavailable.", error); }
    }
    internal static bool CanSend(ZNetPeer peer)
    {
        if (!peer.IsReady() || peer.m_rpc == null) return false;
        var socket = peer.m_rpc.GetSocket();
        return socket != null && socket.IsConnected() &&
            (socket is not ZPlayFabSocket playFab || PlayFab.State(playFab) == PlayFab.Connected && !PlayFab.Resetting(playFab));
    }
}
