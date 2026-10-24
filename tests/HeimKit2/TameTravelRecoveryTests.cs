using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HeimKit.Features.TameTravel;
using UnityEngine;

internal static class TameTravelRecoveryTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run() { ReplicationOrdering(); RecoveryBounds(); PlacementRoutes(); CourtyardPlacement(); PlacementEvidence(); ContinuousFollowing(); PortalPairs(); }

    private static void PortalPairs()
    {
        Check(TameTravelPortalRules.PairRejection(true, true, null, true, "", "", 400, false) == null &&
            TameTravelPortalRules.PairRejection(true, true, null, true, "Home", "Home", 100000, false) == null,
            "A reciprocal native blank-tag or named pair was rejected.");
        Check(TameTravelPortalRules.PairRejection(false, false, "record_missing", false, "", "", float.NaN, false) == "source_unlinked" &&
            TameTravelPortalRules.PairRejection(true, false, "record_missing", false, "", "", float.NaN, false) == "destination_record_missing" &&
            TameTravelPortalRules.PairRejection(true, true, "scale_unsupported", true, "", "", 400, false) == "destination_scale_unsupported" &&
            TameTravelPortalRules.PairRejection(true, true, null, false, "", "", 400, false) == "destination_link_not_reciprocal" &&
            TameTravelPortalRules.PairRejection(true, true, null, true, "A", "B", 400, false) == "portal_tag_mismatch" &&
            TameTravelPortalRules.PairRejection(true, true, null, true, "", "", 399, false) == "portal_separation_invalid" &&
            TameTravelPortalRules.PairRejection(true, true, null, true, "", "", 400, true) == "portals_disabled",
            "Pair diagnostics lost an exact validation reason or weakened a required guard.");
        Check(TameTravelPortalRules.WitnessValid(6) && !TameTravelPortalRules.WitnessValid(6.01f) &&
            !TameTravelPortalRules.WitnessValid(-1) && !TameTravelPortalRules.WitnessValid(float.NaN) &&
            TameTravelPortalRules.CanWait("source_unlinked") && TameTravelPortalRules.CanWait("destination_record_missing") &&
            TameTravelPortalRules.CanWait("destination_link_not_reciprocal") && !TameTravelPortalRules.CanWait("portal_tag_mismatch") &&
            !TameTravelPortalRules.CanWait("destination_scale_unsupported"), "Source evidence became unbounded or a permanent mismatch was treated as a transient link wait.");
        System.Console.WriteLine("PASS: native reciprocal blank/named portal tags are accepted, all pair guards retain exact reasons, and retained source evidence has a finite six-second lifetime limited to transient link conditions.");
    }

    private static void ContinuousFollowing()
    {
        Check(!TameTravelCommandRules.ConsentExpired(HeimKit.Features.TameTravel.Models.TameOrder.Follow, 3600, 300) &&
            TameTravelCommandRules.ConsentExpired(HeimKit.Features.TameTravel.Models.TameOrder.Wait, 301, 300) &&
            !TameTravelCommandRules.ConsentExpired(HeimKit.Features.TameTravel.Models.TameOrder.Wait, 300, 300) &&
            TameTravelCommandRules.ConsentExpired(HeimKit.Features.TameTravel.Models.TameOrder.Follow, float.NaN, 300),
            "Continuous following expired like a single-trip wait, or invalid clock evidence was accepted.");
        var commands = new TameTravelCommands(null!, null!, null!, _ => false, (_, _, _) => { }, null!);
        var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer));
        var replacement = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer));
        peer.m_uid = replacement.m_uid = 91;
        var character = new ZDOID(91, 1); var animal = new ZDOID(91, 2);
        var binding = new HeimKit.Features.TameTravel.Models.FollowBinding { Peer = peer, Character = character, Animal = animal,
            Name = "Traveller", Order = HeimKit.Features.TameTravel.Models.TameOrder.Follow };
        commands.Bindings.Add(animal, binding);
        Check(commands.FollowingAuthorized(animal, peer, character, "Traveller") &&
            !commands.FollowingAuthorized(animal, replacement, character, "Traveller") &&
            !commands.FollowingAuthorized(animal, peer, new ZDOID(91, 3), "Traveller") &&
            !commands.FollowingAuthorized(animal, peer, character, "Someone else"),
            "Persistent follow authority crossed a physical peer, character or name boundary.");
        binding.Order = HeimKit.Features.TameTravel.Models.TameOrder.Stay; binding.Pending = true; binding.CommandSent = false;
        Check(!commands.FollowingAuthorized(animal, peer, character, "Traveller"), "A queued stop retained portal authority.");
        commands.Bindings.Clear();
        Check(!commands.FollowingAuthorized(animal, peer, character, "Traveller"), "A cleared session retained follow permission.");
        Check(!TameTravelTraversalRules.PhaseExpired(1, false, 25) && TameTravelTraversalRules.PhaseExpired(0, false, 10.1f) &&
            !TameTravelTraversalRules.PhaseExpired(1, true, 30) && TameTravelTraversalRules.PhaseExpired(1, true, 30.1f) &&
            TameTravelTraversalRules.PhaseExpired(3, false, 10.1f), "Early source draining consumed the placement window or extended ownership handshake limits.");
        System.Console.WriteLine("PASS: following survives the wait-consent lifetime, remains scoped to one physical player session, ends on queued stop/session cleanup, and separates early source draining from bounded destination placement.");
    }

    private static void PlacementEvidence()
    {
        HeimKit.Core.Diagnostics.DiagnosticLog.StartSession();
        using var log = new BepInEx.Logging.ManualLogSource("Placement evidence checks");
        var lines = new List<string>(); log.LogEvent += (_, args) => lines.Add(LogCapture.Text(args.Data));
        var config = new BepInEx.Configuration.ConfigFile(Path.Combine(Path.GetTempPath(), "heimkit-placement-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var debug = HeimKit.Configuration.DiagnosticsSettings.Bind(config); debug.Value = true;
        var diagnostics = new TameTravelDiagnostics(log, new HeimKit.Configuration.DebugLoggingPolicy(debug, false), () => 1);
        var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); peer.m_uid = 91; peer.m_playerName = "Traveller";
        for (int i = 0; i < 20; i++) diagnostics.Traversal(peer, "placement", "gate_" + i);
        Check(lines.Count == 6, "Placement gates exceeded their reserved allowance.");
        var transfer = new HeimKit.Features.TameTravel.Models.AnimalTransfer {
            Journey = new HeimKit.Features.TameTravel.Models.PortalJourney { Name = "Traveller" },
            PlacementAttempts = 12, PlacementCandidates = 768, PlacementContextMilliseconds = 3, PlacementSearchMilliseconds = 4 };
        transfer.PlacementRejections["solid_overlap"] = 42;
        diagnostics.Transfer(transfer, false, null);
        Check(lines.Count == 7 && lines[^1].Contains("768 candidates") && lines[^1].Contains("context_ms=3.00") && lines[^1].Contains("search_ms=4.00") && !lines[^1].Contains("rejections="),
            "Normal endings lost the compact outcome or built Verbose placement fields.");
        for (int i = 0; i < 20; i++) diagnostics.Transfer(transfer, false, null);
        Check(lines.Count == 14, "Transaction endings exceeded their independent allowance.");
        debug.Value = false; diagnostics.Transfer(transfer, false, null);
        Check(lines.Count == 14, "Suppressed placement evidence still emitted output.");
        System.Console.WriteLine("PASS: placement outcomes retain rejection and timing totals after gate-budget exhaustion, with independent bounded endings and debug suppression.");
    }

    private static void ReplicationOrdering()
    {
        ZdoReplication.CheckCompatibility();
        var type = typeof(ZDOMan).GetNestedType("ZDOPeer", BindingFlags.NonPublic)!;
        var native = RuntimeHelpers.GetUninitializedObject(type);
        var id = new ZDOID(51, 1); var other = new ZDOID(51, 2);
        var invalid = new HashSet<ZDOID> { id, other };
        var forced = new HashSet<ZDOID> { id, other };
        var knownField = type.GetField("m_zdos")!;
        var known = (IDictionary)Activator.CreateInstance(knownField.FieldType)!;
        type.GetField("m_invalidSector")!.SetValue(native, invalid);
        type.GetField("m_forceSend")!.SetValue(native, forced);
        knownField.SetValue(native, known);
        var packet = new ZPackage(); packet.Write("relocated snapshot");

        bool failed = false;
        try { ZdoReplication.Transmit(native, id, 20, 4, 10, packet, _ => throw new IOException("Disconnected")); }
        catch (IOException) { failed = true; }
        Check(failed && invalid.SetEquals(new[] { id, other }) && forced.SetEquals(new[] { id, other }) && known.Count == 0,
            "A failed send consumed native invalidation/force entries or acknowledged unsent data.");

        // The traveller receives the relocated record first. A later queued
        // invalidation would de-index it, and native equal-revision rejection
        // would not put it back. Exercise production queue/acknowledgement work.
        string clientPosition = "source"; bool clientIndexed = true; uint clientRevision = 10;
        ZdoReplication.Transmit(native, id, 20, 4, 10, packet, _ =>
        {
            Check(invalid.Contains(id), "Bookkeeping changed before the reliable send succeeded.");
            clientPosition = "destination"; clientRevision = 20; clientIndexed = true;
        });
        if (invalid.Contains(id)) clientIndexed = false;
        uint resend = 20;
        if (resend > clientRevision) { clientPosition = "destination"; clientIndexed = true; }
        Check(clientPosition == "destination" && clientIndexed && !invalid.Contains(id) && invalid.Contains(other) &&
            !forced.Contains(id) && forced.Contains(other), "Late native invalidation hid the traveller's relocated record or affected another ID.");
        object info = known[id]!;
        Check((uint)info.GetType().GetField("m_dataRevision")!.GetValue(info)! == 20 &&
            (ushort)info.GetType().GetField("m_ownerRevision")!.GetValue(info)! == 4, "Native per-peer sent revisions diverged from the packet.");

        // Ownership-only source drains must retain wire revision zero. Do not
        // accidentally acknowledge the server's full revision for that packet.
        ZdoReplication.Transmit(native, id, 0, 5, 11, packet, _ => { });
        info = known[id]!;
        Check((uint)info.GetType().GetField("m_dataRevision")!.GetValue(info)! == 0, "Source-drain zero revision was discarded.");
        var record = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO)); record.m_uid = id; record.DataRevision = 20; record.OwnerRevision = 5;
        Check((bool)type.GetMethod("ShouldSend")!.Invoke(native, new object[] { record })!, "Zero-revision source drain prevented native full-state reconciliation.");
        System.Console.WriteLine("PASS: targeted animal snapshots preserve native wire/source-drain revisions, consume only their queued invalidation/force entries after successful sending, and prevent the late-invalidation visibility race in the native-ordering model.");
    }

    private static void RecoveryBounds()
    {
        foreach (float distance in new[] { 8.07f, 8.10f, 16f, 32f, 48f })
        {
            Check(TameTravelTraversalRules.DestinationRejection(distance * distance, 20, 30) == null,
                "Moving clear of the portal cancelled a validated arrival within the destination area.");
            Check(TameTravelTraversalRules.DestinationRejection(distance * distance, 20, 30, relocated: true) == null,
                "An already-moved animal lost recovery when the traveller stepped farther away.");
        }
        Check(TameTravelTraversalRules.DestinationRejection(48.01f * 48.01f, 20, 30, true) == "destination_distance_exceeded" &&
            TameTravelTraversalRules.DestinationRejection(48.01f * 48.01f, 20, 30) == "destination_distance_exceeded" &&
            TameTravelTraversalRules.DestinationRejection(0, 60.01f, 30, true) == "traversal_timeout" &&
            TameTravelTraversalRules.DestinationRejection(float.NaN, 20, 30, true) != null,
            "Recovery lost its distance/time bounds or accepted an unknown player position.");
        System.Console.WriteLine("PASS: validated destination travel tolerates movement through 48 metres before and after relocation, while enforcing finite positions, distance and the overall journey timeout.");
    }

    private static void CourtyardPlacement()
    {
        var start = Vector3.zero; var end = new Vector3(0, 0, 8);
        var direct = new[] { start, end };
        Check(!TameTravelPlacementAccess.Validate(start, end, direct, (_, _) => false, out var reason) && reason == "access_wall_collision",
            "A stale navigation path crossed a physical wall.");
        var aroundTable = new[] { start, new Vector3(3, 0, 3), new Vector3(3, 0, 6), end };
        Check(TameTravelPlacementAccess.Validate(start, end, aroundTable,
            (a, b) => !(a.x == 0 && b.x == 0 && a.z < 4 && b.z > 4), out _), "A short route around a table was rejected.");
        Check(TameTravelPlacementAccess.Validate(start, end, new[] { start, new Vector3(0, 0, 3), end }, (_, _) => true, out _),
            "A player-sized open doorway route was rejected.");
        Check(!TameTravelPlacementAccess.Validate(start, end, new[] { new Vector3(3, 0, 0), end }, (_, _) => true, out _),
            "Native navigation snapped the arrival position across a wall.");
        Check(!TameTravelPlacementAccess.Validate(start, end, new[] { start, new Vector3(30, 0, 4), end }, (_, _) => true, out _),
            "A distant route around a castle wall authorized the other side.");
        Check(!TameTravelPlacementAccess.Validate(start, end, new[] { start, new Vector3(float.NaN, 0, 4), end }, (_, _) => true, out _),
            "An invalid navigation corner authorized placement.");
        Check(TameTravelPlacementSurfaces.LowSlab(new Vector3(2, .5f, 2), 1) &&
            !TameTravelPlacementSurfaces.LowSlab(new Vector3(.3f, 2, 2), 1) &&
            !TameTravelPlacementSurfaces.LowSlab(new Vector3(2, .5f, 2), 4), "Low paving geometry was confused with walls or raised construction.");
        // A narrow landing opening between sparse old ring angles must be visited locally.
        var local = Enumerable.Range(0, TameTravelPlacementRules.PreferredCandidates)
            .Select(i => TameTravelPlacementRules.Candidate(i, 3, Vector3.zero, Vector3.zero, Vector3.forward, 17)).ToArray();
        var opening = new Vector3(2.2f, 0, 7.5f);
        Check(local.Any(p => Vector3.Distance(p, opening) < .5f) && local.All(p => p.magnitude < 15),
            "Dense local search missed a half-metre opening or searched far ground first.");
        System.Console.WriteLine("PASS: courtyard placement validates table detours and doorway routes, rejects wall crossings, distant detours and navigation snapping, and densely samples nearby paving.");
    }
    private static void PlacementRoutes()
    {
        foreach (var forward in new[] { Vector3.forward, Vector3.right, -Vector3.forward, -Vector3.right })
        {
            var portal = new Vector3(100, 12, 200);
            var right = new Vector3(forward.z, 0, -forward.x);
            var exit = portal + forward * 3;
            var traveller = portal - forward * 18 + right * 6;
            int count = TameTravelPlacementRules.CandidateCount(3);
            var candidates = Enumerable.Range(0, count).Select(i => TameTravelPlacementRules.Candidate(i, 3, exit, traveller, forward, 17)).ToArray();
            Check(candidates.Take(TameTravelPlacementRules.PreferredCandidates).All(p => TameTravelPlacementRules.InFront(p, portal, forward, 4)),
                "A preferred landing was behind the rotated portal or lacked body clearance.");
            var firstPoints = Enumerable.Range(0, 8).Select(seed => TameTravelPlacementRules.Candidate(0, 3, exit, traveller, forward, seed)).ToArray();
            Check(firstPoints.Distinct().Count() == 8 && firstPoints.Any(p => Vector3.Dot(p - exit, right) < 0) &&
                firstPoints.Any(p => Vector3.Dot(p - exit, right) > 0), "Trip variation did not use both sides of the portal.");
            Check(TameTravelPlacementRules.Candidate(0, 3, exit, traveller, forward, 17) == candidates[0],
                "Candidate variation changed while retrying the same trip.");
            foreach (var target in new[] { portal + forward * 24 + right * 24, portal + forward * 9 - right * 24, portal + forward * 18 + right * 6 })
            {
                bool found = false; int cursor = 0, passes = 0;
                while (cursor < count && !found)
                {
                    int budget = Math.Min(TameTravelPlacementRules.MaximumCandidatesPerPass, count - cursor);
                    for (int i = 0; i < budget; i++)
                        if (candidates[cursor + i] == target && TameTravelPlacementRules.InFront(target, portal, forward, 4)) found = true;
                    cursor += budget; passes++;
                }
                Check(found && passes <= 120, "Front-side building/traveller fallbacks lost coverage within the placement window.");
            }
            var rear = portal - forward * 24;
            Check(!TameTravelPlacementRules.InFront(rear, portal, forward, 4) &&
                !TameTravelPlacementRules.InFront(portal + forward * 3, portal, forward, 4) &&
                TameTravelPlacementRules.InFront(portal + forward * 4, portal, forward, 4),
                "Rear/straddling placement was accepted or exact forward clearance was rejected.");
            Check(!candidates.Any(p => p == rear && TameTravelPlacementRules.InFront(p, portal, forward, 4)),
                "A rear-only opening remained an eligible landing fallback.");
        }
        Check(!TameTravelPlacementRules.InFront(new Vector3(float.NaN, 0, 0), Vector3.zero, Vector3.forward, 1) &&
            !TameTravelPlacementRules.InFront(Vector3.forward, Vector3.zero, Vector3.zero, 1), "Unknown placement/facing passed the front gate.");
        var slope = new Vector3(-.3f, 1, 0).normalized;
        Check(TameTravelPlacementRules.SupportedFootprint(10, 10.9f, new Vector3(3, 0, 0), slope) &&
            !TameTravelPlacementRules.SupportedFootprint(10, 12, new Vector3(3, 0, 0), slope),
            "Steady slopes are confused with unsupported ledges in the animal footprint.");
        Vector3? Flat(Vector3 point) => new Vector3(point.x, 0, point.z);
        int probes = 0, sweeps = 0;
        Check(TameTravelPlacementRules.HasEscape(Vector3.zero, Vector3.forward, 5, Flat,
            (a, b) => { sweeps++; return Math.Abs(b.x) > .01f; }, ref probes) && sweeps > 1,
            "A blocked straight corridor did not try a clear side escape.");
        probes = 0;
        Check(!TameTravelPlacementRules.HasEscape(Vector3.zero, Vector3.forward, 5, Flat, (_, _) => false, ref probes),
            "A clear landing inside a fully obstructed escape corridor was accepted.");
        probes = 0;
        Check(!TameTravelPlacementRules.HasEscape(Vector3.zero, Vector3.forward, 5,
            p => new Vector3(p.x, 4, p.z), (_, _) => true, ref probes), "A sharp drop/step next to otherwise clear landing was accepted.");
        probes = TameTravelPlacementRules.MaximumEscapeProbes - 1;
        Check(!TameTravelPlacementRules.HasEscape(Vector3.zero, Vector3.forward, 5, Flat, (_, _) => true, ref probes) &&
            probes == TameTravelPlacementRules.MaximumEscapeProbes, "Escape-route work exceeded its per-search budget.");
        probes = 0;
        Check(TameTravelPlacementRules.HasEscape(Vector3.zero, Vector3.forward, 5, Flat, (_, _) => true, ref probes), "An open flat walkout was rejected.");
        System.Console.WriteLine("PASS: portal-oriented candidates vary left/right, retain forward building/traveller fallbacks and reject rear/straddling landings across four portal rotations; footprint checks accept steady slopes and reject ledges; escape checks reject blocked routes and enforce the shared probe budget.");
    }
}
