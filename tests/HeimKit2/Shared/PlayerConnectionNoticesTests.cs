using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HeimKit.Core.Players;

internal static class PlayerConnectionNoticesTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static ZNetPeer Peer(long id, string name)
    {
        var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer));
        peer.m_uid = id; peer.m_playerName = name; return peer;
    }
    public static void Run()
    {
        using var log = new ManualLogSource("Connection notice checks");
        var records = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, args) => records.Add((args.Level, LogCapture.Text(args.Data)));
        var notices = new PlayerConnectionNotices(log);
        IReadOnlyList<ZNetPeer>? peers = Array.Empty<ZNetPeer>();
        var presence = new PlayerPresence(() => peers, notices);
        try
        {
            DiagnosticLog.ServerActivity.Reset(); DiagnosticLog.ServerActivity.Observe(300, false);
            presence.Refresh();
            var first = Peer(12, "Trevor"); peers = new[] { first }; presence.Refresh();
            Check(records.Count == 0, "A handshake or empty startup announced character entry.");
            first.m_characterID = new ZDOID(12, 1); presence.Refresh(); presence.Refresh();
            Check(records.Count == 1 && records[0].Text == "PlayerPresence: Player \"Trevor\" (peer 12) connected.",
                "First character entry was unnamed, missing or repeated.");
            first.m_characterID = ZDOID.None; presence.Refresh();
            Check(records.Count == 1, "Death/respawn generated a connection change.");
            peers = null; presence.Refresh();
            peers = new[] { first }; presence.Refresh();
            Check(records.Count == 1, "Unavailable observations or recovery generated a connection change.");
            var second = Peer(13, "Trevor"); second.m_characterID = new ZDOID(13, 1);
            peers = new[] { first, second }; presence.Refresh();
            Check(records.Count == 2 && records[^1].Text.Contains("(peer 13) connected"), "A second player with the same name was lost.");
            first.m_playerName = ""; first.m_uid = 0;
            peers = new[] { second }; presence.Refresh(); presence.Refresh();
            Check(records.Count == 3 && records[^1].Text == "PlayerPresence: Player \"Trevor\" (peer 12) disconnected.",
                "Departure lost cached character identity or repeated.");
            var reconnect = Peer(12, "Line\nBreak\tName"); reconnect.m_characterID = new ZDOID(12, 2);
            peers = new[] { second, reconnect }; presence.Refresh();
            Check(records.Count == 4 && records[^1].Text.Contains("Line Break Name") && !records[^1].Text.Contains('\n') && !records[^1].Text.Contains('\t'),
                "Reconnect was lost or its name injected log controls.");
            reconnect.m_playerName = ""; presence.Refresh();
            peers = new[] { second }; presence.Refresh();
            Check(records.Count == 5 && records[^1].Text.Contains("Line Break Name"), "A blank later name erased the last known character name.");
            peers = null; presence.Refresh();
            peers = Array.Empty<ZNetPeer>(); presence.Refresh(); presence.Refresh();
            Check(records.Count == 6 && records[^1].Text.Contains("(peer 13) disconnected"), "Recovery missed a real departure while presence was unavailable.");
            var unnamed = Peer(14, ""); unnamed.m_characterID = new ZDOID(14, 1);
            peers = new[] { unnamed }; presence.Refresh();
            Check(records[^1].Text == "PlayerPresence: Peer 14 connected.", "Missing character name lost the peer fallback.");
            int before = records.Count; presence.Clear();
            Check(records.Count == before, "Shutdown fabricated a disconnect.");
            var longName = Peer(15, new string('x', 500));
            notices.Observe(new[] { longName }, new[] { longName });
            Check(records[^1].Text.Contains(new string('x', 128)) && !records[^1].Text.Contains(new string('x', 129)), "Character name length was unbounded.");
            longName.m_playerName = "Renamed\nPlayer";
            notices.Observe(new[] { longName }, new[] { longName });
            notices.Observe(Array.Empty<ZNetPeer>(), Array.Empty<ZNetPeer>());
            Check(records[^1].Text.Contains("Renamed Player") && records[^1].Text.Contains("disconnected"),
                "Name caching missed a later name change or lost sanitization.");
            Check(records.All(r => r.Level == LogLevel.Info), "Character notices depended on debugging or used incorrect severity.");
        }
        finally { presence.Clear(); DiagnosticLog.ServerActivity.Reset(); }
        System.Console.WriteLine("PASS: named character connection/departure, respawn and unknown-state stability, cached identities, reconnects and Info logging.");
    }
}
