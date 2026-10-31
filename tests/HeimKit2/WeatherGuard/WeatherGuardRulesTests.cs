using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.WeatherGuard.Models;
using HeimKit.Features.WeatherGuard;
using UnityEngine;

internal static class WeatherGuardRulesTests
{
    private static void Check(bool valid, string message)
    { if (!valid) throw new Exception("WeatherGuard processing check: " + message); }
    private static ZPackage Packet(ZDOID protectedId, ZDOID ordinaryId)
    {
        var packet = new ZPackage();
        packet.Write(2); packet.Write(protectedId); packet.Write(ordinaryId);
        foreach (var id in new[] { protectedId, ordinaryId })
        {
            var body = new ZPackage(); body.Write("opaque creator/health/support/inventory"); body.Write(731L); body.Write(88.25f);
            packet.Write(id); packet.Write((ushort)12); packet.Write(43U); packet.Write(20L);
            packet.Write(new Vector3(5f, 6f, 7f)); packet.Write(body);
        }
        packet.Write(ZDOID.None); packet.SetPos(0); return packet;
    }
    public static void Run()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        foreach (string name in new[] { "CreateObjects", "RemoveObjects" })
        {
            var method = typeof(ZNetScene).GetMethod(name, flags)!;
            Check(method != null && method.GetParameters().Select(p => p.Name).SequenceEqual(new[] { "currentNearObjects", "currentDistantObjects" }),
                "instance-list Harmony parameters match installed game");
        }
        Check(typeof(ZoneSystem).GetMethod("CreateLocalZones", flags)?.ReturnType == typeof(bool) &&
            typeof(ZNetScene).GetField("m_instances", flags)?.FieldType == typeof(Dictionary<ZDO, ZNetView>), "terrain and live-instance dependencies match installed game");
        Check(typeof(Piece).GetField("m_creator", flags)?.FieldType == typeof(long) &&
            typeof(Piece).GetField("m_creatorPlatformUserIDIndex", flags)?.FieldType == typeof(int), "server attribution caches match installed game");
        Check(typeof(ZDOMan).GetMethod("RPC_ZDOData", flags)?.GetParameters().Select(p => p.Name).SequenceEqual(new[] { "rpc", "pkg" }) == true,
            "client data Harmony parameters match installed game");
        Check(WeatherGuardRules.Eligible(111, true, true, true, 234f, true, false), "player-built piece is primary candidate");
        Check(!WeatherGuardRules.RequiresContext(0, false, false), "collider-free sounds do not interrupt structure processing");
        Check(WeatherGuardRules.RequiresContext(1, false, false), "non-piece collider context still required for roof/support physics");
        Check(WeatherGuardRules.RequiresContext(0, true, false), "support-giving wear dependency still required even without prefab colliders");
        Check(WeatherGuardRules.RequiresContext(0, false, true), "shield dependency still required even without prefab colliders");
        int nativeMask = (1 << 0) | (1 << 8) | (1 << 31);
        for (int layer = 0; layer < 32; layer++)
            Check(WeatherGuardRules.ColliderUsesContextMask(layer, nativeMask) == (layer == 0 || layer == 8 || layer == 31),
                "native context mask must include exactly its collision layers, including the sign bit");
        foreach (int layer in new[] { -1, 0, 12, 31, 32 })
            Check(WeatherGuardRules.ColliderUsesContextMask(layer, 0), "uninitialized native mask must retain dependencies");
        Check(WeatherGuardRules.ColliderUsesContextMask(-1, nativeMask) && WeatherGuardRules.ColliderUsesContextMask(32, nativeMask),
            "unknown collider layers must retain dependencies");
        // Cloudberry has WearNTear but m_supports=false and item-layer colliders.
        bool itemColliderRelevant = WeatherGuardRules.ColliderUsesContextMask(12, nativeMask);
        Check(!WeatherGuardRules.RequiresContext(itemColliderRelevant ? 1 : 0, false, false),
            "out-of-mask drops with non-supporting wear must not block building protection");
        Check(WeatherGuardRules.RequiresContext(itemColliderRelevant ? 1 : 0, true, false) &&
            WeatherGuardRules.RequiresContext(itemColliderRelevant ? 1 : 0, false, true),
            "support-giving wear and shields must remain dependencies outside collider query layers");
        Check(typeof(WearNTear).GetField("s_rayMask", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.FieldType == typeof(int) &&
            typeof(WearNTear).GetField("m_supports", flags)?.FieldType == typeof(bool), "native context mask and support flag retain their audited types");
        Check(!WeatherGuardRules.Eligible(0, true, true, true, 234f, true, false), "world piece is unchanged");
        Check(WeatherGuardRules.Exclusion(0, true, true, true, 234f, true, false) == "world-created", "world control reason");
        Check(WeatherGuardRules.Exclusion(0, true, true, true, 234f, true, true) == "container", "container exclusion takes precedence over creator");
        Check(WeatherGuardRules.Exclusion(111, true, true, false, 234f, true, false) == "weather immune", "immune material reason");
        foreach (float health in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            Check(!WeatherGuardRules.Eligible(111, true, true, true, health, true, false), "dead/invalid health excluded");
        Check(!WeatherGuardRules.Eligible(111, true, true, true, 234f, true, true), "inventory containers are not seized");
        Check(!WeatherGuardRules.Eligible(111, true, true, true, 234f, false, false), "non-building categories excluded");
        Check(!WeatherGuardRules.Eligible(111, true, false, true, 234f, true, false), "moving objects excluded");
        Check(!WeatherGuardRules.Eligible(111, true, true, false, 234f, true, false), "already weather-immune materials need no authority change");
        for (int mask = 0; mask < 16; mask++)
            Check(WeatherGuardRules.MayAcquire((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0) == (mask == 15),
                "instance, terrain, surrounding objects and capacity all required");

        var claimed = new ZDOID(1L, 10U); var ordinary = new ZDOID(2L, 20U);
        var packet = Packet(claimed, ordinary); var bytes = packet.GetArray();
        var unchanged = WeatherGuardNetworkFilter.FilterData(packet, _ => false, out int zero);
        Check(ReferenceEquals(packet, unchanged), "Unprotected packet filtering must reuse the validated original packet.");
        Check(zero == 0 && bytes.SequenceEqual(unchanged.GetArray()) && packet.GetPos() == 0 && bytes.SequenceEqual(packet.GetArray()), "unprotected packets remain byte-identical and original cursor/bytes untouched");
        var output = WeatherGuardNetworkFilter.FilterData(packet, id => id == claimed, out int blocked);
        Check(blocked == 2 && packet.GetPos() == 0 && bytes.SequenceEqual(packet.GetArray()), "protected state and sector invalidation filtered without changing source");
        Check(output.ReadInt() == 1 && output.ReadZDOID() == ordinary && output.ReadZDOID() == ordinary, "only ordinary invalidation and data record survive");
        Check(output.ReadUShort() == 12 && output.ReadUInt() == 43 && output.ReadLong() == 20, "unrelated revisions and owner unchanged");
        var position = output.ReadVector3();
        Check(position.x == 5f && position.y == 6f && position.z == 7f, "unrelated position unchanged");
        var body = output.ReadPackage();
        Check(body.ReadString() == "opaque creator/health/support/inventory" && body.ReadLong() == 731 && body.ReadSingle() == 88.25f,
            "unrelated opaque payload unchanged");
        Check(output.ReadZDOID().IsNone() && output.GetPos() == output.Size(), "packet terminator intact");
        foreach (int badCount in new[] { -1, WeatherGuardNetworkFilter.MaximumRecords + 1 })
        {
            var malformed = new ZPackage(); malformed.Write(badCount); malformed.SetPos(0);
            bool rejected = false;
            try { WeatherGuardNetworkFilter.FilterData(malformed, _ => false, out _); } catch (System.IO.InvalidDataException) { rejected = true; }
            Check(rejected && malformed.GetPos() == 0, "malformed counts rejected with cursor restored");
        }
        var truncated = Packet(claimed, ordinary); truncated.SetPos(truncated.Size() - 1);
        int cursor = truncated.GetPos(); bool failed = false;
        try { WeatherGuardNetworkFilter.FilterData(truncated, _ => false, out _); } catch (Exception) { failed = true; }
        Check(failed && truncated.GetPos() == cursor, "truncated input restores nonzero cursor");
        var destroy = new ZPackage(); destroy.Write(2); destroy.Write(claimed); destroy.Write(ordinary); destroy.SetPos(0);
        var filtered = WeatherGuardNetworkFilter.FilterDestroyed(destroy, id => id == claimed, out int excluded);
        Check(excluded == 1 && filtered.ReadInt() == 1 && filtered.ReadZDOID() == ordinary && destroy.GetPos() == 0, "stale client destruction excludes only server-controlled IDs");
        foreach (string name in new[] { "RPC_Damage", "RPC_Repair", "RPC_Remove" })
        {
            int method = name.GetStableHashCode();
            Check(WeatherGuardRules.Route(method, 20, 99) == Routing.Server, "combat/repair/removal requests redirected during handover");
            Check(WeatherGuardRules.Route(method, 99, 99) == Routing.Forward, "already-server requests preserved");
        }
        foreach (string name in new[] { "RPC_HealthChanged", "RPC_SetSnow", "RPC_CreateFragments" })
            Check(WeatherGuardRules.Route(name.GetStableHashCode(), 0, 99) == Routing.Drop, "client processing broadcasts cannot overwrite server decisions");
        Check(WeatherGuardRules.Route("RPC_ClearCachedSupport".GetStableHashCode(), 0, 99) == Routing.Forward, "support cache broadcasts remain allowed");
        var newer = WeatherGuardRules.ReassertRevisions(43, 12, 50, 15);
        Check(newer.Data == 51 && newer.Owner == 16, "newer client revisions cannot suppress the authoritative snapshot");
        var older = WeatherGuardRules.ReassertRevisions(43, 12, 40, 10);
        Check(older.Data == 43 && older.Owner == 12, "older packets do not rewind metadata");
        bool overflow = false;
        try { WeatherGuardRules.ReassertRevisions(43, 12, uint.MaxValue, 15); } catch (InvalidOperationException) { overflow = true; }
        Check(overflow, "revision overflow cannot silently wrap");
        int callbacks = 0;
        WeatherGuardNetworkFilter.FilterData(Packet(claimed, ordinary), id => id == claimed, out _, (id, revision, owner) =>
        { Check(id == claimed && revision == 43 && owner == 12, "handover metadata belongs only to rejected record"); callbacks++; });
        Check(callbacks == 1, "one authority correction per rejected data record");
        var largeUnrelated = new ZPackage();
        largeUnrelated.Write(1L); largeUnrelated.Write(20L); largeUnrelated.Write(0L); largeUnrelated.Write(ZDOID.None);
        largeUnrelated.Write("RPC_MapData".GetStableHashCode()); largeUnrelated.Write(new byte[WeatherGuardNetworkFilter.MaximumPacketBytes + 1]); largeUnrelated.SetPos(0);
        Check(!WeatherGuardNetworkFilter.RelevantRouted(largeUnrelated, id => id == claimed) && largeUnrelated.GetPos() == 0,
            "large unrelated map traffic bypasses WeatherGuard payload decoder and preserves cursor");
        var trailing = new ZPackage(Packet(claimed, ordinary).GetArray().Concat(new byte[] { 1 }).ToArray());
        bool trailingRejected = false;
        try { WeatherGuardNetworkFilter.FilterData(trailing, id => id == claimed, out _, (_, _, _) => callbacks++); }
        catch (System.IO.InvalidDataException) { trailingRejected = true; }
        Check(trailingRejected && callbacks == 1 && trailing.GetPos() == 0, "invalid packet cannot partially correct authority");
        System.Console.WriteLine("PASS: player-created eligibility and readiness gates; byte-preserving client-state/destruction filters; malformed/cursor handling; native combat/repair/removal routing and owner-broadcast rejection. Live authority, terrain, rain, combat and support still require Unity testing.");
    }
}
