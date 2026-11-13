using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HeimKit.Core.Native;
using HeimKit.Core.World;
using HeimKit.Features.TameTravel;
using HeimKit.Features.WeatherGuard;
using UnityEngine;
using HarmonyLib;
public static class MonoSmoke
{
    public static string Run()
    {
        if (Type.GetType("Mono.Runtime") == null) throw new Exception("This check must run in Mono.");
        bool rejected = false;
        try { _ = new DynamicMethod("old_fieldref", typeof(bool).MakeByRefType(), new[] { typeof(object) }, typeof(MonoSmoke).Module, true); }
        catch (ArgumentException e) { rejected = e.Message.Contains("byref"); }
        if (!rejected) throw new Exception("The original ref-return failure was not reproduced.");
        System.Console.WriteLine("PASS: installed game Mono reproduces the original raw DynamicMethod ref-return rejection.");
        NativeAccessTests.Run();
        _ = NativeAccess.Field<WearNTear, bool>("m_rainWet");
        _ = NativeAccess.Field<WearNTear, bool>("m_haveRoof");
        _ = NativeAccess.Field<WearNTear, int>("m_shieldChangeID");
        _ = NativeAccess.Field<WearNTear, float>("m_rainTimer");
        _ = NativeAccess.Field<WearNTear, float>("m_createTime");
        _ = NativeAccess.Field<WearNTear, float>("m_updateCoverTimer");
        _ = NativeAccess.Field<Piece, long>("m_creator");
        _ = NativeAccess.Field<Piece, int>("m_creatorPlatformUserIDIndex");
        WorldObjectProvider.CheckLiveInstanceCompatibility();
        TerrainContext.CheckCompatibility();
        NativeConnection.CheckCompatibility();
        NativeAccess.Target(typeof(BaseAI), "GetTargetCreature");
        NativeAccess.Target(typeof(MonsterAI), "GetStaticTarget");
        var contextParameters = new ZPackage(); contextParameters.Write(true); contextParameters.Write(false);
        var contextData = new ZRoutedRpc.RoutedRPCData { m_msgID = 1, m_senderPeerID = 99, m_targetPeerID = 42,
            m_targetZDO = new ZDOID(44, 3), m_methodHash = "OnTargeted".GetStableHashCode(), m_parameters = contextParameters };
        var contextPacket = new ZPackage(); contextData.Serialize(contextPacket); contextPacket.SetPos(0);
        if (!HeimKit.Core.Players.RoutedActivityReader.TryReadTargeted(contextPacket, 99, out _, out bool sensed, out bool alerted) || !sensed || alerted || contextPacket.GetPos() != 0)
            throw new Exception("Native player-target context parsing changed parameters or cursor state.");
        System.Console.WriteLine("PASS: game Mono resolves NPC target accessors and preserves authenticated player-target notification packets.");
        var slicedMap = new HeimKit.Features.SharedMap.SharedMapExploration(2048, 12, 50, .3f);
        byte[] slicedSource;
        using (var terrain = new System.IO.MemoryStream())
        {
            using (var fields = new System.IO.BinaryWriter(terrain, System.Text.Encoding.UTF8, true))
            {
                fields.Write(1); fields.Write(2048 * 2048);
                var mask = new byte[2048 * 2048]; mask[0] = 1; fields.Write(mask);
            }
            using var compressed = new System.IO.MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, true))
                gzip.Write(terrain.GetBuffer(), 0, (int)terrain.Length);
            slicedSource = compressed.ToArray();
        }
        var noMarkers = Array.Empty<HeimKit.Features.SharedMap.Models.MapMarker>();
        var slicedInputs = new HeimKit.Features.SharedMap.SharedMapReconciliationState(0, 0, 0, 0, 10,
            new[] { new HeimKit.Features.SharedMap.Models.MapMarker("Probe", 1, 2, 3, "probe") }, noMarkers, noMarkers, noMarkers, noMarkers, noMarkers);
        using (var sliced = new HeimKit.Features.SharedMap.SharedMapTableData.Reconciliation(slicedMap, slicedSource, slicedInputs,
            false, null, null, null, null))
        {
            int slices = 0;
            while (!sliced.Advance()) if (++slices > 10000) throw new Exception("Sliced native table pipeline stopped progressing.");
            if (slices < 2 || sliced.Output == null || !sliced.CanPublish(slicedSource) || !slicedMap.IsExploredPixel(0, 0))
                throw new Exception("Native Mono changed sliced table import or completion semantics.");
        }
        System.Console.WriteLine("PASS: game Mono runs pooled full-sized table decompression, validation, compression and sliced terrain import without Unity world access.");
        NativeAccess.Target(typeof(SectorReadiness), "Ready", typeof(Vector2s));
        NativeAccess.Target(typeof(CandidateSubscription), "get_Revision");
        NativeAccess.Target(typeof(SceneInstances), "get_BindingRevision");
        System.Console.WriteLine("PASS: game Mono resolves indexed readiness and native-binding revision APIs without requiring Unity scene services.");
        NativeAccess.Target(typeof(ZoneSystem), "IsZoneLoaded", typeof(Vector3));
        NativeAccess.Target(typeof(EnvMan), "GetCurrentEnvironment");
        NativeAccess.Target(typeof(EnvMan), "IsWet");
        HeimKit.Core.Diagnostics.PerformanceStages.CaptureArrivals = true;
        HeimKit.Core.Diagnostics.PerformanceStages.EndArrivalFrame(null);
        HeimKit.Core.Diagnostics.PerformanceStages.Record("HeimKit", "player_services", 300, 7, 11, 1);
        int arrivalSamples = 0;
        HeimKit.Core.Diagnostics.PerformanceStages.EndArrivalFrame(sample =>
        {
            if (sample.Module == "HeimKit" && sample.Stage == "player_services" && sample.Maximum == 300 && sample.Items == 7 && sample.Collections == 1) arrivalSamples++;
        });
        HeimKit.Core.Diagnostics.PerformanceStages.CaptureArrivals = false;
        HeimKit.Core.Diagnostics.PerformanceStages.Reset();
        if (arrivalSamples != 1) throw new Exception("Native Mono lost correlated arrival-stage evidence.");
        System.Console.WriteLine("PASS: game Mono preserves arrival timing correlation and resolves passive destination APIs without invoking Unity scene or environment services.");
        NativeAccess.Target(typeof(WorldObjectProvider), "CollectIds", typeof(CandidateSubscription), typeof(Vector3), typeof(int), typeof(bool), typeof(List<ZDOID>));
        NativeAccess.Target(typeof(WorldObjectProvider), "WithinNear", typeof(Vector3), typeof(int), typeof(bool), typeof(Vector3));
        if (!typeof(HeimKit.Core.Scheduling.IImmediateActivation).IsAssignableFrom(typeof(WeatherGuard)))
            throw new Exception("WeatherGuard lost immediate activation priority in game Mono.");
        System.Console.WriteLine("PASS: game Mono resolves cooperative candidate-ID discovery/current-scope validation and retains WeatherGuard's immediate activation contract.");
        _ = NativeAccess.Field<ResourceRoot, float>("m_maxLevel");
        _ = NativeAccess.Field<ResourceRoot, float>("m_regenPerSec");
        NativeAccess.Target(typeof(ResourceRoot), "GetLevel");
        if (HeimKit.Features.SharedMap.SharedMapAncientRoot.Percentage(32.5f, 50) != 65 ||
            HeimKit.Features.SharedMap.SharedMapAncientRoot.Percentage(0, 50) != 0 ||
            HeimKit.Features.SharedMap.SharedMapAncientRoot.Percentage(float.NaN, 50) != null)
            throw new Exception("Native Mono changed Ancient Root reserve calculation semantics.");
        System.Console.WriteLine("PASS: game Mono resolves native root reserve fields/GetLevel and preserves read-only percentage validation.");
        foreach (string member in new[] { "LoadAsync", "get_IsLoading", "get_IsLoaded", "Release" })
            NativeAccess.Target(typeof(SoftReferenceableAssets.SoftReference<GameObject>), member);
        using (var saveLog = new BepInEx.Logging.ManualLogSource("Native save request compatibility"))
        using (var saves = new HeimKit.Core.Lifecycle.ServerSaveRequests(saveLog)) { }
        System.Console.WriteLine("PASS: game Mono resolves async soft-reference APIs and installs/removes native save-request compatibility and timing hooks.");
        var events = (RandEventSystem)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RandEventSystem));
        var raid = (RandomEvent)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RandomEvent));
        NativeAccess.Field<RandEventSystem, RandomEvent>("m_randomEvent")(events) = raid;
        if (!ReferenceEquals(events.GetCurrentRandomEvent(), raid)) throw new Exception("The native server raid getter did not expose current random-event state.");
        if (!HeimKit.Features.SharedMap.SharedMapController.Equal(new byte[] { 0, 1, 2 }, new byte[] { 0, 1, 2 }) ||
            HeimKit.Features.SharedMap.SharedMapController.Equal(new byte[] { 0, 1, 2 }, new byte[] { 0, 1, 3 }))
            throw new Exception("Span-based table equality is unavailable or changed payload semantics in game Mono.");
        System.Console.WriteLine("PASS: installed game Mono exposes independent server raid state and supports span-based table payload equality.");
        var sceneHooks = new Harmony("heimkit.scene_binding_smoke");
        try
        {
            foreach (string name in new[] { "OnDestroy", "ResetZDO" })
                sceneHooks.Patch(NativeAccess.Target(typeof(ZNetView), name), prefix: new HarmonyMethod(typeof(SceneInstances), "Changing"));
            sceneHooks.Patch(NativeAccess.Target(typeof(ZNetScene), "AddInstance", typeof(ZDO), typeof(ZNetView)), postfix: new HarmonyMethod(typeof(SceneInstances), "Added"));
            foreach (string name in new[] { "CreateObjects", "RemoveObjects" })
                sceneHooks.Patch(NativeAccess.Target(typeof(ZNetScene), name, typeof(List<ZDO>), typeof(List<ZDO>)),
                    prefix: new HarmonyMethod(typeof(SceneInstances), "Before"), finalizer: new HarmonyMethod(typeof(SceneInstances), "Completed"));
        }
        finally { sceneHooks.UnpatchSelf(); }
        var firstSceneLease = SceneInstances.Acquire();
        var secondSceneLease = SceneInstances.Acquire();
        firstSceneLease.Dispose();
        var sceneTarget = NativeAccess.Target(typeof(ZNetScene), "RemoveObjects", typeof(List<ZDO>), typeof(List<ZDO>));
        if (Harmony.GetPatchInfo(sceneTarget)?.Owners.Contains("heimkit.scene_instances") != true) throw new Exception("One scene consumer removed another consumer's hooks.");
        secondSceneLease.Dispose();
        if (Harmony.GetPatchInfo(sceneTarget)?.Owners.Contains("heimkit.scene_instances") == true) throw new Exception("Final scene consumer leaked native hooks.");
        System.Console.WriteLine("PASS: installed game Mono installs/removes scene lifecycle prefixes and exception-preserving timing finalizers; native transport state APIs resolve.");
        RuntimeHelpers.RunClassConstructor(typeof(TameTravelPlacement).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(WeatherGuardPatches).TypeHandle);
        using (var log = new BepInEx.Logging.ManualLogSource("Follow recovery native checks"))
        using (var recovery = new TameTravelFollowRecovery(log)) { }
        System.Console.WriteLine("PASS: installed game Mono installs and removes native disk-load and pre-save follow recovery hooks without loading a world.");
        return "PASS: installed game Mono resolves WeatherGuard, TameTravel placement, live-scene and terrain accessors without starting gameplay or server hooks.";
    }
}
