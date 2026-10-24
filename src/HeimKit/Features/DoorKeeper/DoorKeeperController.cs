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
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Core.Players;

namespace HeimKit.Features.DoorKeeper;

internal sealed class DoorKeeperController : IDisposable
{
    private readonly DoorKeeperSettings _settings;
    private readonly ManualLogSource _log;
    private readonly DoorKeeperDiagnostics _diagnostics;
    private readonly PlayerWorldScope _scope;
    private readonly ObjectAuthority _authority;
    private readonly DoorKeeperContext _context;
    private readonly DoorKeeperClearance _clearance = new();
    private readonly HashSet<int> _prefabs = new();
    private readonly Dictionary<ZDOID, (float Opened, float Seen, int State)> _doors = new();
    private readonly Func<Door, bool> _canInteract;
    private readonly Action<Door, long, bool> _use;
    private readonly List<ZDOID> _expired = new();
    private int _contextCursor;
    private long _closed;
    public DoorKeeperController(DoorKeeper module, DoorKeeperSettings settings, ManualLogSource log, PlayerServices players, string directory)
    {
        _settings = settings; _log = log; _scope = WorldObjectProvider.Current.CreateScope("DoorKeeper"); _diagnostics = new(log, settings.DebugLogging);
        _canInteract = NativeAccess.Method<Func<Door, bool>>(typeof(Door), "CanInteract");
        _use = NativeAccess.Method<Action<Door, long, bool>>(typeof(Door), "RPC_UseDoor", typeof(long), typeof(bool));
        foreach (string name in new[] { "wood_door", "wood_gate", "wood_fence_gate", "darkwood_gate", "iron_grate", "stave_gate", "piece_dvergr_wood_door", "piece_hexagonal_door", "flametal_gate", "ashwood_door" })
        {
            var prefab = PrefabRegistry.Current.Prefab(name); var door = PrefabRegistry.Component<Door>(prefab); var view = PrefabRegistry.Component<ZNetView>(prefab);
            if (door != null && view != null && view.m_persistent && PrefabRegistry.Component<Piece>(prefab) != null && door.m_keyItem == null && !door.m_canNotBeClosed && !door.m_invertedOpenClosedText) _prefabs.Add(name.GetStableHashCode());
        }
        if (_prefabs.Count == 0) throw new InvalidOperationException("No supported native doors are available.");
        _context = new();
        try { _authority = new(module.Fail, log, players.Presence, true, "UseDoor", "RPC_Damage", "RPC_Repair", "RPC_Remove"); } catch { _context.Dispose(); throw; }
        try { _scope.Index(z => _prefabs.Contains(z.GetPrefab())); } catch { Dispose(); throw; }
    }
    public void Tick(float now)
    {
        if (!ModuleScheduler.Current.Due(this, "processing", now, _settings.IntervalSeconds.Value)) return;
        using var timing = _diagnostics.OptionalEnabled ? _diagnostics.Measure(now, () => _scope.Records.Count) : null;
        if (!_scope.Refresh(_settings.ScanRangeMeters.Value)) { ReleaseAll(); _context.Clear(); return; }
        _expired.Clear();
        foreach (var pair in _doors)
            if (now - pair.Value.Seen > 10 || ZDOMan.instance.GetZDO(pair.Key) == null) _expired.Add(pair.Key);
        foreach (var id in _expired) _doors.Remove(id);
        int closed = 0, occupied = 0, delayed = 0;
        bool prepared = false, contextReady = true;
        int start = _scope.Records.Count == 0 ? 0 : _contextCursor % _scope.Records.Count;
        for (int visited = 0; visited < _scope.Records.Count; visited++)
        {
            int index = (start + visited) % _scope.Records.Count;
            var zdo = _scope.Records[index];
            if (!zdo.IsValid() || !_prefabs.Contains(zdo.GetPrefab()) || zdo.GetLong(ZDOVars.s_creator) == 0 || !_scope.InRange(zdo.GetPosition(), _settings.ScanRangeMeters.Value)) continue;
            int state = zdo.GetInt(ZDOVars.s_state);
            if (state == 0) { _doors.Remove(zdo.m_uid); continue; }
            if (state != 1 && state != -1) continue;
            if (!_doors.TryGetValue(zdo.m_uid, out var tracked))
            {
                if (_doors.Count >= 1024) continue;
                tracked = (now, now, state);
            }
            if (state != tracked.State) tracked.Opened = now;
            tracked.Seen = now; tracked.State = state; _doors[zdo.m_uid] = tracked;
            if (now - tracked.Opened < _settings.CloseDelaySeconds.Value) { delayed++; continue; }
            if (_clearance.Occupied(_scope, zdo, _settings.ClearanceMeters.Value)) { occupied++; continue; }
            // Rotate one due, clear door's scene preparation per pass. Closed and
            // waiting doors require neither native instances nor server authority.
            if (prepared) continue;
            prepared = true; _contextCursor = index + 1;
            if (!_context.Prepare(_scope, zdo, now)) { contextReady = false; continue; }
            var view = ZNetScene.instance.FindInstance(zdo); var door = view == null ? null : view.GetComponent<Door>();
            if (door == null || !view!.IsValid() || !ReferenceEquals(view.GetZDO(), zdo) || !_canInteract(door)) continue;
            // Native toggle executes synchronously after the closed-state check: no delayed owner-routed toggle.
            if (zdo.GetInt(ZDOVars.s_state) == 0) continue;
            try
            {
                _authority.Retain(zdo);
                _use(door, ZDOMan.GetSessionID(), false);
                if (zdo.GetInt(ZDOVars.s_state) == 0) { closed++; _closed++; _doors.Remove(zdo.m_uid); }
            }
            finally { _authority.Release(zdo.m_uid); }
        }
        if (closed > 0) DiagnosticLog.Write(_log, "DoorKeeper", closed == 1 ? "Closed one door." : $"Closed {closed} doors.");
        _diagnostics.Result(now, closed, _closed, _doors.Count);
        _diagnostics.Scan(now, _scope.Records.Count, _doors.Count, delayed, occupied, contextReady, _context.WaitReason, _context.WaitObject);
    }
    private void ReleaseAll() { foreach (var id in _doors.Keys) _authority.Release(id); _doors.Clear(); }
    public void Dispose() { try { ReleaseAll(); } finally { try { _authority.Dispose(); } finally { try { _scope.Dispose(); } finally { _context.Dispose(); } } } }
}
