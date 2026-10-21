using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HeimKit.Shared;

// Load native terrain and saved modifications only at a bounded set of processing points.
internal sealed class WorldTerrainContext : IDisposable
{
    private static readonly List<WorldTerrainContext> Consumers = new();
    private static readonly Harmony Hooks = new("heimkit.world_terrain_context");
    private const BindingFlags NativeFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static MethodInfo NativeMethod(Type type, string name, params Type[] args) => type.GetMethod(name, NativeFlags, null, args, null)!;
    private static readonly MethodInfo CreateZones = NativeMethod(typeof(ZoneSystem), "CreateLocalZones", typeof(Vector3));
    private static readonly MethodInfo CreateObject = NativeMethod(typeof(ZNetScene), "CreateObject", typeof(ZDO));
    private static readonly MethodInfo CheckLoad = NativeMethod(typeof(TerrainComp), "CheckLoad");
    private static readonly MethodInfo CompilerAwake = NativeMethod(typeof(TerrainComp), "Awake");
    private static readonly FieldInfo Initialized = typeof(TerrainComp).GetField("m_initialized", NativeFlags)!;
    private static readonly FieldInfo CompilerMap = typeof(TerrainComp).GetField("m_hmap", NativeFlags)!;
    private static readonly FieldInfo[] CompilerArrays = new[] { "m_modifiedHeight", "m_levelDelta", "m_smoothDelta", "m_modifiedPaint", "m_paintMask" }
        .Select(name => typeof(TerrainComp).GetField(name, NativeFlags)!).ToArray();
    private readonly Dictionary<ZDOID, (ZDO Record, float Seen)> _retained = new();
    private readonly ZDOMan _world = ZDOMan.instance;
    private float _nextPrune;
    public string? WaitReason { get; private set; }
    public ZDOID WaitObject { get; private set; }
    public WorldTerrainContext()
    {
        if (CreateZones == null || CreateObject == null || CheckLoad == null || CompilerAwake == null || Initialized == null || CompilerMap == null || CompilerArrays.Any(field => field == null))
            throw new MissingMethodException("Native terrain context APIs.");
        if (Consumers.Count == 0)
        {
            try
            {
                foreach (string method in new[] { "CreateObjects", "RemoveObjects" })
                    Hooks.Patch(AccessTools.Method(typeof(ZNetScene), method), prefix: new HarmonyMethod(typeof(WorldTerrainContext), nameof(Instances)));
            }
            catch { Hooks.UnpatchSelf(); throw; }
        }
        Consumers.Add(this);
    }
    public bool Prepare(Vector3 point, IReadOnlyList<ZDO> scope, float now, float radius = 12)
    {
        WaitReason = null; WaitObject = ZDOID.None;
        Prune(now);
        if (!ZoneSystem.instance.LocationsGenerated || Character.InInterior(point)) return false;
        CreateZones.Invoke(ZoneSystem.instance, new object[] { point });
        var hmap = Heightmap.FindHeightmap(point);
        if (hmap == null || !ZoneSystem.instance.IsZoneLoaded(point)) return false;
        foreach (var record in scope)
        {
            var position = record.GetPosition();
            if (Math.Abs(position.x - point.x) > 64 + radius || Math.Abs(position.z - point.z) > 64 + radius) continue;
            var prefab = ZNetScene.instance.GetPrefab(record.GetPrefab());
            if (prefab == null || prefab.GetComponent<TerrainComp>() == null && prefab.GetComponent<TerrainModifier>() == null) continue;
            var compilerMap = Heightmap.FindHeightmap(position);
            if (compilerMap == null || compilerMap.GetPaintMask() == null || compilerMap.HaveQueuedRebuild()) return Wait(record, "terrain_heightmap_unavailable");
            if (!Include(record, now)) return false;
            var instance = ZNetScene.instance.FindInstance(record);
            if (instance == null) { CreateObject.Invoke(ZNetScene.instance, new object[] { record }); instance = ZNetScene.instance.FindInstance(record); }
            if (instance == null) return false;
            var compiler = instance.GetComponent<TerrainComp>();
            if (compiler != null)
            {
                if (!instance.IsValid() || !ReferenceEquals(instance.GetZDO(), record)) return Wait(record, "terrain_compiler_record_unavailable");
                // Native Awake can return before Initialize if its heightmap was not
                // present at creation. CheckLoad assumes initialized arrays. Resume
                // that exact early-return path only after its heightmap exists.
                if (!(bool)Initialized.GetValue(compiler) && ReferenceEquals(CompilerMap.GetValue(compiler), null))
                    CompilerAwake.Invoke(compiler, Array.Empty<object>());
                if (!CompilerInitialized(compiler)) return Wait(record, "terrain_compiler_uninitialized");
                if (!ReferenceEquals(CompilerMap.GetValue(compiler), compilerMap)) return Wait(record, "terrain_compiler_heightmap_changed");
                CheckLoad.Invoke(compiler, Array.Empty<object>());
            }
        }
        return !Heightmap.HaveQueuedRebuild(point, radius);
    }
    internal static bool CompilerInitialized(TerrainComp compiler) => (bool)Initialized.GetValue(compiler) &&
        !ReferenceEquals(CompilerMap.GetValue(compiler), null) && CompilerArrays.All(field => field.GetValue(compiler) is Array array && array.Length > 0);
    private bool Wait(ZDO record, string reason) { WaitObject = record.m_uid; WaitReason = reason; return false; }
    public bool Include(ZDO record, float now)
    {
        if (_retained.Count >= 8192 && !_retained.ContainsKey(record.m_uid)) return false;
        _retained[record.m_uid] = (record, now); return true;
    }
    private static void Instances(List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
    {
        bool any = false;
        foreach (var consumer in Consumers)
        {
            if (!ReferenceEquals(consumer._world, ZDOMan.instance)) continue;
            if (consumer._retained.Count > 0) consumer.Prune(Time.unscaledTime);
            any |= consumer._retained.Count > 0;
        }
        if (!any) return;
        var seen = new HashSet<ZDOID>(currentNearObjects.Select(z => z.m_uid));
        foreach (var c in Consumers.Where(c => ReferenceEquals(c._world, ZDOMan.instance)))
            foreach (var pair in c._retained)
                if (pair.Value.Record.IsValid() && pair.Value.Record.m_uid == pair.Key && seen.Add(pair.Key)) currentNearObjects.Add(pair.Value.Record);
    }
    private void Prune(float now)
    {
        if (now < _nextPrune) return; _nextPrune = now + 1;
        foreach (var id in _retained.Where(p => now - p.Value.Seen > 10 || !p.Value.Record.IsValid() || p.Value.Record.m_uid != p.Key).Select(p => p.Key).ToArray()) _retained.Remove(id);
    }
    public void Clear() => _retained.Clear();
    public void Dispose() { Consumers.Remove(this); _retained.Clear(); if (Consumers.Count == 0) Hooks.UnpatchSelf(); }
}
