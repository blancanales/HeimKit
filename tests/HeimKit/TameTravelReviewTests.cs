using System.Runtime.CompilerServices;
using HeimKit.Core.Players;
using HeimKit.Features.TameTravel;
using UnityEngine;

internal static class TameTravelReviewTests
{
    private sealed class Presence : IPlayerPresence
    {
        internal readonly List<ZNetPeer> Roster = new();
        internal bool Available = true;
        public int? ConnectedCount => Available ? Roster.Count : null;
        public int? ReadyCount => 0;
        public int? JoinedCount => 0;
        public bool? HasConnectedPlayers => Available ? Roster.Count > 0 : null;
        public bool? HasJoinedPlayers => false;
        public IReadOnlyList<ZNetPeer> Peers => Roster;
        public void Refresh() { }
        public void Clear() => Roster.Clear();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run()
    {
        var presence = new Presence();
        Check(!TameTravelRules.ConnectedName(presence, "Bjorn Toulose"), "An absent saved follower was treated as online.");
        var oldOwner = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); oldOwner.m_playerName = "Bjorn Toulose";
        presence.Roster.Add(oldOwner);
        Check(TameTravelRules.ConnectedName(presence, "Bjorn Toulose"), "A loading/respawning transport peer lost follower protection.");
        Check(!TameTravelRules.ConnectedName(presence, "Astra"), "One connected player blocked unrelated saved names.");
        presence.Roster.Clear(); presence.Available = false;
        Check(TameTravelRules.ConnectedName(presence, "Bjorn Toulose"), "Unknown presence authorized a follower takeover.");
        Check(TameTravelCommandRules.StopAssociated("Bjorn Toulose", "Astra", true, "Bjorn Toulose", true) &&
            !TameTravelCommandRules.StopAssociated("Bjorn Toulose", "Astra", false, "Bjorn Toulose", true) &&
            !TameTravelCommandRules.StopAssociated("Other", "Astra", true, "Bjorn Toulose", true) &&
            !TameTravelCommandRules.StopAssociated("Bjorn Toulose", "Astra", true, "Bjorn Toulose", false),
            "Stay could not preempt its own pending reclamation, or gained authority over someone else's animal.");
        System.Console.WriteLine("PASS: absent follower names can be reclaimed, loading/respawning players remain protected, unknown presence refuses takeover, and stay preempts only its own pending reclaim.");

        string? Surface(bool structural = true, bool isStatic = true, bool supports = true, int comfort = 0, float thickness = 1, float height = .5f)
            => TameTravelPlacementSurfaces.SurfaceRejection(true, comfort, structural, isStatic, supports, new Vector3(2, thickness, 2), height);
        Check(Surface() == null && Surface(thickness: .5f, height: 2) == null, "A low static ruin floor was rejected as support.");
        Check(Surface(isStatic: false) == "not_static" && Surface(structural: false) == "not_structural" &&
            Surface(supports: false) == "not_supporting" && Surface(comfort: 1) == "furniture" &&
            Surface(thickness: 2) == "too_thick" && Surface(height: 3.1f) == "raised_construction",
            "Accepting ruin floors weakened static, structural, furniture, thickness or height guards.");
        System.Console.WriteLine("PASS: static low ruin floors qualify while furniture, mobile/non-supporting pieces, thick walls and raised construction remain excluded.");

        Check(TameTravelDiagnostics.CommandMessage("Astra", "player_data_applied", "", null) == "Observed fresh player data for Astra." &&
            !TameTravelCommandRules.Emote("", out _) && !TameTravelCommandRules.Emote("sit", out _),
            "An emote end or unrelated state update was presented as an animal command.");
        Check(TameTravelDiagnostics.CommandMessage("Astra", "stop_queued", "Stay", "ownership_and_stop_pending").StartsWith("Queued Astra's Stay command") &&
            TameTravelDiagnostics.CommandMessage("Astra", "reclaim_queued", "Follow", "saved_follower_offline").StartsWith("Queued Astra's Follow command") &&
            TameTravelDiagnostics.CommandMessage("Astra", "rejected", "Stay", "no_associated_animals").Contains("Ignored"),
            "Pending cleanup was described as rejection or actual rejection lost its identity.");
        System.Console.WriteLine("PASS: emote ends and fresh data are not blank commands, queued stops/reclaims are identified as pending and real command rejections remain distinct.");
    }
}
