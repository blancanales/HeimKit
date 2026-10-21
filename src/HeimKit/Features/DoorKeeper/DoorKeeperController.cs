using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Shared;
using HeimKit.Shared.Players;

namespace HeimKit.Features.DoorKeeper;

internal sealed class DoorKeeperController : IDisposable
{
    private readonly DoorKeeperSettings _settings;
    private readonly ManualLogSource _log;
    private readonly DoorKeeperDiagnostics _diagnostics;
    private readonly PlayerWorldScope _scope;
    private readonly ServerObjectAuthority _authority;
    private readonly DoorKeeperContext _context;
    private readonly DoorKeeperClearance _clearance = new();
    private readonly HashSet<int> _prefabs = new();
    private readonly Dictionary<ZDOID, (float Opened, float Seen, int State)> _doors = new();
    private readonly MethodInfo _canInteract = AccessTools.Method(typeof(Door), "CanInteract");
    private readonly MethodInfo _use = AccessTools.Method(typeof(Door), "RPC_UseDoor");
    private float _next;
    private long _closed;
    public DoorKeeperController(DoorKeeper module, DoorKeeperSettings settings, ManualLogSource log, PlayerServices players, string directory)
    {
        _settings = settings; _log = log; _scope = new(players.Presence); _diagnostics = new(log, settings.DebugLogging);
        if (_canInteract?.ReturnType != typeof(bool) || _use == null) throw new MissingMethodException("Native Door interaction APIs.");
        foreach (string name in new[] { "wood_door", "wood_gate", "wood_fence_gate", "darkwood_gate", "iron_grate", "stave_gate", "piece_dvergr_wood_door", "piece_hexagonal_door", "flametal_gate", "ashwood_door" })
        {
            var prefab = ZNetScene.instance.GetPrefab(name); var door = prefab?.GetComponent<Door>(); var view = prefab?.GetComponent<ZNetView>();
            if (door != null && view != null && view.m_persistent && prefab!.GetComponent<Piece>() != null && door.m_keyItem == null && !door.m_canNotBeClosed && !door.m_invertedOpenClosedText) _prefabs.Add(name.GetStableHashCode());
        }
        if (_prefabs.Count == 0) throw new InvalidOperationException("No supported native doors are available.");
        _context = new();
        try { _authority = new(module.Fail, log, players.Presence, true, "UseDoor", "RPC_Damage", "RPC_Repair", "RPC_Remove"); } catch { _context.Dispose(); throw; }
    }
    public void Tick(float now)
    {
        if (now < _next) return; _next = now + _settings.IntervalSeconds.Value;
        using var timing = _settings.DebugLogging.Enabled ? _diagnostics.Measure(now, () => _scope.Records.Count) : null;
        if (!_scope.Refresh(_settings.ScanRangeMeters.Value)) { ReleaseAll(); _context.Clear(); return; }
        foreach (var id in _doors.Keys.ToArray())
            if (now - _doors[id].Seen > 10 || ZDOMan.instance.GetZDO(id) == null) { _doors.Remove(id); _authority.Release(id); }
        if (!_context.Prepare(_scope, now)) { _diagnostics.Scan(now, _scope.Records.Count, _doors.Count, 0, 0, false, _context.WaitReason, _context.WaitObject); ReleaseAll(); return; }
        int closed = 0, occupied = 0, delayed = 0;
        foreach (var zdo in _scope.Records)
        {
            if (!zdo.IsValid() || !_prefabs.Contains(zdo.GetPrefab()) || zdo.GetLong(ZDOVars.s_creator) == 0 || !_scope.InRange(zdo.GetPosition(), _settings.ScanRangeMeters.Value)) continue;
            int state = zdo.GetInt(ZDOVars.s_state);
            if (state != 0 && state != 1 && state != -1) continue;
            if (!_doors.TryGetValue(zdo.m_uid, out var tracked))
            {
                if (_doors.Count >= 1024) continue;
                _authority.Retain(zdo); tracked = (now, now, state);
            }
            if (state == 0 || state != tracked.State) tracked.Opened = now;
            tracked.Seen = now; tracked.State = state; _doors[zdo.m_uid] = tracked;
            if (state == 0) continue;
            if (now - tracked.Opened < _settings.CloseDelaySeconds.Value) { delayed++; continue; }
            if (_clearance.Occupied(_scope, zdo, _settings.ClearanceMeters.Value)) { occupied++; continue; }
            var view = ZNetScene.instance.FindInstance(zdo); var door = view?.GetComponent<Door>();
            if (door == null || !view!.IsOwner() || !(bool)_canInteract.Invoke(door, Array.Empty<object>())) continue;
            // Native toggle executes synchronously after the closed-state check: no delayed owner-routed toggle.
            if (zdo.GetInt(ZDOVars.s_state) == 0) continue;
            _use.Invoke(door, new object[] { ZDOMan.GetSessionID(), false });
            if (zdo.GetInt(ZDOVars.s_state) == 0) { closed++; _closed++; _doors[zdo.m_uid] = (now, now, 0); }
        }
        if (closed > 0) DiagnosticLog.Write(_log, "DoorKeeper", DiagnosticEvent.Result, closed == 1 ? "Closed one door." : $"Closed {closed} doors.");
        _diagnostics.Result(now, closed, _closed, _doors.Count);
        _diagnostics.Scan(now, _scope.Records.Count, _doors.Count, delayed, occupied, true);
    }
    private void ReleaseAll() { foreach (var id in _doors.Keys.ToArray()) _authority.Release(id); _doors.Clear(); }
    public void Dispose() { try { ReleaseAll(); } finally { try { _authority.Dispose(); } finally { _context.Dispose(); } } }
}
