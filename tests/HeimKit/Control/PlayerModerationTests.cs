using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HeimKit.Core.Control;
using HeimKit.Core.Control.Server;
using HeimKit.Core.Players;

public static class PlayerModerationTests
{
    public class AccountSocket : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(ISocket.GetHostName) ? "76561198000000001" : throw new NotSupportedException();
    }

    public static void Run()
    {
        using var log = new ManualLogSource("Moderation history");
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var history = new PlayerJoinHistory(directory, log);
            var notices = new PlayerConnectionNotices(log) { History = history };
            var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer));
            peer.m_uid = 12;
            peer.m_playerName = "Visitor";
            peer.m_socket = DispatchProxy.Create<ISocket, AccountSocket>();
            ZNetPeer[] peers = { peer };
            var presence = new PlayerPresence(() => peers, notices);
            presence.Refresh();
            if (history.Rows().Any()) throw new Exception("Connection attempt entered history.");

            // No identification, admin, control server or panel exists during the visit.
            peer.m_characterID = new ZDOID(12, 1);
            presence.Refresh();
            history.Flush(0);
            long joined = history.Revision;
            peer.m_characterID = ZDOID.None;
            presence.Refresh();
            peer.m_characterID = new ZDOID(12, 2);
            presence.Refresh();
            if (history.Revision != joined) throw new Exception("Respawn counted as a new visit.");
            peers = Array.Empty<ZNetPeer>();
            presence.Refresh();
            if (history.Rows().Single().Group != RosterGroup.Previous) throw new Exception("Departure missed.");

            var restarted = new PlayerJoinHistory(directory, log);
            restarted.Flush(1);
            var delivery = new ModerationDelivery();
            delivery.Send(false, UnexpectedSnapshot, UnexpectedPacket);

            var packets = new List<ZPackage>();
            var bytes = ModerationProtocol.Encode(restarted.Rows().ToArray());
            delivery.Send(true, () => bytes, packets.Add);
            var packet = packets.Single().GetArray();
            if (packet[0] != 3) throw new Exception("Incomplete first admin snapshot.");
            using var stream = new MemoryStream(packet[1..]);
            var visitor = ModerationProtocol.Decode(stream).Single();
            if (visitor.Group != RosterGroup.Previous || visitor.Name != "Visitor" ||
                visitor.Id != "Steam_76561198000000001") throw new Exception("Earlier vanilla account missing.");

            delivery.Send(false, UnexpectedSnapshot, UnexpectedPacket);
            delivery.Send(true, () => bytes, packets.Add);
            if (packets.Count != 2) throw new Exception("Reauthorization did not restore history.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static byte[] UnexpectedSnapshot() => throw new Exception("Non-admin requested private data.");
    private static void UnexpectedPacket(ZPackage packet) => throw new Exception("Non-admin received a packet.");
}
