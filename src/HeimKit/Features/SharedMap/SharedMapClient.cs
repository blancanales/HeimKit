using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Client;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Features.SharedMap.Models;
using UnityEngine;

namespace HeimKit.Features.SharedMap;
internal sealed class SharedMapClient : IDisposable
{
    private sealed class ImportState
    {
        internal SharedMapPersonalLedger Ledger = null !;
        internal List<MapPin> Rows = null !;
        internal string[]? Keys;
        internal List<Minimap.PinData>? Held;
    }

    private readonly ClientSettings _settings;
    private readonly ClientLink _link;
    private readonly ManualLogSource _log;
    private readonly Harmony _hooks = new("heimkit.sharedmap.personal");
    private readonly Harmony _tint = new("heimkit.sharedmap.portal.tint");
    private readonly ClientTraceWriter _writer;
    private readonly DiagnosticBudget _budget = new();
    private readonly Action<string, string> _tracePortalStatus;
    private SharedMapPersonalLedger? _ledger;
    private SharedMapLocalDiscovery? _local;
    private ZRpc? _server;
    private Minimap? _map;
    private Player? _player;
    private long _world, _profile;
    private int _request, _received;
    private float _next, _lastHost;
    private bool _failed;
    private int _policyToken, _categories, _effectiveCategories;
    private readonly HashSet<string> _exclusions = new();
    private readonly Dictionary<string, string> _aliases = new();
    private readonly HashSet<string> _groupAnchors = new();
    private float _portalDistance = 60;
    private float _nextPolicy;
    private AccessTools.FieldRef<Minimap, bool>? _loaded;
    private static SharedMapClient? _current;
    [ThreadStatic]
    private static ZDO? _readTable;
    internal SharedMapClient(ClientSettings settings, ClientLink link, ManualLogSource log)
    {
        _tracePortalStatus = Trace;
        _settings = settings;
        _link = link;
        _log = log;
        _writer = new(log, "HeimKit-SharedMap-client.log");
        try
        {
            _tint.Patch(NativeAccess.Target(typeof(Minimap), "UpdatePins"),
                transpiler: new HarmonyMethod(typeof(SharedMapPortalTint), nameof(SharedMapPortalTint.Transpile)));
        }
        catch (Exception error)
        {
            DiagnosticLog.Write(log, "SharedMap", CoreLogTopics.Lifecycle, "Portal highlight unavailable.", level: LogLevel.Warning, error: error);
        }
    }

    internal void Tick(float now)
    {
        var map = Minimap.instance;
        var player = Player.m_localPlayer;
        if (!_settings.PersonalMarkers.Value || Game.m_noMap || map == null || player == null || ZNet.World == null || ZNet.instance == null || ZDOMan.instance == null || ZoneSystem.instance == null || Game.instance == null)
        {
            if (_player != null || _map != null || _ledger != null || _failed)
                Close();
            return;
        }

        long profile = player.GetPlayerID(), world = ZNet.instance.GetWorldUID();
        if (profile == 0 || world == 0 || (_failed && ReferenceEquals(_map, map) && ReferenceEquals(_player, player) && _world == world && _profile == profile))
            return;
        try
        {
            _loaded ??= NativeAccess.Field<Minimap, bool>("m_hasGenerated");
            if (!_loaded(map))
                return;
            if (!ReferenceEquals(map, _map) || !ReferenceEquals(player, _player) || _world != world || _profile != profile)
            {
                Close();
                _map = map;
                _player = player;
                _profile = profile;
                _world = world;
                _ledger = new(Path.GetDirectoryName(_settings.ConfigPath)!, map, world, profile);
                _hooks.Patch(NativeAccess.Target(typeof(Minimap), "AddSharedMapData", typeof(byte[])), prefix: new HarmonyMethod(typeof(SharedMapClient), nameof(BeforeRead)), postfix: new HarmonyMethod(typeof(SharedMapClient), nameof(AfterRead)), finalizer: new HarmonyMethod(typeof(SharedMapClient), nameof(FinishRead)));
                _hooks.Patch(NativeAccess.Target(typeof(MapTable), "OnRead", typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData), typeof(bool)), prefix: new HarmonyMethod(typeof(SharedMapClient), nameof(BeginTable)), finalizer: new HarmonyMethod(typeof(SharedMapClient), nameof(EndTable)));
                _hooks.Patch(NativeAccess.Target(typeof(Teleport), "Interact", typeof(Humanoid), typeof(bool), typeof(bool)),
                    postfix: new HarmonyMethod(typeof(SharedMapClient), nameof(EnteredDungeon)));
                _current = this;
                _lastHost = float.NegativeInfinity;
            }

            if (_failed || _ledger == null)
                return;
            var rpc = ZNet.instance.IsServer() ? null : ZNet.instance.GetServerPeer()?.m_rpc;
            if (!ReferenceEquals(rpc, _server))
            {
                _server?.Unregister(SharedMapPersonalMarkers.Reply);
                _server?.Unregister(SharedMapPersonalMarkers.PolicyReply);
                _policyToken = _categories = _effectiveCategories = 0;
                _nextPolicy = 0;
                _groupAnchors.Clear();
                _aliases.Clear();
                _exclusions.Clear();
                _server = rpc;
                rpc?.Register<ZPackage>(SharedMapPersonalMarkers.Reply, Receive);
                rpc?.Register<ZPackage>(SharedMapPersonalMarkers.PolicyReply, ReceivePolicy);
            }

            if (now >= _nextPolicy)
            {
                _nextPolicy = now + (_policyToken == 0 ? 3 : 10);
                if (rpc?.IsConnected() == true)
                    rpc.Invoke(SharedMapPersonalMarkers.PolicyQuery, _policyToken);
                else if (SharedMapController.Active?.LocalPolicy(_policyToken) is byte[] localPolicy)
                    AcceptPolicy(localPolicy);
                RefreshCategories();
            }

            bool standalone = rpc != null && _policyToken == 0 && _link.State == LinkState.ClientOnly && now - _lastHost > 15;
            if (!_player!.IsDead() && (standalone || _effectiveCategories != 0))
            {
                _local ??= new();
                if (_local.Tick(now, player.transform.position, map.m_exploreRadius, _portalDistance) is { } rows)
                {
                    if (_local.PortalAliases.Count > 0) _ledger.ReconcileAliases(_local.PortalAliases, pendingOnly: !standalone);
                    if (!standalone)
                    {
                        int observed = rows.Count;
                        var groups = new Dictionary<string, SharedMapPersonalMarker>();
                        foreach (var row in rows)
                        {
                            if (!LocalAllowed(row)) continue;
                            // The host publishes the centre, label and count for known groups.
                            if (SharedMapTableData.CanonicalOwner(row.Pin.Owner) == SharedMapTableData.PortalOwner &&
                                (_aliases.ContainsKey(row.Id) || _groupAnchors.Contains(row.Id))) continue;
                            if (_aliases.TryGetValue(row.Id, out var group))
                                groups[group] = new(group, row.Pin);
                            else groups[row.Id] = row;
                        }
                        rows = new List<SharedMapPersonalMarker>(groups.Values);
                        Trace("local_scan", $"observed={observed} allowed_groups={rows.Count} category_mask={_effectiveCategories}");
                        foreach (var row in rows) row.PendingLocal = true;
                    }
                    Apply(rows, player.transform.position, 0, false, "local_observation");
                }
            }

            bool portalStatusAllowed = standalone || (_effectiveCategories & SharedMapPersonalMarkers.Category("PortalMarkerSync")) != 0;
            _ledger.PortalStatus.Tick(now, portalStatusAllowed, _aliases, _groupAnchors, _exclusions, _tracePortalStatus);

            if (now < _next)
                return;
            _next = now + (rpc != null && _link.State == LinkState.ClientOnly && now - _lastHost > 15 ? 10 : 2);
            _ledger.Save(false);
            if (player.IsDead() || Character.InInterior(player.transform.position))
                return;
            if (_request == int.MaxValue)
                _request = _received = 0;
            _request++;
            if (rpc?.IsConnected() == true)
                rpc.Invoke(SharedMapPersonalMarkers.Query, _request);
            else if (SharedMapController.Active?.LocalMarkers(profile, _request, player.transform.position) is byte[] data)
                Accept(data, "local_host");
        }
        catch (Exception error)
        {
            Failure(error);
        }
    }

    private static void EnteredDungeon(Teleport __instance, Humanoid character, bool __result)
    {
        var self = _current;
        if (!__result || self == null || self._failed || !ReferenceEquals(character, self._player)) return;
        try { (self._local ??= new()).Entered(__instance); }
        catch (Exception error) { self.Failure(error); }
    }

    internal static bool? DisplayPortalStatus(Minimap.PinData pin) =>
        _current is { _failed: false } client ? client._ledger?.PortalStatus.Display(pin) : null;

    private void ReceivePolicy(ZRpc rpc, ZPackage packet)
    {
        if (!ReferenceEquals(_server, rpc) || !ReferenceEquals(ZNet.instance?.GetServerPeer()?.m_rpc, rpc) ||
            packet.Size() > SharedMapPersonalMarkers.PolicyBytes) return;
        AcceptPolicy(packet.GetArray());
    }

    private void AcceptPolicy(byte[] data)
    {
        if (data.Length < 20 || data.Length > SharedMapPersonalMarkers.PolicyBytes || BitConverter.ToInt64(data, 0) != _world) return;
        int categories = BitConverter.ToInt32(data, 12);
        if ((categories & ~1023) != 0) return;
        int count = BitConverter.ToInt32(data, 16);
        if (count < 0 || count > (data.Length - 20) / 32) return;
        int pairs = 20 + count * 32;
        if ((data.Length - pairs) % 64 != 0) return;
        string Hash(int offset) => BitConverter.ToString(data, offset, 32).Replace("-", "").ToLowerInvariant();
        _exclusions.Clear();
        _groupAnchors.Clear();
        _aliases.Clear();
        for (int offset = 20; offset < pairs; offset += 32) _exclusions.Add(Hash(offset));
        for (int offset = pairs; offset < data.Length; offset += 64) _aliases[Hash(offset)] = Hash(offset + 32);
        _groupAnchors.UnionWith(_aliases.Values);
        _ledger?.ReconcileAliases(_aliases);
        _policyToken = BitConverter.ToInt32(data, 8);
        _categories = categories;
        RefreshCategories(policyChanged: true);
    }

    private void RefreshCategories(bool policyChanged = false)
    {
        int allowed = _categories;
        if (_link.Snapshot is { } host)
            foreach (var setting in host.Numbers)
                if (setting.Module == "SharedMap" && setting.Key == "PortalClusterDistanceMeters")
                    _portalDistance = Mathf.Clamp((float)setting.Value, 0, 100);
        if (_link.Snapshot is { } snapshot)
            foreach (var setting in snapshot.Switches)
            {
                if (setting.Definition.Module != "SharedMap" || setting.Value) continue;
                if (setting.Definition.Key == "Enabled") allowed = 0;
                else allowed &= ~SharedMapPersonalMarkers.Category(setting.Definition.Key);
            }
        if (!policyChanged && allowed == _effectiveCategories) return;
        _effectiveCategories = allowed;
        _ledger?.SetLocalPolicy(LocalAllowed);
    }

    private bool LocalAllowed(SharedMapPersonalMarker row)
    {
        int category = SharedMapPersonalMarkers.Category(row.Pin);
        return (_effectiveCategories & category) != 0 && !_exclusions.Contains(row.Id) &&
            !(_aliases.TryGetValue(row.Id, out var group) && _exclusions.Contains(group)) &&
            (_exclusions.Count == 0 || !_exclusions.Contains(SharedMapPinRemovals.Identity(row.Pin)));
    }

    private void Receive(ZRpc rpc, ZPackage package)
    {
        if (_failed || _ledger == null || !ReferenceEquals(rpc, _server) || !ReferenceEquals(ZNet.instance?.GetServerPeer()?.m_rpc, rpc) || package.Size() > SharedMapPersonalMarkers.Bytes || !ReferenceEquals(Player.m_localPlayer, _player))
            return;
        try
        {
            Accept(package.GetArray(), "remote_host");
        }
        catch (Exception error)
        {
            Trace("rejected", $"reason={error.Message}");
        }
    }

    private void Accept(byte[] data, string source)
    {
        if (data.Length < 24 || BitConverter.ToInt64(data, 4) != _world || BitConverter.ToInt64(data, 12) != _profile)
            return;
        int request = BitConverter.ToInt32(data, 20);
        if (request <= _received || request > _request)
            return;
        var rows = SharedMapPersonalMarkers.Decode(data, _world, _profile, request, out var centre, out var radius);
        _lastHost = Time.unscaledTime;
        Apply(rows, centre, radius, true, source);
        _received = request;
        Trace("delivery", $"source={source} request={request} received={rows.Count}");
    }

    private void Apply(List<SharedMapPersonalMarker> rows, Vector3 centre, float radius, bool complete, string source)
    {
        var result = _ledger!.Apply(rows, centre, radius, complete, _settings.TraceSharedMap.Value
            ? row => Trace("marker_added", $"source={source} id={row.Id} label={row.Pin.Name} x={row.Pin.X:F1} z={row.Pin.Z:F1}")
            : null);
        if (result.Added + result.Updated + result.Retired > 0)
            Trace("apply", $"source={source} added={result.Added} updated={result.Updated} retired={result.Retired} sample_id={(rows.Count > 0 ? rows[0].Id : "none")}");
    }

    private static void BeginTable(MapTable __instance, out ZDO? __state)
    {
        __state = _readTable;
        _readTable = __instance.GetComponent<ZNetView>()?.GetZDO();
    }

    private static Exception? EndTable(ZDO? __state, Exception? __exception)
    {
        _readTable = __state;
        return __exception;
    }

    private static void BeforeRead(Minimap __instance, ref byte[] dataArray, out ImportState? __state)
    {
        __state = null;
        var client = _current;
        if (client?._ledger == null || client._failed || !ReferenceEquals(client._map, __instance) || dataArray.Length < 4 || BitConverter.ToInt32(dataArray, 0) < 2)
            return;
        try
        {
            var raw = dataArray;
            var rows = SharedMapTableData.SeparateManagedPins(__instance.m_textureSize, ref raw);
            var state = new ImportState
            {
                Ledger = client._ledger,
                Rows = rows,
                Keys = SharedMapTableKeys.Read(_readTable, rows.Count)
            };
            state.Held = state.Ledger.Detach();
            __state = state;
            dataArray = raw;
        }
        catch (Exception error)
        {
            client.Trace("table_skipped", $"reason={error.Message}");
        }
    }

    private static void AfterRead(ImportState? __state, ref bool __result)
    {
        if (__state?.Held == null)
            return;
        __state.Ledger.Reattach(__state.Held);
        __state.Held = null;
        try
        {
            var counts = __state.Ledger.Import(__state.Rows, __state.Keys);
            __result |= counts.Changed != 0;
            _current?.Trace("table_read", $"managed_records={__state.Rows.Count} table={_readTable?.m_uid} changed={counts.Changed} reused={counts.Reused} source_keys={__state.Keys != null}");
        }
        catch (Exception error)
        {
            _current?.Failure(error);
        }
    }

    private static Exception? FinishRead(ImportState? __state, Exception? __exception)
    {
        if (__state?.Held != null)
            __state.Ledger.Reattach(__state.Held);
        return __exception;
    }

    private void Trace(string name, string evidence)
    {
        if (_settings.TraceSharedMap.Value && _budget.Reserve(Time.unscaledTime, name, 24))
            _writer.Write($"{DateTime.Now:HH:mm:ss.fff} SharedMap: {name} world={_world} profile={_profile} {evidence}");
    }

    private void Failure(Exception error)
    {
        _failed = true;
        DiagnosticLog.Write(_log, "SharedMap", CoreLogTopics.Lifecycle, "Personal markers paused for this client session.", level: LogLevel.Warning, error: error);
    }

    private void Close()
    {
        if (ReferenceEquals(_current, this))
            _current = null;
        _portalDistance = 60;
        _hooks.UnpatchSelf();
        _server?.Unregister(SharedMapPersonalMarkers.PolicyReply);
        _policyToken = _categories = _effectiveCategories = 0;
        _groupAnchors.Clear();
        _aliases.Clear();
        _exclusions.Clear();
        _nextPolicy = 0;
        _server?.Unregister(SharedMapPersonalMarkers.Reply);
        _server = null;
        _local?.Dispose();
        _local = null;
        try
        {
            _ledger?.Dispose();
        }
        catch (Exception e)
        {
            Failure(e);
        }

        _ledger = null;
        _map = null;
        _player = null;
        _world = _profile = 0;
        _received = _request;
        _next = 0;
        _failed = false;
        _budget.Reset();
    }

    public void Dispose()
    {
        Close();
        _tint.UnpatchSelf();
        _writer.Dispose();
    }
}
