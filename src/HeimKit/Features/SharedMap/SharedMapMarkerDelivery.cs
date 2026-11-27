using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;
using UnityEngine;

namespace HeimKit.Features.SharedMap;
internal sealed class SharedMapMarkerDelivery : IDisposable
{
    private sealed class Recipient
    {
        internal float LastPolicy = float.NegativeInfinity;
        internal int Request;
        internal float Last = float.NegativeInfinity;
    }

    private readonly SharedMapController _host;
    private readonly IPlayerPresence _presence;
    private readonly ManualLogSource _log;
    private readonly Func<bool> _debug;
    private readonly DiagnosticBudget _budget = new();
    private readonly Dictionary<ZRpc, Recipient> _peers = new();
    private IEnumerator<bool>? _work;
    private Dictionary<(int, int), List<SharedMapPersonalMarker>>? _cells;
    private bool _requested;
    private float _expires;
    private bool _failed;
    private static int _nextPolicyToken;
    private int _policyToken;
    private bool _policyRequested;
    private byte[]? _policy;
    private (long, long, int, int, object, object?, object) _policyVersion;
    private IEnumerator<bool>? _policyWork;
    private int _generation;
    private SharedMapReconciliationState? _inputs;
    internal SharedMapMarkerDelivery(SharedMapController host, IPlayerPresence presence, ManualLogSource log, Func<bool> debug)
    {
        _host = host;
        _presence = presence;
        _log = log;
        _debug = debug;
    }

    internal void Tick(float now)
    {
        if (_failed)
            return;
        try
        {
            Process(now);
        }
        catch (Exception error)
        {
            _failed = true;
            Dispose();
            DiagnosticLog.Write(_log, "SharedMap", CoreLogTopics.Lifecycle, "Personal marker delivery stopped for this host session.", level: LogLevel.Error, error: error);
        }
    }

    private void Process(float now)
    {
        if (ModuleScheduler.Current.Due(this, "connections", now, 1))
        {
            foreach (var rpc in _peers.Keys.Where(r => PlayerPeerLookup.Ready(_presence.Peers, r) == null).ToArray())
            {
                rpc.Unregister(SharedMapPersonalMarkers.Query);
                rpc.Unregister(SharedMapPersonalMarkers.PolicyQuery);
                _peers.Remove(rpc);
            }

            foreach (var p in _presence.Peers)
                if (p.IsReady() && !p.m_characterID.IsNone() && !_peers.ContainsKey(p.m_rpc))
                {
                    _peers.Add(p.m_rpc, new Recipient());
                    p.m_rpc.Register<int>(SharedMapPersonalMarkers.Query, OnRequest);
                    p.m_rpc.Register<int>(SharedMapPersonalMarkers.PolicyQuery, OnPolicyRequest);
                }
        }

        if (_policyRequested && !Game.m_noMap)
        {
            var version = _host.LocalPolicyVersion;
            if (_policyWork == null || version != _policyVersion)
            {
                _policyWork?.Dispose();
                _policyVersion = version;
                _policyWork = BuildPolicy().GetEnumerator();
            }
            if (WorkSlice.Advance(_policyWork, 1, 16))
            {
                _policyWork.Dispose(); _policyWork = null; _policyRequested = false;
            }
        }
        if (!_requested || Game.m_noMap)
            return;
        if (_cells == null || now >= _expires)
        {
            _work ??= Build().GetEnumerator();
            if (!WorkSlice.Advance(_work, 1, 16))
                return;
            _work.Dispose();
            _work = null;
            _expires = now + 2;
        }

        _requested = false;
        foreach (var pair in _peers)
        {
            int request = pair.Value.Request;
            if (request <= 0)
                continue;
            var peer = PlayerPeerLookup.Ready(_presence.Peers, pair.Key);
            pair.Value.Request = 0;
            if (peer == null || !SharedMapController.TryCharacter(peer, out var centre))
                continue;
            long profile = ZDOMan.instance.GetZDO(peer.m_characterID)?.GetLong(ZDOVars.s_playerID) ?? 0;
            if (profile == 0)
                continue;
            byte[]? bytes = Snapshot(profile, request, centre);
            if (bytes != null)
                pair.Key.Invoke(SharedMapPersonalMarkers.Reply, new ZPackage(bytes));
        }
    }

    private IEnumerable<bool> BuildPolicy()
    {
        var hashes = new HashSet<string>();
        foreach (var id in _host.LocalExclusions())
        {
            hashes.Add(id);
            yield return true;
        }
        // Exclusions contain hashes only, never positions of undiscovered sites.
        var aliases = new List<(string Member, string Group)>();
        foreach (var alias in _host.LocalAliases())
        {
            aliases.Add(alias);
            yield return true;
        }
        var data = new byte[20 + checked(hashes.Count * 32 + aliases.Count * 64)];
        if (data.Length > SharedMapPersonalMarkers.PolicyBytes) throw new InvalidOperationException("Personal marker exclusions exceed the supported host limits.");
        BitConverter.GetBytes(ZNet.instance.GetWorldUID()).CopyTo(data, 0);
        BitConverter.GetBytes(++_nextPolicyToken).CopyTo(data, 8);
        BitConverter.GetBytes(_host.LocalCategories).CopyTo(data, 12);
        BitConverter.GetBytes(hashes.Count).CopyTo(data, 16);
        int offset = 20;
        foreach (var id in hashes)
        {
            SharedMapTableKeys.Encode(id, data, offset);
            offset += 32;
            yield return true;
        }
        foreach (var alias in aliases)
        {
            SharedMapTableKeys.Encode(alias.Member, data, offset);
            SharedMapTableKeys.Encode(alias.Group, data, offset + 32);
            offset += 64;
            yield return true;
        }
        _policy = data;
        _policyToken = BitConverter.ToInt32(data, 8);
    }

    private void OnPolicyRequest(ZRpc rpc, int token)
    {
        if (!_peers.TryGetValue(rpc, out var recipient) || Time.unscaledTime - recipient.LastPolicy < 2 ||
            Game.m_noMap || PlayerPeerLookup.Ready(_presence.Peers, rpc) == null) return;
        recipient.LastPolicy = Time.unscaledTime;
        if (_policy == null || _host.LocalPolicyVersion != _policyVersion || _policyWork != null)
        {
            _policyRequested = true;
            return;
        }
        if (token != _policyToken) rpc.Invoke(SharedMapPersonalMarkers.PolicyReply, new ZPackage(_policy));
    }

    internal byte[]? Policy(int token)
    {
        if (_policy == null || _policyWork != null || _host.LocalPolicyVersion != _policyVersion)
        {
            _policyRequested = true;
            return null;
        }
        return token != _policyToken ? _policy : null;
    }

    private void OnRequest(ZRpc rpc, int request)
    {
        if (request <= 0 || !_peers.TryGetValue(rpc, out var recipient) || Time.unscaledTime - recipient.Last < 1)
            return;
        recipient.Last = Time.unscaledTime;
        recipient.Request = request;
        _requested = true;
    }

    private IEnumerable<bool> Build()
    {
        var inputs = _host.PersonalInputs();
        if (_inputs != null && _inputs.SamePublications(inputs))
            yield break;
        var next = new Dictionary<(int, int), List<SharedMapPersonalMarker>>();
        var rows = new List<SharedMapPersonalMarker>();
        foreach (bool unit in _host.BuildPersonalMarkers(inputs, rows))
            yield return unit;
        int i = 0;
        foreach (var row in rows)
        {
            if ((i++ & 15) == 0)
                yield return true;
            var key = Cell(row.Position);
            if (!next.TryGetValue(key, out var cell))
                next.Add(key, cell = new());
            cell.Add(row);
        }

        _cells = next;
        _inputs = inputs;
        _generation++;
    }

    private static (int X, int Z) Cell(Vector3 pos) => (Mathf.FloorToInt(pos.x / 64), Mathf.FloorToInt(pos.z / 64));
    internal byte[]? Snapshot(long profile, int request, Vector3 centre)
    {
        if (_failed)
            return null;
        _requested = true;
        if (_cells == null || Time.unscaledTime >= _expires || Character.InInterior(centre))
            return null;
        float radius = _host.PersonalRadius;
        if (!SharedMapDungeonPrefab.Finite(centre) || Math.Abs(centre.x) > 1000000 || Math.Abs(centre.z) > 1000000 || radius <= 0 || radius > 1024)
            return null;
        var low = Cell(centre - new Vector3(radius, 0, radius));
        var high = Cell(centre + new Vector3(radius, 0, radius));
        var result = new List<SharedMapPersonalMarker>();
        for (int x = low.X; x <= high.X; x++)
            for (int z = low.Z; z <= high.Z; z++)
                if (_cells.TryGetValue((x, z), out var rows))
                    foreach (var row in rows)
                        if (SharedMapPersonalMarkers.Nearby(centre, row.Position, radius))
                            result.Add(row);
        long world = ZNet.instance.GetWorldUID();
        if (_debug() && _budget.Reserve(Time.unscaledTime, "delivery", 12))
            DiagnosticLog.Write(_log, "SharedMap", $"Personal markers prepared world={world} profile={profile} request={request} generation={_generation} pins={result.Count}", level: LogLevel.Debug);
        return SharedMapPersonalMarkers.Encode(world, profile, request, centre, radius, result);
    }

    public void Dispose()
    {
        foreach (var rpc in _peers.Keys)
        {
            rpc.Unregister(SharedMapPersonalMarkers.Query);
            rpc.Unregister(SharedMapPersonalMarkers.PolicyQuery);
        }
        _policyWork?.Dispose();
        _peers.Clear();
        _work?.Dispose();
        ModuleScheduler.Current.Forget(this);
    }
}
