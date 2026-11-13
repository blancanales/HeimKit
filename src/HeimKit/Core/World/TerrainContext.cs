using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HeimKit.Core.World;

// Load native terrain and saved modifications only at a bounded set of processing points.
internal sealed class TerrainContext : IDisposable
{
    private static readonly List<TerrainContext> Consumers = new();
    private static readonly Harmony Hooks = new("heimkit.world_terrain_context");
    private static readonly HashSet<ZDOID> InstanceIds = new();
    private static SceneListPass _scenePass;
    private static readonly List<ZDO> CreationNear = new(), CreationDistant = new();
    private static readonly Func<ZDO, bool> CreationReady = CanCreate;
    private static Exception? _creationFailure;
    private static class Accessors
    {
        static Accessors() { }
        internal static readonly Func<ZoneSystem, Vector3, bool> PopulateZones = NativeAccess.Method<Func<ZoneSystem, Vector3, bool>>(typeof(ZoneSystem), "CreateLocalZones", typeof(Vector3));
        internal static readonly Func<ZNetScene, ZDO, GameObject> CreateObject = NativeAccess.Method<Func<ZNetScene, ZDO, GameObject>>(typeof(ZNetScene), "CreateObject", typeof(ZDO));
        internal static readonly Action<TerrainComp> CheckLoad = NativeAccess.Method<Action<TerrainComp>>(typeof(TerrainComp), "CheckLoad");
        internal static readonly Action<TerrainComp> CompilerAwake = NativeAccess.Method<Action<TerrainComp>>(typeof(TerrainComp), "Awake");
        internal static readonly AccessTools.FieldRef<TerrainComp, bool> Initialized = NativeAccess.Field<TerrainComp, bool>("m_initialized");
        internal static readonly AccessTools.FieldRef<TerrainComp, Heightmap> CompilerMap = NativeAccess.Field<TerrainComp, Heightmap>("m_hmap");
        internal static readonly AccessTools.FieldRef<TerrainComp, bool[]> ModifiedHeight = NativeAccess.Field<TerrainComp, bool[]>("m_modifiedHeight");
        internal static readonly AccessTools.FieldRef<TerrainComp, float[]> LevelDelta = NativeAccess.Field<TerrainComp, float[]>("m_levelDelta");
        internal static readonly AccessTools.FieldRef<TerrainComp, float[]> SmoothDelta = NativeAccess.Field<TerrainComp, float[]>("m_smoothDelta");
        internal static readonly AccessTools.FieldRef<TerrainComp, bool[]> ModifiedPaint = NativeAccess.Field<TerrainComp, bool[]>("m_modifiedPaint");
        internal static readonly AccessTools.FieldRef<TerrainComp, Color[]> PaintMask = NativeAccess.Field<TerrainComp, Color[]>("m_paintMask");
        internal static readonly MethodInfo CreateNear = NativeAccess.Target(typeof(ZNetScene), "CreateObjectsSorted", typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType());
        internal static readonly MethodInfo CreateDistant = NativeAccess.Target(typeof(ZNetScene), "CreateDistantObjects", typeof(List<ZDO>), typeof(int), typeof(int).MakeByRefType());
    }
    internal static void CheckCompatibility() => ValidateNative(() => { _ = Accessors.PopulateZones; _ = Accessors.PaintMask; });
    internal static void ValidateNative(Action resolve)
    {
        try { resolve(); }
        catch (Exception error) { throw new MissingMemberException("Native terrain context APIs.", error); }
    }
    private readonly Dictionary<ZDOID, (ZDO Record, float Seen)> _retained = new();
    private readonly ZDOMan _world = ZDOMan.instance;
    private readonly PrefabCatalog<bool> _terrainDefinitions = PrefabRegistry.Current.Catalog<bool>("Terrain");
    private readonly List<ZDOID> _expired = new();
    private float _nextPrune;
    private readonly IDisposable _sceneLease;
    public string? WaitReason { get; private set; }
    public ZDOID WaitObject { get; private set; }
    public TerrainContext()
    {
        CheckCompatibility();
        ThrowIfFailed();
        if (Consumers.Count == 0)
        {
            try
            {
                foreach (string method in new[] { "CreateObjects", "RemoveObjects" })
                    Hooks.Patch(AccessTools.Method(typeof(ZNetScene), method), prefix: new HarmonyMethod(typeof(TerrainContext), nameof(Instances)));
                Hooks.Patch(Accessors.CreateNear, prefix: new HarmonyMethod(typeof(TerrainContext), nameof(FilterNear)));
                Hooks.Patch(Accessors.CreateDistant, prefix: new HarmonyMethod(typeof(TerrainContext), nameof(FilterDistant)));
            }
            catch { Hooks.UnpatchSelf(); throw; }
        }
        Consumers.Add(this);
        try { _sceneLease = SceneInstances.Acquire(WorldObjectProvider.Current?.Logger); }
        catch { Consumers.Remove(this); if (Consumers.Count == 0) Hooks.UnpatchSelf(); throw; }
    }
    // Filter at the native loop boundary, AFTER consumers extend scene coverage.
    // Returning null from CreateObject would destroy the persistent ZDO as an
    // invalid prefab. Keep removal/retention lists intact, and never mask Update errors.
    private static void FilterNear(ref List<ZDO> currentNearObjects)
        => currentNearObjects = AdmitCreation(currentNearObjects, CreationNear);
    private static void FilterDistant(ref List<ZDO> objects)
        => objects = AdmitCreation(objects, CreationDistant);
    private static List<ZDO> AdmitCreation(List<ZDO> source, List<ZDO> buffer)
    {
        if (_creationFailure == null)
            try { return FilterCreation(source, buffer, CreationReady); }
            catch (Exception error) { _creationFailure = error; }
        // Native callbacks cannot escape ModuleHost every scene frame. Hold new
        // creation until the owning controllers observe and report this failure;
        // existing instances and persistent/removal lists remain untouched.
        buffer.Clear(); return buffer;
    }
    internal static void ThrowIfFailed()
    {
        if (_creationFailure != null) throw new InvalidOperationException("Native terrain scene admission failed.", _creationFailure);
    }
    internal static List<ZDO> FilterCreation(List<ZDO> source, List<ZDO> buffer, Func<ZDO, bool> ready)
    {
        buffer.Clear(); bool filtered = false;
        for (int i = 0; i < source.Count; i++)
        {
            var record = source[i];
            if (!record.Created && !ready(record))
            {
                if (!filtered) { for (int prior = 0; prior < i; prior++) buffer.Add(source[prior]); filtered = true; }
            }
            else if (filtered) buffer.Add(record);
        }
        return filtered ? buffer : source;
    }
    private static bool CanCreate(ZDO record)
    {
        var prefab = PrefabRegistry.Current.Prefab(record.GetPrefab());
        if (prefab == null || PrefabRegistry.Component<TerrainComp>(prefab) == null) return true;
        var point = record.GetPosition();
        var map = Heightmap.FindHeightmap(point);
        return ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(point) && map != null &&
            map.GetPaintMask() != null && !map.HaveQueuedRebuild();
    }
    public bool Prepare(Vector3 point, IReadOnlyList<ZDO> scope, float now, float radius = 12)
    {
        ThrowIfFailed();
        using var measured = ServerPerformance.MeasureNativeContext();
        using var timing = PerformanceStages.Measure("Core", "terrain_prepare", scope.Count);
        WaitReason = null; WaitObject = ZDOID.None;
        Prune(now);
        if (!ZoneSystem.instance.LocationsGenerated || Character.InInterior(point)) return false;
        Accessors.PopulateZones(ZoneSystem.instance, point);
        var hmap = Heightmap.FindHeightmap(point);
        if (hmap == null || !ZoneSystem.instance.IsZoneLoaded(point)) return false;
        for (int index = 0; index < scope.Count; index++)
        {
            var record = scope[index];
            var position = record.GetPosition();
            if (Math.Abs(position.x - point.x) > 64 + radius || Math.Abs(position.z - point.z) > 64 + radius) continue;
            int hash = record.GetPrefab();
            if (!_terrainDefinitions.TryGetValue(hash, out bool terrain))
            {
                var prefab = PrefabRegistry.Current.Prefab(hash);
                if (prefab == null) continue;
                terrain = PrefabRegistry.Component<TerrainComp>(prefab) != null || PrefabRegistry.Component<TerrainModifier>(prefab) != null;
                if (_terrainDefinitions.Count < 4096) _terrainDefinitions[hash] = terrain;
            }
            if (!terrain) continue;
            var compilerMap = Heightmap.FindHeightmap(position);
            if (compilerMap == null || compilerMap.GetPaintMask() == null || compilerMap.HaveQueuedRebuild()) return Wait(record, "terrain_heightmap_unavailable");
            if (!Include(record, now)) return false;
            var instance = ZNetScene.instance.FindInstance(record);
            if (instance == null) { Accessors.CreateObject(ZNetScene.instance, record); instance = ZNetScene.instance.FindInstance(record); }
            if (instance == null) return false;
            var compiler = instance.GetComponent<TerrainComp>();
            if (compiler != null)
            {
                if (!instance.IsValid() || !ReferenceEquals(instance.GetZDO(), record)) return Wait(record, "terrain_compiler_record_unavailable");
                // Native Awake can return before Initialize if its heightmap was not
                // present at creation. CheckLoad assumes initialized arrays. Resume
                // that exact early-return path only after its heightmap exists.
                if (!(bool)Accessors.Initialized(compiler) && ReferenceEquals(Accessors.CompilerMap(compiler), null))
                    Accessors.CompilerAwake(compiler);
                if (!CompilerInitialized(compiler)) return Wait(record, "terrain_compiler_uninitialized");
                if (!ReferenceEquals(Accessors.CompilerMap(compiler), compilerMap)) return Wait(record, "terrain_compiler_heightmap_changed");
                Accessors.CheckLoad(compiler);
            }
        }
        return !Heightmap.HaveQueuedRebuild(point, radius);
    }
    internal static bool CompilerInitialized(TerrainComp compiler) => (bool)Accessors.Initialized(compiler) &&
        !ReferenceEquals(Accessors.CompilerMap(compiler), null) && Accessors.ModifiedHeight(compiler)?.Length > 0 && Accessors.LevelDelta(compiler)?.Length > 0 && Accessors.SmoothDelta(compiler)?.Length > 0 && Accessors.ModifiedPaint(compiler)?.Length > 0 && Accessors.PaintMask(compiler)?.Length > 0;
    private bool Wait(ZDO record, string reason) { WaitObject = record.m_uid; WaitReason = reason; return false; }
    public bool Include(ZDO record, float now)
    {
        if (_retained.Count >= 8192 && !_retained.ContainsKey(record.m_uid)) return false;
        _retained[record.m_uid] = (record, now); return true;
    }
    private static void Instances(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
    {
        if (!_scenePass.Enter(Time.frameCount, currentNearObjects, ZDOMan.instance)) return;
        bool any = false;
        foreach (var consumer in Consumers)
        {
            if (!ReferenceEquals(consumer._world, ZDOMan.instance)) continue;
            if (consumer._retained.Count > 0) consumer.Prune(Time.unscaledTime);
            any |= consumer._retained.Count > 0;
        }
        if (!any) return;
        var seen = InstanceIds; seen.Clear();
        foreach (var record in currentNearObjects) seen.Add(record.m_uid);
        foreach (var c in Consumers)
            if (ReferenceEquals(c._world, ZDOMan.instance))
            foreach (var pair in c._retained)
                if (pair.Value.Record.IsValid() && pair.Value.Record.m_uid == pair.Key && seen.Add(pair.Key)) currentNearObjects.Add(pair.Value.Record);
        seen.Clear();
    }
    private void Prune(float now)
    {
        if (now < _nextPrune) return; _nextPrune = now + 1;
        _expired.Clear();
        foreach (var pair in _retained)
            if (now - pair.Value.Seen > 10 || !pair.Value.Record.IsValid() || pair.Value.Record.m_uid != pair.Key) _expired.Add(pair.Key);
        foreach (var id in _expired) _retained.Remove(id);
        _expired.Clear();
    }
    public void Clear() { _retained.Clear(); _scenePass.Clear(); }
    public void Dispose() { if (!Consumers.Remove(this)) return; _scenePass.Clear(); _sceneLease.Dispose(); _retained.Clear(); if (Consumers.Count == 0) { Hooks.UnpatchSelf(); CreationNear.Clear(); CreationDistant.Clear(); _creationFailure = null; } }
}
