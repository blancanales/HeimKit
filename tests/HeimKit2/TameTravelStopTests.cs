using System.Reflection;
using System.Runtime.CompilerServices;
using HeimKit.Core.Ownership;
using HeimKit.Features.TameTravel;

internal static class TameTravelStopTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run()
    {
        var animal = new ZDOID(77441, 51);
        var calls = new List<string>();
        ZdoReplication.ResetClientCache(animal, packet =>
        {
            packet.SetPos(0);
            Check(packet.ReadInt() == 1 && packet.ReadZDOID() == animal && packet.GetPos() == packet.Size(), "Cache reset addressed a different animal or malformed native payload.");
            calls.Add("remove-client-instance");
        }, () => calls.Add("original-record-snapshot"));
        Check(calls.SequenceEqual(new[] { "remove-client-instance", "original-record-snapshot" }), "Snapshot arrived before stale client state was removed.");
        bool snapshot = false;
        try { ZdoReplication.ResetClientCache(animal, _ => throw new IOException("connection lost"), () => snapshot = true); }
        catch (IOException) { }
        Check(!snapshot, "Failed cache removal still sent a misleading replacement snapshot.");
        bool invalid = false;
        try { ZdoReplication.ResetClientCache(ZDOID.None, _ => { }, () => { }); }
        catch (ArgumentException) { invalid = true; }
        Check(invalid, "An invalid record ID reached the client destruction route.");
        System.Console.WriteLine("PASS: stop refresh uses one exact original animal ID, orders remote cache removal before its snapshot, and does not advance after a failed removal send.");

        int wolf = "Wolf".GetStableHashCode(), lox = "Lox".GetStableHashCode();
        Check(TameTravelFollowState.Recoverable(wolf, "Owner", "Owner", "previous-boot") &&
            TameTravelFollowState.Recoverable(lox, "", "Owner", "previous-boot"), "Interrupted managed follow/stop was not recoverable.");
        Check(!TameTravelFollowState.Recoverable(wolf, "Other", "Owner", "previous-boot") &&
            !TameTravelFollowState.Recoverable(wolf, "Owner", "Owner", TameTravelFollowState.Session) &&
            !TameTravelFollowState.Recoverable("piece_chest_wood".GetStableHashCode(), "Owner", "Owner", "previous-boot"),
            "Recovery crossed a player, current session, or supported-animal boundary.");
        Check(TameTravelFollowState.Recoverable(lox, "OldOwner", "", "") &&
            !TameTravelFollowState.Recoverable(wolf, "VanillaOwner", "", "") &&
            !TameTravelFollowState.Recoverable(lox, "", "", ""), "Legacy migration cleared native wolves or ordinary standing livestock.");
        System.Console.WriteLine("PASS: recovery clears prior HeimKit sessions and unmarked legacy lox, preserves current sessions, other players, native wolves, unrelated prefabs and standing livestock.");

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var record = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO)); record.m_uid = animal;
        typeof(ZDO).GetField("m_prefab", flags)!.SetValue(record, lox);
        var netField = typeof(ZNet).GetField("m_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var worldField = typeof(ZDOMan).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousNet = netField.GetValue(null); var previousWorld = worldField.GetValue(null);
        netField.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(ZNet)));
        worldField.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(ZDOMan)));
        try
        {
            record.Set(ZDOVars.s_follow, "Owner"); record.Set(ZDOVars.s_tamed, true);
            record.Set(ZDOVars.s_tamedName, "Test Lox"); record.Set(ZDOVars.s_health, 731f);
            TameTravelFollowState.Mark(record, "Owner");
            var world = (ZDOMan)worldField.GetValue(null)!;
            typeof(ZDOMan).GetField("m_objectsByID", flags)!.SetValue(world, new Dictionary<ZDOID, ZDO> { [animal] = record });
            // Native owner payloads replace extra-data maps. The pre-save hook
            // must restore provenance even if that upload omitted both keys.
            ZDOExtraData.RemoveString(animal, TameTravelFollowState.OwnerKey);
            ZDOExtraData.RemoveString(animal, TameTravelFollowState.SessionKey);
            TameTravelFollowState.PrepareSave(world);
            Check(record.GetString(TameTravelFollowState.OwnerKey) == "Owner" &&
                record.GetString(TameTravelFollowState.SessionKey) == TameTravelFollowState.Session,
                "A native client payload could erase recovery provenance from the next world save.");
            TameTravelFollowState.ClearSaved(record);
            uint revision = record.DataRevision;
            TameTravelFollowState.ClearSaved(record);
            Check(record.m_uid == animal && record.GetString(ZDOVars.s_follow) == "" &&
                record.GetString(TameTravelFollowState.OwnerKey) == "" && record.GetBool(ZDOVars.s_patrol) &&
                record.GetString(ZDOVars.s_tamedName) == "Test Lox" && record.GetFloat(ZDOVars.s_health) == 731 &&
                record.GetBool(ZDOVars.s_tamed) && record.DataRevision == revision,
                "Stop altered animal identity/state or was not idempotent.");
            System.Console.WriteLine("PASS: saved stop is idempotent, clears following/provenance and establishes staying while preserving the original animal, name, health and tame state.");
        }
        finally { TameTravelFollowState.Untrack(animal); ZDOExtraData.Release(record, animal); netField.SetValue(null, previousNet); worldField.SetValue(null, previousWorld); }
    }
}
