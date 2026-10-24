using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.ForestRenewal;
using HeimKit.Features.ForestRenewal.Models;
using HeimKit.Features.HeimMender;
using HeimKit.Features.SeaSalvage;
using HeimKit.Features.SeaSalvage.Models;
using HeimKit.Features.TameTravel;
using HeimKit.Features.TameTravel.Models;
using UnityEngine;

internal static class WorldAdditionsTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        Packets(); Inventory(); Ledger(); RenewalCommit(); Reload(); NativeMembers(); Timings(); AuthorityTargets(); TameCommands(); CommandEvidence(); FollowSpacing(); TransferDiagnostics();
        NativeHealth(); TerrainReadiness(); StructureClearance(); RenewalEvidence();
        TraversalHistory(); TraversalEvidence();
    }
    private static void TraversalHistory()
    {
        var history = new TameTravelPositionHistory();
        var box = new Bounds(Vector3.zero, new Vector3(2, 4, 2));
        var before = new Vector3(-3, 0, 0); float previous = 1;
        history.Add(new PositionObservation(Vector3.zero, 1.05f, 10));
        history.Add(new PositionObservation(new Vector3(3, 0, 0), 1.1f, 11));
        history.Add(new PositionObservation(new Vector3(1000, 0, 0), 1.2f, 12));
        history.Add(new PositionObservation(new Vector3(9000, 0, 0), 1.3f, 11));
        bool witnessed = false; int read = 0, jumps = 0;
        while (history.Take(out var observation))
        {
            string? rejected = TameTravelTraversalRules.SourceRejection(1.25f - observation.Time, observation.Time - previous, (observation.Position - before).sqrMagnitude);
            if (rejected == null && TameTravelTraversalRules.Touches(box, before, observation.Position)) witnessed = true;
            if (rejected == "jump_without_trigger_witness") jumps++;
            before = observation.Position; previous = observation.Time; read++;
        }
        Check(witnessed && read == 3 && jumps == 1, "Between-pass trigger history was lost, stale revision accepted, or a jump alone authorized source entry.");
        Check(TameTravelTraversalRules.Touches(box, Vector3.zero, Vector3.zero) &&
            TameTravelTraversalRules.Touches(box, new Vector3(-3, 0, 0), new Vector3(3, 0, 0)) &&
            !TameTravelTraversalRules.Touches(box, Vector3.zero, new Vector3(3, 0, 0)) &&
            !TameTravelTraversalRules.Touches(box, new Vector3(-3, 10, 0), new Vector3(3, 10, 0)),
            "Inside-trigger observations, bounded pass-through, leaving-only or wrong-height geometry was misclassified.");
        Check(TameTravelTraversalRules.SourceRejection(3, 1, 1) == "stale_position_observation" &&
            TameTravelTraversalRules.SourceRejection(0, 3, 1) == "source_sample_gap" &&
            TameTravelTraversalRules.SourceRejection(0, 1, 145) == "jump_without_trigger_witness" &&
            TameTravelTraversalRules.SourceRejection(0, 5, 145) == "jump_without_trigger_witness" &&
            TameTravelTraversalRules.SourceRejection(float.NaN, 1, 1) != null, "Stale/nonfinite source evidence was accepted.");
        for (uint i = 13; i < 13 + TameTravelPositionHistory.Capacity + 1; i++) history.Add(new PositionObservation(Vector3.zero, i, i));
        Check(history.Overflowed && history.Count == 1, "Overflow did not discard incomplete position history within its capacity.");
        history.AcknowledgeOverflow(); Check(!history.Overflowed, "History overflow could not be acknowledged.");
        Check(TameTravelTraversalRules.DestinationRejection(64, 11, 30) == null &&
            TameTravelTraversalRules.DestinationRejection(2305, 11, 30) == "destination_distance_exceeded" &&
            TameTravelTraversalRules.DestinationRejection(1, 61, 30) == "traversal_timeout", "Distance and traversal timeout were conflated or the distance boundary changed.");
        Check(TameTravelTraversalRules.ArrivalRejection(true, 1.8f, 1000000) == "arrival_too_early" &&
            TameTravelTraversalRules.ArrivalRejection(true, 2, 1000000) == null &&
            TameTravelTraversalRules.ArrivalRejection(false, 3, 1000000) == "movement_outside_linked_endpoints" &&
            TameTravelTraversalRules.ArrivalRejection(false, 3, 1) == null,
            "A late source witness made an early matching arrival cancel as unrelated movement.");
        System.Console.WriteLine("PASS: accepted position history retains between-pass trigger observations, rejects replay/jump/stale evidence, handles inside/height boundaries, bounds overflow and separates destination distance from timeout.");
    }
    private static void TraversalEvidence()
    {
        DiagnosticLog.StartSession();
        using var log = new ManualLogSource("Traversal evidence checks");
        var lines = new List<string>(); log.LogEvent += (_, args) => lines.Add(LogCapture.Text(args.Data));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "heimkit-traversal-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var debug = config.Bind("Diagnostics", "DebugMode", true); float now = 1;
        var diagnostics = new TameTravelDiagnostics(log, new HeimKit.Configuration.DebugLoggingPolicy(debug, false), () => now);
        var peer = (ZNetPeer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); peer.m_uid = 123; peer.m_playerName = "Trevor";
        for (int i = 0; i < 30; i++) diagnostics.Traversal(peer, "source_entry", "outside_trigger", new ZDOID(456, 1));
        Check(lines.Count == 1 && lines[0].Contains("Portal travel for Trevor: outside trigger.") && lines[0].Contains("source=456:1"), "Repeated source misses flooded or lost actor/source identity.");
        diagnostics.Traversal(peer, "source_entry", "portal_pair_unavailable", new ZDOID(456, 1));
        diagnostics.Traversal(peer, "source_entry", "outside_trigger", new ZDOID(456, 1));
        Check(lines.Count == 2, "A pair failure was hidden or routine source polling bypassed its separate interval.");
        diagnostics.Traversal(peer, "consent_ended", "animal_left_wait_area", animal: new ZDOID(456, 2), transition: true);
        var journey = new PortalJourney { Peer = peer, Name = "Trevor", Entered = 0, Arrived = 3, Exit = Vector3.zero };
        diagnostics.Cancel(journey, "destination_distance_exceeded", new Vector3(9, 0, 0), 11);
        Check(lines.Count == 4 && lines[^1].Contains("distance_m=9.0") && lines[^1].Contains("entry_age_s=11"), "Cancellation distance/timing or consent-loss evidence was lost.");
        for (int i = 0; i < 30; i++) diagnostics.Traversal(peer, "arrival", "linked_exit_observed", transition: true);
        Check(lines.Count == 7, "Critical traversal evidence exceeded six records or routine source polling consumed its allowance.");
        diagnostics.Command(peer, "request_received", "Wait"); Check(lines.Count == 8, "Traversal events consumed the command budget.");
        now = 301; diagnostics.Traversal(peer, "source_entry", "outside_trigger", new ZDOID(456, 1));
        Check(lines.Count == 9, "Unchanged source-gate heartbeat was lost.");
        debug.Value = false; diagnostics.Traversal(peer, "source_entry", "no_portal_nearby"); Check(lines.Count == 9, "Suppressed traversal evidence emitted output.");
        debug.Value = true; HeimKit.Configuration.DiagnosticsSettings.Bind(config);
        config.Bind("Diagnostics", "DebugVerbosity", HeimKit.Configuration.DebugVerbosity.Normal).Value = HeimKit.Configuration.DebugVerbosity.Verbose;
        try
        {
            lines.Clear();
            new TameTravelDiagnostics(log, new HeimKit.Configuration.DebugLoggingPolicy(debug, false), () => now)
                .Traversal(peer, "source_entry", "no_portal_nearby", fields: new (string, object?)[] { ("pos", new Vector3(1, 2, 3)), ("followers", 1) });
            Check(lines.Count == 2 && lines[0].Contains("No portal near Trevor; travel not started.") &&
                lines.All(line => !line.Contains("_now=")) && lines[1].Contains("pos=1.0,2.0,3.0 followers=1"),
                "Traversal supporting evidence retained retired field suffixes or lost typed positions.");
        }
        finally { config.Bind("Diagnostics", "DebugVerbosity", HeimKit.Configuration.DebugVerbosity.Normal).Value = HeimKit.Configuration.DebugVerbosity.Normal; }
        System.Console.WriteLine("PASS: traversal evidence preserves actor/object identity, distance and time, combines repeated misses, retains consent transitions and has an independent bounded stream with suppression.");
    }
    private static void NativeHealth()
    {
        var record = (ZDO)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ZDO));
        record.m_uid = new ZDOID(987654321, 87654);
        try
        {
            Check(!record.GetFloat(ZDOVars.s_health, out _) && NativeCharacterHealth.IsAlive(record), "Native omitted full health was rejected.");
            Check(!record.GetFloat(ZDOVars.s_health, out _), "Eligibility manufactured saved health.");
            foreach (float value in new[] { 1f, 1234f, 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                ZDOExtraData.Set(record.m_uid, ZDOVars.s_health, value);
                Check(NativeCharacterHealth.IsAlive(record) == (float.IsFinite(value) && value > 0), "Explicit saved health was misclassified: " + value);
            }
            ZDOExtraData.RemoveFloat(record.m_uid, ZDOVars.s_health);
            ZDOExtraData.Set(record.m_uid, ZDOVars.s_dead, 1);
            Check(!NativeCharacterHealth.IsAlive(record), "Dead record with omitted health was accepted.");
        }
        finally { ZDOExtraData.RemoveFloat(record.m_uid, ZDOVars.s_health); ZDOExtraData.RemoveInt(record.m_uid, ZDOVars.s_dead); }
        System.Console.WriteLine("PASS: native ZDO health omission is accepted without mutation; explicit nonfinite/nonpositive health and death remain excluded.");
    }
    private static void TerrainReadiness()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var compiler = (TerrainComp)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TerrainComp));
        Check(!TerrainContext.CompilerInitialized(compiler), "An early-return terrain compiler was considered initialized.");
        typeof(TerrainComp).GetField("m_initialized", flags)!.SetValue(compiler, true);
        Check(!TerrainContext.CompilerInitialized(compiler), "A compiler without its map and arrays was considered initialized.");
        typeof(TerrainComp).GetField("m_hmap", flags)!.SetValue(compiler, System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Heightmap)));
        foreach (string field in new[] { "m_modifiedHeight", "m_levelDelta", "m_smoothDelta", "m_modifiedPaint", "m_paintMask" })
        {
            var info = typeof(TerrainComp).GetField(field, flags)!;
            info.SetValue(compiler, Array.CreateInstance(info.FieldType.GetElementType()!, 1));
        }
        Check(TerrainContext.CompilerInitialized(compiler), "Initialized native compiler state was rejected.");
        typeof(TerrainComp).GetField("m_modifiedHeight", flags)!.SetValue(compiler, Array.Empty<bool>());
        Check(!TerrainContext.CompilerInitialized(compiler), "Empty terrain arrays passed readiness.");
        Check(typeof(TerrainComp).GetMethod("Awake", flags) != null, "Native terrain initialization entry point is unavailable.");
        System.Console.WriteLine("PASS: terrain readiness rejects missing/empty compiler state before load and resolves the native initialization path; Unity initialization itself is not executed.");
    }
    private static void StructureClearance()
    {
        // Every detailed footprint point inside the exclusion must survive the
        // cheap rejection, including offset, rotation, scale and elevated pieces.
        var bounds = new Bounds(new Vector3(4, 1, -3), new Vector3(12, 4, 2));
        var scale = new Vector3(2, .5f, 3);
        var origin = new Vector3(20, 40, -10);
        foreach (var rotation in new[] { new Quaternion(0, 0, 0, 1), new Quaternion(0, .70710677f, 0, .70710677f), new Quaternion(.2f, .4f, .1f, 1.5f) })
        {
            var world = new Bounds(origin + rotation * Vector3.Scale(bounds.center, scale), Vector3.zero);
            for (int i = 0; i < 8; i++) world.Encapsulate(origin + rotation * Vector3.Scale(PrefabCollisionBounds.Corner(bounds, i), scale));
            for (int i = 0; i < 8; i++)
            {
                var corner = PrefabCollisionBounds.Corner(world, i); corner.y = 0;
                Check(ForestRenewalStructures.CanReach(bounds, scale, rotation, origin, corner + new Vector3(9, 0, 0), 10), "Broad phase discarded a blocking structure AABB corner.");
            }
        }
        Check(!ForestRenewalStructures.CanReach(bounds, scale, new Quaternion(0, 0, 0, 1), origin, new Vector3(500, 0, 500), 10), "Distant known geometry was not culled.");
        System.Console.WriteLine("PASS: structure broad phase retains offset/scaled/rotated and height-separated exclusion geometry while rejecting distant pieces.");
    }
    private static void RenewalEvidence()
    {
        using var log = new ManualLogSource("Renewal evidence checks");
        var lines = new List<string>(); log.LogEvent += (_, args) => lines.Add(LogCapture.Text(args.Data));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "heimkit-renewal-evidence-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var debug = config.Bind("Diagnostics", "DebugMode", true);
        var diagnostics = new ForestRenewalDiagnostics(log, new HeimKit.Configuration.DebugLoggingPolicy(debug, false));
        var blocker = (ZDO)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ZDO)); blocker.m_uid = new ZDOID(99, 10);
        diagnostics.Decision(0, new ZDOID(99, 1), "structure_context_unknown", blocker, "collision_geometry_unsupported");
        diagnostics.Decision(61, new ZDOID(99, 2), "structure_context_unknown", blocker, "collision_geometry_unsupported");
        Check(lines.Count == 1 && lines[0].Contains("blocking_object=99:10") && lines[0].Contains("collision_geometry_unsupported"), "Same blocker rotated through sources or lost diagnostic identity.");
        diagnostics.Decision(300, new ZDOID(99, 2), "structure_context_unknown", blocker, "collision_geometry_unsupported");
        Check(lines.Count == 2, "Unresolved blocker heartbeat was lost.");
        debug.Value = false; diagnostics.Decision(600, new ZDOID(99, 3), "structure_buffer");
        Check(lines.Count == 2, "Suppressed renewal decisions emitted evidence.");
        System.Console.WriteLine("PASS: repeated renewal blockers combine across source stumps, retain exact blocker/reason, allow five-minute reminders and obey suppression.");
    }
    private static void Packets()
    {
        var guarded = new ZDOID(2, 1); var ordinary = new ZDOID(2, 2);
        var packet = new ZPackage(); packet.Write(1); packet.Write(guarded);
        foreach (var id in new[] { guarded, ordinary })
        { packet.Write(id); packet.Write((ushort)7); packet.Write(9U); packet.Write(3L); packet.Write(new Vector3(1, 2, 3)); var payload = new ZPackage(); payload.Write("opaque metadata"); packet.Write(payload); }
        packet.Write(ZDOID.None); packet.SetPos(0); var original = packet.GetArray();
        int callbacks = 0;
        var filtered = ZdoPackets.FilterData(packet, id => id == guarded, out int blocked, (_, _, _) => callbacks++);
        Check(blocked == 2 && callbacks == 1 && packet.GetPos() == 0 && packet.GetArray().SequenceEqual(original), "Authority filtering changed original data or callback counts.");
        Check(filtered.ReadInt() == 0 && filtered.ReadZDOID() == ordinary && filtered.ReadUShort() == 7 && filtered.ReadUInt() == 9 && filtered.ReadLong() == 3, "Unrelated native header was changed.");
        filtered.ReadVector3(); Check(filtered.ReadPackage().ReadString() == "opaque metadata" && filtered.ReadZDOID().IsNone() && filtered.GetPos() == filtered.Size(), "Opaque record or terminator lost.");
        var malformed = new ZPackage(original.Take(original.Length - 1).ToArray());
        callbacks = 0; bool rejected = false;
        try { ZdoPackets.FilterData(malformed, _ => true, out _, (_, _, _) => callbacks++); }
        catch (IOException) { rejected = true; }
        Check(rejected && callbacks == 0 && malformed.GetPos() == 0, "Malformed packet partially changed authority metadata.");
        System.Console.WriteLine("PASS: new shared authority filtering preserves unrelated payloads and rejects incomplete packets without metadata mutation.");
    }
    private static void Inventory()
    {
        var sources = new List<SalvageSource>();
        for (int i = 0; i < 4; i++)
        {
            var item = new ZPackage(); item.Write((byte)110); item.Write(123457 + i); item.Write((byte)19); item.Write((byte)23); item.Write((byte)2); item.Write((byte)252);
            item.Write((ushort)(i + 2)); item.Write((ushort)(i + 7)); item.Write(3); item.Write(1234L); item.Write("Crafter"); item.Write(9876);
            item.WriteNumItems(2); item.Write("custom-key"); item.Write("custom-value"); item.Write("unicode"); item.Write("Ægir"); item.Write((byte)1);
            sources.Add(new SalvageSource { Original = item.GetArray() });
        }
        var original = sources.Select(s => (byte[])s.Original.Clone()).ToArray();
        var packed = new ZPackage(SeaSalvageInventory.Pack(sources));
        Check(packed.ReadInt() == 110 && packed.ReadUShort() == 4, "Native inventory header differs.");
        for (int i = 0; i < 4; i++)
        {
            var item = new ItemDrop.ItemData(); int hash = ItemDrop.ItemData.Load(packed, item, (global::Version.Item)110);
            Check(hash == 9876 && item.m_stack == i + 7 && item.m_quality == i + 2 && Math.Abs(item.m_durability - (123457 + i) * .01f) < .001f &&
                item.m_gridPos.x == i % 2 && item.m_gridPos.y == i / 2 && item.m_crafterID == 1234 && item.m_variant == 3 && item.m_worldLevel == 2 &&
                item.m_customData["custom-key"] == "custom-value" && item.m_customData["unicode"] == "Ægir" && item.m_cheated, "Native decoder lost source item state.");
            Check(sources[i].Original.SequenceEqual(original[i]), "Packing mutated source inventory bytes.");
        }
        Check(packed.GetPos() == packed.Size(), "Native inventory decoder did not consume the complete payload.");
        bool rejected = false; try { SeaSalvageInventory.Pack(sources.Concat(sources.Take(1)).ToArray()); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Over-capacity crate accepted.");
        System.Console.WriteLine("PASS: installed native ItemData decoder reads four packed salvage items with quantities, quality, durability, crafter, variant and custom data intact; source bytes remain unchanged.");
    }
    private static void Ledger()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-renewal-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ForestRenewalStore(directory, 123);
            for (uint i = 1; i <= 4; i++) { var id = new ZDOID(456, i); store.Records.Add(id, new RenewalRecord { Source = id, Position = new Vector3(i, 2, 3), Sapling = 987, State = (byte)(i - 1), DueTicks = 100, DecisionTicks = 50 }); }
            store.Save(); var restarted = new ForestRenewalStore(directory, 123);
            Check(restarted.Records.Count == 4 && restarted.Records[new ZDOID(456, 4)].State == 3 && restarted.Records[new ZDOID(456, 1)].DueTicks == 100, "Restart lost pending/consumed decision or timer.");
            var writing = store.SaveAsync();
            store.Records[new ZDOID(456, 1)].State = 2;
            Check(writing.GetAwaiter().GetResult() >= 0 && new ForestRenewalStore(directory, 123).Records[new ZDOID(456, 1)].State == 0,
                "Background ledger writer observed mutations after its main-thread snapshot.");
            store.Save();
            Check(new ForestRenewalStore(directory, 123).Records[new ZDOID(456, 1)].State == 2, "Later ledger generation was not persisted after the prior write completed.");
            restarted.Save(); string path = Directory.GetFiles(directory, "*.renewals").Single();
            Check(File.Exists(path + ".bak"), "Ledger save lost backup.");
            var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
            bool rejected = false; try { _ = new ForestRenewalStore(directory, 123); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(path).SequenceEqual(bytes), "Corrupt renewal ledger was accepted or overwritten.");
            File.Delete(path); rejected = false; try { _ = new ForestRenewalStore(directory, 123); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Missing primary with backup silently reset renewal decisions.");
            string blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "file");
            var unavailable = new ForestRenewalStore(blocked, 124);
            rejected = false;
            try { unavailable.SaveAsync().GetAwaiter().GetResult(); } catch (IOException) { rejected = true; }
            Check(rejected, "Background ledger write failure did not reach its main-thread completion observer.");
            System.Console.WriteLine("PASS: renewal decisions/timers survive atomic ledger round trips; checksum corruption and missing-primary cases fail closed.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static void RenewalCommit()
    {
        var decision = new RenewalRecord { Source = new ZDOID(456, 1), State = 2, DueTicks = 100, DecisionTicks = 200 };
        var placement = new RenewalPlacement { Decision = decision, PendingState = 3, PreviousDecisionTicks = 50, RequiredRevision = 8 };
        Check(!ForestRenewalCommitRules.Ready(placement, 7) && ForestRenewalCommitRules.Ready(placement, 8), "An older ledger acknowledgement authorized a later consumed placement.");
        ForestRenewalCommitRules.RestorePending(placement);
        Check(decision.State == 3 && decision.DueTicks == 100 && decision.DecisionTicks == 50 && !ForestRenewalCommitRules.Ready(placement, 8),
            "Unperformed renewal lost its original timer/state or reused an old acknowledgement.");
        decision.State = 2; placement.PendingState = 0;
        ForestRenewalCommitRules.RestorePending(placement);
        Check(decision.State == 0, "Confirmed removal during a pending write did not retain removed-source renewal.");
        decision.State = 2; placement.MutationStarted = true;
        bool rejected = false;
        try { ForestRenewalCommitRules.RestorePending(placement); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && decision.State == 2 && !ForestRenewalCommitRules.Ready(placement, 8), "Attempted world mutation was eligible for replay.");
        System.Console.WriteLine("PASS: renewal generation acknowledgements reject stale saves, preserve unperformed standing/removed decisions without reroll and forbid replay after world mutation starts.");
    }
    private static void Reload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-mender-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "heimkit.cfg");
            using var log = new ManualLogSource("HeimMender config checks");
            var messages = new List<string>(); log.LogEvent += (_, args) => messages.Add(LogCapture.Text(args.Data));
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false }; var settings = new HeimMenderSettings(config, log); config.Save(); config.SaveOnConfigSet = true;
            using var observer = new HeimMenderConfiguration(config, settings, log);
            void Read(string text, float now)
            {
                File.WriteAllText(path, text); observer.Tick(now); Thread.Sleep(1200); observer.Tick(now + 2);
            }
            string valid = "[HeimMender]\nEnabled = false\nRepairChancePercent = 80\nMaxRepairsPerCycle = 2\nRepairTarget = Trevor Hayman\nRepairRequestId = 1\n[SeaSalvage]\nEnabled = false\n";
            Read(valid, 0);
            Check(!settings.Enabled.Value && settings.RepairChancePercent.Value == 80 && settings.MaxRepairsPerCycle.Value == 2 && settings.RepairTarget.Value == "Trevor Hayman" && settings.RepairRequestId.Value == 1, "Valid live section did not apply.");
            Check(File.ReadAllText(path) == valid && config.SaveOnConfigSet, "Reload rewrote operator file or left config saves suppressed.");
            Read("[HeimMender]\nEnabled = true\nRepairChancePercent = NaN\n", 4);
            Check(!settings.Enabled.Value && settings.RepairChancePercent.Value == 80 && messages.Any(m => m.Contains("reload rejected")), "Invalid live edit partially changed the section.");
            Read("[HeimMender]\nEnabled = true\nMaxRepairsPerCycle = 0\n", 8);
            Check(!settings.Enabled.Value && settings.MaxRepairsPerCycle.Value == 2, "Out-of-range live edit was clamped or partially applied.");
            System.Console.WriteLine("PASS: actual live config observer applies valid section edits without rewriting other sections and rejects nonfinite/out-of-range edits atomically.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void NativeMembers()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        foreach (var pair in new (Type Type, string Member)[] { (typeof(ZoneSystem), "CreateLocalZones"), (typeof(TerrainComp), "CheckLoad"), (typeof(Door), "CanInteract"),
            (typeof(Door), "RPC_UseDoor"), (typeof(Character), "ResetGroundContact"), (typeof(Chat), "TeleportPlayer"), (typeof(Player), "TeleportTo"),
            (typeof(Game), "RPC_Pong"), (typeof(ZSyncTransform), "SyncNow"), (typeof(WearNTear), "RPC_Repair"), (typeof(Terminal), "InitTerminal") })
            Check(pair.Type.GetMethods(flags).Any(m => m.Name == pair.Member), "Native method missing: " + pair.Type.Name + "." + pair.Member);
        foreach (string field in new[] { "m_maxAirAltitude", "m_lastGroundTouch", "m_fallTimer" }) Check(typeof(Character).GetField(field, flags) != null, "Native transfer reset field missing: " + field);
        Check(typeof(BaseAI).GetField("m_path", flags) != null && typeof(Terminal).GetField("commands", flags) != null, "Native path/terminal fields unavailable.");
        System.Console.WriteLine("PASS: referenced native reflection members resolve in installed assemblies; this does not execute Unity hooks or establish gameplay correctness.");
    }
    private static void Timings()
    {
        DiagnosticLog.StartSession();
        using var log = new ManualLogSource("Timing checks");
        var lines = new List<string>(); log.LogEvent += (_, args) => lines.Add(LogCapture.Text(args.Data));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "heimkit-timing-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var debug = config.Bind("Diagnostics", "DebugMode", true);
        var timing = new ProcessingTiming(log, "HeimMender", new HeimKit.Configuration.DebugLoggingPolicy(debug, false));
        var scope = timing.Measure(0, () => 5); scope.Dispose(); scope.Dispose();
        using (timing.Measure(1, () => 5)) { }
        Check(lines.Count == 1 && lines[0].Contains(" ms."), "Timing duplicated a disposed scope or exceeded its reporting interval.");
        using (timing.Measure(60, () => 5)) { }
        Check(lines.Count == 2 && lines[1].Contains("2 passes in the last minute, avg"), "Timing interval omitted measurements.");
        debug.Value = false;
        using (timing.Measure(120, () => 5)) { }
        Check(lines.Count == 2, "Disabled diagnostic policy emitted timing data.");
        System.Console.WriteLine("PASS: bounded controller timing aggregates intervals, disposes idempotently and obeys live debug suppression.");
    }
    private static void AuthorityTargets()
    {
        // Resolve the actual constructor's patch set, rather than checking names exist.
        var targets = ObjectAuthority.NativePatches();
        Check(targets.Length == 6 && targets.Select(t => t.Target).Distinct().Count() == 6, "Shared authority patch set is incomplete or duplicates a method.");
        var outside = targets.Single(t => t.Target.Name == "OutsideActiveArea").Target;
        Check(!outside.IsStatic && outside.ReturnType == typeof(bool) && outside.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Vector3) }),
            "Authority selected the static two-argument active-area overload.");
        var staticOverload = typeof(ZNetScene).GetMethod("OutsideActiveArea", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3), typeof(Vector2s) }, null);
        Check(staticOverload != null && staticOverload != outside, "Fixture did not contain the ambiguous native overloads.");
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var humanoid = typeof(Humanoid).GetMethod("CustomFixedUpdate", flags, null, new[] { typeof(float) }, null);
        var character = typeof(Character).GetMethod("CustomFixedUpdate", flags, null, new[] { typeof(float) }, null);
        Check(humanoid != null && character != null && humanoid.DeclaringType == typeof(Humanoid) && character.DeclaringType == typeof(Character), "Native humanoid override no longer has its audited owner-work entry point.");
        Check(typeof(BaseAI).GetField("m_spawnPoint", flags) != null, "Native relocation spawn anchor is missing.");
        System.Console.WriteLine("PASS: actual shared authority patch set resolves all exact overloads, including the formerly ambiguous instance active-area method; native humanoid override and spawn anchor resolve.");
    }
    private static void FollowSpacing()
    {
        var distances = TameTravelFollowRules.Distances(4, 5, 1.5f, .49f);
        Check(distances == (4f, 5f), "Smaller-animal spacing changed configured stop/resume distances.");
        var lox = TameTravelFollowRules.Distances(4, 5, 1.5f, .49f, lox: true);
        Check(lox == (6f, 8f) && TameTravelFollowRules.Stopped(false, 36, lox.Stop, lox.Resume) &&
            TameTravelFollowRules.Stopped(true, 64, lox.Stop, lox.Resume) &&
            !TameTravelFollowRules.Stopped(true, 8.1f * 8.1f, lox.Stop, lox.Resume),
            "Lox spacing lost its six/eight-metre minimum or two-metre hysteresis.");
        Check(TameTravelFollowRules.Distances(10, 15, 1.5f, .49f, lox: true) == (10f, 15f) &&
            TameTravelFollowRules.Distances(4, 5, 9, .5f, lox: true) == (10.5f, 12.5f),
            "Lox spacing ignored larger configured distances or scaled body clearance.");
        bool stopped = TameTravelFollowRules.Stopped(false, 3.9f * 3.9f, distances.Stop, distances.Resume);
        Check(stopped && TameTravelFollowRules.Stopped(stopped, 4.9f * 4.9f, distances.Stop, distances.Resume) &&
            TameTravelFollowRules.Stopped(stopped, 25, distances.Stop, distances.Resume) &&
            !TameTravelFollowRules.Stopped(stopped, 5.1f * 5.1f, distances.Stop, distances.Resume) &&
            !TameTravelFollowRules.Stopped(false, 4.1f * 4.1f, distances.Stop, distances.Resume), "Follower movement lost stop/resume hysteresis.");
        var large = TameTravelFollowRules.Distances(4, 4, 5, .5f);
        Check(large == (6.5f, 7.5f), "Scaled animal clearance or minimum resume gap was lost.");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, -1 })
        {
            bool rejected = false;
            try { TameTravelFollowRules.Distances(invalid, 5, 1.5f, .49f); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Nonfinite/negative follow spacing was accepted.");
        }
        var follow = TameTravelFollowing.NativeMethod("Follow", typeof(GameObject), typeof(float));
        var move = TameTravelFollowing.NativeMethod("MoveTo", typeof(float), typeof(Vector3), typeof(float), typeof(bool));
        Check(follow.ReturnType == typeof(void) && move.ReturnType == typeof(bool), "Follow movement native signatures changed.");
        _ = move.CreateDelegate(typeof(Func<BaseAI, float, Vector3, float, bool, bool>));
        using var log = new ManualLogSource("Follow spacing checks");
        var lines = new List<string>(); log.LogEvent += (_, args) => lines.Add(LogCapture.Text(args.Data));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "heimkit-follow-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var debug = config.Bind("Diagnostics", "DebugMode", true);
        float now = 1;
        var diagnostics = new TameTravelDiagnostics(log, new HeimKit.Configuration.DebugLoggingPolicy(debug, false), () => now);
        diagnostics.EndFollowing(diagnostics.BeginFollowing(), 2);
        diagnostics.EndFollowing(diagnostics.BeginFollowing(), 2);
        Check(lines.Count == 1 && lines[0].Contains("1 follow updates in the last minute") && lines[0].Contains("scope=2"), "Follow timing exceeded its interval or omitted scope.");
        now = 61; diagnostics.EndFollowing(diagnostics.BeginFollowing(), 2);
        Check(lines.Count == 2 && lines[1].Contains("2 follow updates in the last minute"), "Follow timing lost its aggregated interval.");
        debug.Value = false; now = 121;
        Check(diagnostics.BeginFollowing() == 0, "Suppressed follow timing still collected measurements.");
        diagnostics.EndFollowing(1, 2); Check(lines.Count == 2, "Suppressed follow timing emitted output.");
        System.Console.WriteLine("PASS: follow spacing preserves hysteresis and scaled clearance; exact native follow/movement signatures and open delegate resolve; movement timing aggregates and obeys suppression. Unity ownership and physics require live checks.");
    }
    private static void CommandEvidence()
    {
        using var log = new ManualLogSource("Animal command evidence checks");
        var lines = new List<string>(); log.LogEvent += (_, args) => lines.Add(LogCapture.Text(args.Data));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "heimkit-command-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var debug = config.Bind("Diagnostics", "DebugMode", true);
        float now = 1;
        var diagnostics = new TameTravelDiagnostics(log, new HeimKit.Configuration.DebugLoggingPolicy(debug, false), () => now);
        var peer = (ZNetPeer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer));
        peer.m_uid = 123; peer.m_playerName = "Trevor"; peer.m_characterID = new ZDOID(123, 1);
        diagnostics.ActorGate(peer, "character_name_mismatch"); diagnostics.ActorGate(peer, "character_name_mismatch");
        Check(lines.Count == 1 && lines[0].Contains("character_name_mismatch"), "Actor gate repeats were not combined or rejection reason was hidden.");
        diagnostics.Command(peer, "emote_applied", "nonono", counter: 4);
        diagnostics.Command(peer, "rejected", "Follow", "nearest_animal_ambiguous");
        Check(lines.Count == 3 && lines[1].Contains("emote_counter=4") && lines[2].Contains("nearest_animal_ambiguous"), "Receipt and selection rejection evidence were lost.");
        for (int i = 0; i < 50; i++) diagnostics.Command(peer, "request_received", "Stay");
        Check(lines.Count == 32, "Command evidence exceeded its thirty-two-record budget.");
        now = 61; diagnostics.ActorGate(peer, null);
        Check(lines.Count == 33 && lines[^1].Contains("reason=ready"), "Actor recovery did not start a new bounded evidence interval.");
        debug.Value = false; diagnostics.Command(peer, "request_received", "Follow");
        Check(lines.Count == 33, "Suppressed command evidence emitted output.");
        diagnostics.Selection(peer, TameOrder.Follow, 100, 2, 0);
        Check(lines.Count == 33, "Suppressed selection evidence emitted output.");
        debug.Value = true; now = 122; lines.Clear();
        var animal = (ZDO)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ZDO)); animal.m_uid = new ZDOID(123, 22);
        diagnostics.Selection(peer, TameOrder.Follow, 100, 2, 1);
        diagnostics.Candidate(peer, TameOrder.Follow, animal, 3, 8, "eligible");
        Check(lines.Count == 2 && !lines[1].Contains("health_saved=") && lines[1].Contains("animal=123:22") && lines[1].Contains("distance_m=3"), "Candidate identity, distance or native omitted-health evidence was lost.");
        for (int i = 0; i < 20; i++) diagnostics.Candidate(peer, TameOrder.Follow, animal, 3, 8, "eligible");
        Check(lines.Count == 8, "Selection evidence exceeded its shared eight-record budget.");
        diagnostics.Command(peer, "request_received", "Stay");
        Check(lines.Count == 9, "Selection evidence consumed the command receipt budget.");
        System.Console.WriteLine("PASS: animal command receipt, actor/selection rejections and recovery retain identity, combine unchanged gates, obey suppression and stay bounded.");
    }
    private static void TameCommands()
    {
        Check(TameTravelCommandRules.StopAssociated("Trevor", "Trevor", false) &&
            TameTravelCommandRules.StopAssociated("", "Trevor", true) &&
            !TameTravelCommandRules.StopAssociated("Someone else", "Trevor", true) &&
            !TameTravelCommandRules.StopAssociated("", "Trevor", false) &&
            !TameTravelCommandRules.StopAssociated("", "", true), "Group stay crossed player identity or selected unrelated standing animals.");
        Check(TameTravelCommandRules.Chat(" !FOLLOW ", out var follow) && follow == TameOrder.Follow &&
            TameTravelCommandRules.Chat("!stay", out var stay) && stay == TameOrder.Stay &&
            TameTravelCommandRules.Chat("!wait", out var wait) && wait == TameOrder.Wait, "Meaningful animal commands failed to parse.");
        foreach (string text in new[] { "/follow", "/stay", "/wait", "follow", "!follow someone", "!wait-all" })
            Check(!TameTravelCommandRules.Chat(text, out _), "Unsupported command or ordinary conversation authorized an animal action.");
        Check(TameTravelCommandRules.Emote("comehere", out follow) && follow == TameOrder.Follow &&
            TameTravelCommandRules.Emote("nonono", out stay) && stay == TameOrder.Stay &&
            TameTravelCommandRules.Emote("wave", out wait) && wait == TameOrder.Wait && !TameTravelCommandRules.Emote("cheer", out _), "Vanilla command fallback mapping changed.");
        Check(TameTravelCommandRules.ConsentValid(TameOrder.Follow, "Trevor", "Trevor") &&
            !TameTravelCommandRules.ConsentValid(TameOrder.Follow, "Another player", "Trevor") &&
            !TameTravelCommandRules.ConsentValid(TameOrder.Follow, "", "Trevor") &&
            TameTravelCommandRules.ConsentValid(TameOrder.Wait, "", "Trevor") &&
            !TameTravelCommandRules.ConsentValid(TameOrder.Wait, "Another player", "Trevor") &&
            !TameTravelCommandRules.ConsentValid(TameOrder.Stay, "", "Trevor"), "Wait, stay and active following did not keep distinct travel eligibility.");
        System.Console.WriteLine("PASS: follow/stay/wait parsing and native emote fallbacks retain distinct consent rules, exclude ordinary speech, reject foreign following and never turn stay into travel consent.");
        foreach (float configured in new[] { 2f, 8f, 12f })
        {
            float stopRange = TameTravelCommandRules.SelectionRange(TameOrder.Stay, configured);
            Check(8.2f < stopRange && 16f < stopRange && stopRange == 32 &&
                TameTravelCommandRules.SelectionRange(TameOrder.Follow, configured) == configured &&
                TameTravelCommandRules.SelectionRange(TameOrder.Wait, configured) == configured,
                "Stay cannot reach a following Lox beyond the command radius, or follow/wait selection expanded.");
        }
        Check(TameTravelCommandRules.AwaitingEarlierFollow(true, TameOrder.Follow, true) &&
            !TameTravelCommandRules.AwaitingEarlierFollow(false, TameOrder.Follow, true) &&
            !TameTravelCommandRules.AwaitingEarlierFollow(true, TameOrder.Wait, true) &&
            !TameTravelCommandRules.AwaitingEarlierFollow(true, TameOrder.Stay, false),
            "Queued stay can falsely confirm before an in-flight follow toggle, or waits for unrelated commands.");
        System.Console.WriteLine("PASS: stay reaches associated followers beyond Lox resume spacing without expanding follow/wait selection, and distinguishes an in-flight follow from confirmed follow or pending stay/wait.");
    }
    private static void TransferDiagnostics()
    {
        DiagnosticLog.StartSession();
        using var log = new ManualLogSource("Transfer diagnosis checks");
        var lines = new List<string>(); log.LogEvent += (_, args) => lines.Add(LogCapture.Text(args.Data));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "heimkit-transfer-diagnostics-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        var policy = new HeimKit.Configuration.DebugLoggingPolicy(config.Bind("Diagnostics", "DebugMode", true), false);
        var diagnostics = new TameTravelDiagnostics(log, policy, () => 1);
        // A released/recycled live record must never be consulted by the formatter.
        var transfer = new AnimalTransfer { Record = null!, Journey = new PortalJourney { Name = "Trevor" }, Id = new ZDOID(456, 1), Phase = 2, Moved = true, SourceStateObserved = true };
        var snapshot = new TransferSnapshot { Reason = "follow_changed", SameRecord = true, Owner = 1234, DataRevision = 99, OwnerRevision = 5,
            Position = new Vector3(1, 2, 3), Follow = "", Health = 200, Tamed = true, Rider = 0, PhaseAge = .5f, BarrierEchoed = true };
        diagnostics.Transfer(transfer, false, snapshot);
        Check(lines.Count == 1 && lines[0].Contains("for Trevor ended — follow changed") && !lines[0].Contains("owner="),
            "Normal transfer outcome lost its cause or read the released record.");
        for (int i = 0; i < 32; i++) diagnostics.Transfer(transfer, false, snapshot);
        Check(lines.Count == 8, "Detailed transfer diagnostics exceeded the independent ending allowance.");
        HeimKit.Configuration.DiagnosticsSettings.Bind(config);
        config.Bind("Diagnostics", "DebugVerbosity", HeimKit.Configuration.DebugVerbosity.Normal).Value = HeimKit.Configuration.DebugVerbosity.Verbose;
        try
        {
            lines.Clear();
            new TameTravelDiagnostics(log, policy, () => 1).Transfer(transfer, false, snapshot);
            Check(lines.Count == 2 && lines[1].Contains("reason=follow_changed") && lines[1].Contains("owner=1234") &&
                lines[1].Contains("position_before_release=1.0,2.0,3.0") && lines[1].Contains("source_state_observed=true") &&
                lines[1].Contains("barrier_echoed=true"), "Verbose transfer evidence lost its immutable pre-release snapshot.");
        }
        finally { config.Bind("Diagnostics", "DebugVerbosity", HeimKit.Configuration.DebugVerbosity.Normal).Value = HeimKit.Configuration.DebugVerbosity.Normal; }
        System.Console.WriteLine("PASS: transfer stop reasons and pre-release evidence survive record cleanup without reading the live record, and remain capped at eight endings per minute.");
    }
}
