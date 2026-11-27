using System.Runtime.CompilerServices;
using HeimKit.Core.Players;
using HeimKit.Core.World;
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
    private static void CrowdedPortalScope()
    {
        using var world = new ReviewRegressionTests.World();
        var piece = world.Add(1);
        var skeleton = world.Add(2);
        void Scan(Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO>? far)
        {
            for (int i = 0; i < 8192; i++) near.Add(piece);
            near.Add(skeleton);
        }
        using var provider = new WorldObjectProvider(new ReviewRegressionTests.Presence(), Scan, observe: false);
        provider.BeginCycle(true);
        var subscription = provider.For("TameTravel");
        var selected = subscription.Nearby(Vector3.zero, (z, _) => ReferenceEquals(z, skeleton));
        Check(selected?.Count == 1 && ReferenceEquals(selected[0], skeleton), "Crowded portal hid the last matching record.");
        Check(subscription.Nearby(Vector3.zero, (z, _) => ReferenceEquals(z, piece))?.Count == 8192 &&
            subscription.Nearby(Vector3.zero, static (_, _) => true) == null &&
            subscription.Nearby(Vector3.zero) == null, "Oversized context returned a partial view or changed the ordinary tame limit.");
    }

    internal static void Run()
    {
        CrowdedPortalScope();
        Check(TameTravelCommandRules.ExplicitNativeToggle(true, true) &&
            !TameTravelCommandRules.ExplicitNativeToggle(false, true) &&
            !TameTravelCommandRules.ExplicitNativeToggle(true, false) &&
            !TameTravelCommandRules.ExplicitNativeToggle(false, false),
            "A silent restore or a non-commandable tame could change ordinary portal consent.");
        int lox = "Lox".GetStableHashCode(), citadel = "Mistlands_DvergrBossEntrance1".GetStableHashCode();
        Check(!TameTravelTraversalRules.DungeonAllows(lox, citadel, true) &&
            TameTravelTraversalRules.DungeonAllows("Hen".GetStableHashCode(), citadel, true) &&
            TameTravelTraversalRules.DungeonAllows(lox, "TrollCave02".GetStableHashCode(), true),
            "Citadel entry restriction leaked into another animal or the Troll Cave clearance path.");
        Check(!TameTravelTraversalRules.NeedsFullDoorClearance(new Vector3(.98f, 1.85f, 1), .49f, 1.85f) &&
            TameTravelTraversalRules.NeedsFullDoorClearance(new Vector3(3, 3.5f, 4), .49f, 1.85f),
            "A player-sized body or the Lox capsule was assigned the wrong doorway clearance.");
        Check(TameTravelGuestSummons.CasterAtCreation(11, 11, 11, 11, 101, true, 3) == 101 &&
            TameTravelGuestSummons.CasterAtCreation(22, 22, 22, 22, 202, true, 1) == 202 &&
            TameTravelGuestSummons.CasterAtCreation(22, 11, 22, 22, 202, true, 3) == 0 &&
            TameTravelGuestSummons.CasterAtCreation(11, 11, 11, 22, 202, true, 3) == 0 &&
            TameTravelGuestSummons.CasterAtCreation(11, 11, 11, 11, 101, true, 0) == 0,
            "Guest attribution mixed players, accepted a later simulator or ignored the staff's native limit.");
        var exit = new Vector3(3, 5000, 4);
        Check(!TameTravelGuestSummons.WalkedFromExit(exit, exit, exit) &&
            !TameTravelGuestSummons.WalkedFromExit(exit, exit + Vector3.up, exit) &&
            !TameTravelGuestSummons.WalkedFromExit(exit, exit - Vector3.up * 5000, exit) &&
            !TameTravelGuestSummons.WalkedFromExit(exit + Vector3.right, exit + Vector3.right, exit) &&
            TameTravelGuestSummons.WalkedFromExit(exit, exit + Vector3.right, exit),
            "Guest summon arrival accepted a pinned, vertical, rolled-back or repeated sample, or rejected destination walking.");
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

        string? Surface(bool structural = true, bool isStatic = true, bool supports = true, int comfort = 0, float thickness = 1)
            => TameTravelPlacementSurfaces.SurfaceRejection(true, comfort, structural, isStatic, supports, new Vector3(2, thickness, 2));
        Check(Surface() == null && Surface(thickness: .5f) == null, "A low static ruin floor was rejected as support.");
        Check(Surface(isStatic: false) == "not_static" && Surface(structural: false) == "not_structural" &&
            Surface(supports: false) == "not_supporting" && Surface(comfort: 1) == "furniture" &&
            Surface(thickness: 2) == "too_thick",
            "Accepting ruin floors weakened static, structural, furniture, or thickness guards.");
        System.Console.WriteLine("PASS: static low ruin floors qualify while furniture, mobile/non-supporting pieces, and thick walls remain excluded.");

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
