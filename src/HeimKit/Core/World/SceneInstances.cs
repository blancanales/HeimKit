using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using UnityEngine;

namespace HeimKit.Core.World;

// Scene bindings are transient. A destroyed Unity view must not remain in the
// native dictionary, even when its persistent world record is still valid.
internal sealed class SceneInstances : IDisposable
{
    private static readonly Harmony Hooks = new("heimkit.scene_instances");
    private static class Accessors
    {
        static Accessors() { }
        internal static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> Entries = NativeAccess.Field<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
    }
    private static SceneInstances? _current;
    private static int _leases;
    private ZNetScene _scene = ZNetScene.instance;
    private ZDOMan _world = ZDOMan.instance;
    private readonly Dictionary<ZDO, ZNetView> _dirty = new();
    private readonly List<ZDO> _remove = new();
    private readonly List<ZDO> _live = new();
    private readonly List<Vector3> _positions = new();
    private readonly List<ActiveSceneArea> _areas = new();
    private int _nearDistance;
    private bool _classic;
    private float _zoneSize;
    private readonly ManualLogSource? _log;
    private bool _audit = true, _disposed;
    private int _frame = -1, _revision, _cachedRevision = -1;
    private Exception? _failure;
    private long _pendingRepairs;
    private ZDOID _example;
    private int _examplePrefab;
    private float _nextNotice;
    private SceneInstances(ManualLogSource? log) => _log = log;
    static SceneInstances() { }
    private sealed class Lease : IDisposable
    {
        private bool _done;
        public void Dispose() { if (_done) return; _done = true; if (--_leases == 0) { _current?.Dispose(); _current = null; } }
    }
    internal static IDisposable Acquire(ManualLogSource? log = null)
    {
        if (_leases == 0)
        {
            var service = new SceneInstances(log);
            try
            {
                _ = Accessors.Entries;
                Hooks.Patch(NativeAccess.Target(typeof(ZNetView), "OnDestroy"), prefix: new HarmonyMethod(typeof(SceneInstances), nameof(Changing)));
                Hooks.Patch(NativeAccess.Target(typeof(ZNetView), "ResetZDO"), prefix: new HarmonyMethod(typeof(SceneInstances), nameof(Changing)));
                Hooks.Patch(NativeAccess.Target(typeof(ZNetScene), "AddInstance", typeof(ZDO), typeof(ZNetView)), postfix: new HarmonyMethod(typeof(SceneInstances), nameof(Added)));
                foreach (string method in new[] { "CreateObjects", "RemoveObjects" })
                    Hooks.Patch(NativeAccess.Target(typeof(ZNetScene), method, typeof(List<ZDO>), typeof(List<ZDO>)),
                        prefix: new HarmonyMethod(typeof(SceneInstances), nameof(Before)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(SceneInstances), nameof(Completed)));
                _current = service;
            }
            catch { Hooks.UnpatchSelf(); throw; }
        }
        _leases++; return new Lease();
    }
    private bool Current => !_disposed && !ReferenceEquals(_scene, null) && !ReferenceEquals(_world, null) && ReferenceEquals(_scene, ZNetScene.instance) && ReferenceEquals(_world, ZDOMan.instance);
    // A negative value means no current lease can guarantee native binding observation.
    internal static long BindingRevision => _current?.Current == true ? _current._revision : -1;
    private static void Changing(ZNetView __instance)
    {
        var service = _current; if (service == null || !service.Current) return;
        try
        {
            var record = __instance.GetZDO(); if (record == null) return;
            if (service._dirty.Count < 65536) service._dirty[record] = __instance;
            else service._audit = true;
            service._revision++;
        }
        catch (Exception error) { service._failure ??= error; }
    }
    private static void Added(ZDO zdo, ZNetView nview)
    {
        var service = _current; if (service == null || !service.Current) return;
        service._revision++;
        if (service._dirty.Count < 65536) service._dirty[zdo] = nview; else service._audit = true;
    }
    private static bool Before(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        var service = _current; if (service == null) return true;
        if (!service.Current)
        {
            if (ZNetScene.instance == null || ZDOMan.instance == null) return true;
            service._scene = ZNetScene.instance; service._world = ZDOMan.instance;
            service._dirty.Clear(); service._live.Clear(); service._audit = true; service._frame = -1;
        }
        try { service.Reconcile(); return service._failure == null; }
        catch (Exception error) { service._failure ??= error; return false; }
    }
    private static Exception? Completed(Exception? __exception, long __state)
    {
        if (__state != 0) ServerPerformance.ScenePass.Add(Stopwatch.GetTimestamp() - __state);
        if (__exception != null && _current != null) _current._audit = true;
        return __exception;
    }
    internal static void ThrowIfFailed()
    {
        if (_current?._failure != null) throw new InvalidOperationException("Native scene binding maintenance failed.", _current._failure);
    }
    internal static bool BindingMatches(ZDO record, ZDO? observed, bool alive) => alive && ReferenceEquals(observed, record);
    internal static bool BindingValid(ZDO record, ZNetView? view) => BindingMatches(record, ReferenceEquals(view, null) ? null : view.GetZDO(), view != null);
    internal static bool RemoveBinding(Dictionary<ZDO, ZNetView> entries, ZDO record, ZNetView? expected, bool valid)
    {
        if (valid || !entries.TryGetValue(record, out var current) || !ReferenceEquals(current, expected)) return false;
        entries.Remove(record); return true;
    }
    private void Reconcile()
    {
        using var timing = ServerPerformance.Measure(ServerPerformance.SceneMaintenance);
        var entries = Accessors.Entries(_scene); _remove.Clear();
        if (_audit)
        {
            foreach (var pair in entries) if (!BindingValid(pair.Key, pair.Value)) _remove.Add(pair.Key);
        }
        else foreach (var pair in _dirty)
            if (entries.TryGetValue(pair.Key, out var view) && ReferenceEquals(view, pair.Value) && !BindingValid(pair.Key, view)) _remove.Add(pair.Key);
        ZDO? example = null;
        foreach (var record in _remove)
        {
            var view = entries[record];
            if (!RemoveBinding(entries, record, view, BindingValid(record, view))) continue;
            // Never delete a ZDO or touch a replacement view/record. Native loading
            // may recreate this original record if it is still in retained scope.
            if (record.IsValid() && ReferenceEquals(_world.GetZDO(record.m_uid), record)) record.Created = false;
            example ??= record; _pendingRepairs++; _revision++;
        }
        _dirty.Clear(); _remove.Clear(); _audit = false;
        if (example != null) { _example = example.m_uid; _examplePrefab = example.GetPrefab(); }
        Report(Time.unscaledTime);
    }
    private void Report(float now, bool force = false)
    {
        if (_pendingRepairs == 0 || _log == null || !force && now < _nextNotice) return;
        long count = _pendingRepairs; _pendingRepairs = 0; _nextNotice = now + 60;
        DiagnosticLog.Write(_log, "HeimKit", "Retired " + count + " stale server scene binding(s); original world records were preserved. Example: " + _example + ", prefab hash " + _examplePrefab + ".", LogLevel.Warning);
    }
    internal static void Include(List<ZDO> near, IReadOnlyList<Vector3> positions, HashSet<ZDOID> seen)
    {
        using var timing = ServerPerformance.Measure(ServerPerformance.SceneRetention);
        var service = _current ?? throw new InvalidOperationException("Live scene retention requires a scene binding lease.");
        using var evidence = PerformanceStages.Measure("HeimKit", "scene_retention", Accessors.Entries(service._scene).Count);
        service.Reconcile();
        var distance = ZNet.instance.GetSyncedSimulationDistance();
        float zoneSize = ZoneSystem.instance.m_zoneSize;
        bool same = service._frame == Time.frameCount && service._cachedRevision == service._revision && service._positions.Count == positions.Count &&
            service._nearDistance == distance.NearSimulationDistance && service._classic == distance.IsClassic && service._zoneSize == zoneSize;
        if (same) for (int i = 0; i < positions.Count; i++) if (positions[i] != service._positions[i]) { same = false; break; }
        if (!same)
        {
            service._live.Clear(); service._positions.Clear(); service._areas.Clear();
            service._nearDistance = distance.NearSimulationDistance; service._classic = distance.IsClassic; service._zoneSize = zoneSize;
            for (int i = 0; i < positions.Count; i++)
            {
                service._positions.Add(positions[i]);
                service._areas.Add(new ActiveSceneArea(ZoneSystem.GetZonePos(ZoneSystem.GetZone(positions[i])), zoneSize, distance.NearSimulationDistance, distance.IsClassic));
            }
            foreach (var pair in Accessors.Entries(service._scene))
            {
                if (!pair.Key.IsValid() || !BindingValid(pair.Key, pair.Value)) continue;
                var point = pair.Key.GetPosition();
                for (int i = 0; i < service._areas.Count; i++)
                    if (service._areas[i].Contains(point)) { service._live.Add(pair.Key); break; }
            }
            service._frame = Time.frameCount; service._cachedRevision = service._revision;
        }
        foreach (var record in service._live) if (record.IsValid() && record.Created && seen.Add(record.m_uid)) near.Add(record);
    }
    public void Dispose()
    {
        if (_disposed) return;
        try { if (Current) Reconcile(); }
        finally
        {
            _disposed = true; Hooks.UnpatchSelf();
            try { if (_pendingRepairs > 0 && _log != null) Report(Time.unscaledTime, force: true); }
            finally { _dirty.Clear(); _remove.Clear(); _live.Clear(); _positions.Clear(); _areas.Clear(); }
        }
    }
}
