using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Players;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HarmonyLib;
using UnityEngine;

namespace HeimKit.Features.HeimDefender;
internal sealed class HeimDefenderController : IDisposable
{
    private sealed class Lease : OwnershipLease
    {
        public override ZNetPeer DestinationPeer => PreviousPeer!;

        internal float Started;
        internal bool Applied;
        internal readonly List<float> Refresh = new();
    }

    private static readonly HashSet<int> NativeInteractions = new()
    {
        "RPC_AddAmmo".GetStableHashCode(),
        "RPC_Damage".GetStableHashCode(),
        "RPC_Remove".GetStableHashCode(),
        "RPC_Repair".GetStableHashCode(),
        "RPC_HealthChanged".GetStableHashCode(),
        "RPC_ClearCachedSupport".GetStableHashCode(),
        "RPC_SetSnow".GetStableHashCode(),
        "RPC_CreateFragments".GetStableHashCode()
    };
    private static readonly int Target = "RPC_SetTarget".GetStableHashCode(), Pong = "RPC_Pong".GetStableHashCode();
    private static HeimDefenderController? _active;
    private readonly PlayerServices _players;
    private readonly HeimDefenderSettings _settings;
    private readonly ManualLogSource _log;
    private readonly Turret _prefab;
    private readonly DiagnosticBudget _budget = new();
    private readonly ObjectTransferAuthority _authority;
    private readonly CandidateSubscription _index;
    private readonly IDisposable _messages;
    private readonly Harmony _hooks = new("heimkit.heimdefender.target");
    private readonly Dictionary<long, (Vector3 Point, int Radius, bool Classic)> _owners = new();
    private IEnumerator<bool>? _scan;
    private sealed class FullMagazineCheck
    {
        internal double Next;
    }

    // A full magazine needs no ZDO write or ownership transfer just to schedule its next check.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ZDO, FullMagazineCheck> _fullMagazines = new();
    private Lease? _lease;
    private int _examined, _applied, _restored;
    internal HeimDefenderController(PlayerServices players, HeimDefenderSettings settings, ManualLogSource log, Action<Exception> fail)
    {
        _players = players;
        _settings = settings;
        _log = log;
        var prefab = PrefabRegistry.Current.Prefab(HeimDefenderBallistaPolicy.Prefab);
        var turret = PrefabRegistry.Component<Turret>(prefab);
        if (turret == null || !turret.m_allowedAmmo.Exists(entry => entry.m_ammo != null && entry.m_ammo.name == HeimDefenderBallistaPolicy.Wood))
            throw new InvalidOperationException("The player-built Ballista or its wood ammunition definition is unavailable.");
        _prefab = turret;
        _authority = new(players.Presence, fail, "HeimDefender", staticObjects: true);
        try
        {
            _index = WorldObjectProvider.Current.Subscribe("HeimDefender", z => z.GetPrefab() == HeimDefenderBallistaPolicy.Prefab, ScanKind.Nearby);
            _messages = players.RoutedMessages.Subscribe(Observe, fail);
            _hooks.Patch(NativeAccess.Target(typeof(Turret), "RPC_SetTarget", typeof(long), typeof(ZDOID)), postfix: new HarmonyMethod(typeof(HeimDefenderController), nameof(LocalTarget)));
            _hooks.Patch(NativeAccess.Target(typeof(Turret), "FixedUpdate"), prefix: new HarmonyMethod(typeof(HeimDefenderController), nameof(CanSimulate)));
            _active = this;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Tick(float now)
    {
        _authority.Tick(now);
        if (_lease != null)
        {
            Advance(now);
            return;
        }

        if (_scan == null)
        {
            if (!ModuleScheduler.Current.Due(this, "ballistas", now, 2, cooperative: true))
                return;
            WorldActors.ReadSimulationOwners(_players.Presence, _owners);
            _scan = Scan().GetEnumerator();
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _examined = 0;
        // Stop discovery before advancing another candidate after starting a transaction.
        if (WorkSlice.Advance(_scan, 1, 16))
        {
            _scan.Dispose();
            _scan = null;
            if (_applied + _restored > 0) DiagnosticLog.Write(_log,"HeimDefender",$"Updated {_applied} Ballistas and restored {_restored} to native settings.");
            _applied = _restored = 0;
        }
        PerformanceStages.Record("HeimDefender","reconcile",ServerPerformance.Milliseconds(System.Diagnostics.Stopwatch.GetTimestamp()-started),_examined);
    }

    private IEnumerable<bool> Scan()
    {
        var ids = new List<ZDOID>();
        foreach (var scope in _owners.Values)
            foreach (bool unit in WorldObjectProvider.Current.CollectIds(_index, scope.Point, scope.Radius, scope.Classic, ids))
            { _examined++; yield return unit; }
        var seen = new HashSet<ZDOID>();
        foreach (var id in ids)
        {
            _examined++;
            var z = ZDOMan.instance.GetZDO(id);
            if (seen.Add(id) && z != null && HeimDefenderBallistaPolicy.Eligible(z) && InRange(z) && NeedsChange(z))
            {
                var lease = new Lease
                {
                    Id = id,
                    Record = z,
                    StaticControl = true,
                    Started = Time.unscaledTime,
                    PreemptOnInteraction = NativeInteractions.Contains
                };
                if (_authority.Acquire(lease, lease.Started, out string reason, allowInitialStaticRevision: true))
                    _lease = lease;
                else
                    Trace("refresh_deferred", $"ballista={id} reason={reason}");
            }

            do
            {
                yield return true;
            }
            while (_lease != null);
        }
    }

    private bool NeedsChange(ZDO record)
    {
        if (HeimDefenderBallistaPolicy.FieldChangesNeeded(record, _settings))
            return true;
        double now = ZNet.instance.GetTime().Ticks / (double)TimeSpan.TicksPerSecond;
        if (_fullMagazines.TryGetValue(record, out var check) && now < check.Next)
            return false;
        string reason = HeimDefenderBallistaPolicy.RefillStatus(record, _settings, _prefab, out _, out _, out double interval);
        if (reason == "at_target")
            _fullMagazines.GetValue(record, _ => new FullMagazineCheck()).Next = now + interval;
        if (_settings.FreeWoodBolts.Value && reason != "due" && _settings.Debug.Enabled)
            Trace("refill_skipped", $"ballista={record.m_uid} reason={reason} stock={record.GetInt(ZDOVars.s_ammo)}");
        return reason == "due";
    }

    private bool InRange(ZDO record)
    {
        WorldActors.ReadSimulationOwners(_players.Presence, _owners);
        foreach (var scope in _owners.Values)
            if (WorldObjectProvider.Current.WithinNear(scope.Point, scope.Radius, scope.Classic, record.GetPosition())) return true;
        return false;
    }

    private void Advance(float now)
    {
        var lease = _lease!;
        if (!_authority.Holds(lease) || !lease.Record.IsValid() || lease.Record.GetOwner() != ZDOMan.GetSessionID() || !HeimDefenderBallistaPolicy.Eligible(lease.Record) || now - lease.Started > 8)
        {
            Trace("refresh_interrupted", $"ballista={lease.Id} applied={lease.Applied} source_received={lease.SourceStateObserved} previous_owner={lease.PreviousOwner} revision={lease.Record.DataRevision}");
            Release();
            return;
        }

        if (lease.Applied)
        {
            foreach (float barrier in lease.Refresh)
                if (!_authority.Echoed(barrier))
                    return;
            Trace("refresh_ordered", $"ballista={lease.Id} peers={lease.Refresh.Count}");
            Release();
            return;
        }

        if (lease.PreviousPeer != null && (!lease.SourceStateObserved || !_authority.Echoed(lease.Barrier)))
            return;
        lease.Phase = 1;
        if (!InRange(lease.Record) || !NeedsChange(lease.Record))
        {
            Release();
            return;
        }

        var record = lease.Record;
        bool fieldsChanged = HeimDefenderBallistaPolicy.FieldChangesNeeded(record, _settings);
        if (fieldsChanged)
            HeimDefenderBallistaPolicy.ApplyFields(record, _settings);

        int previousStock = record.GetInt(ZDOVars.s_ammo);
        string refill = HeimDefenderBallistaPolicy.RefillStatus(record, _settings, _prefab, out int capacity, out int batch, out double interval);
        if (refill == "due")
        {
            string ammo = _settings.SelectedAmmo;
            HeimDefenderBallistaPolicy.Fill(record, ammo, batch, interval);
            Trace("magazine_refilled", $"ballista={lease.Id} previous_owner={lease.PreviousOwner} previous_stock={previousStock} added={batch - previousStock} stock={record.GetInt(ZDOVars.s_ammo)} ammo_type={ammo} capacity={capacity} interval_seconds={interval}");
        }
        // Stock is ordinary replicated ZDO data. Only native field overrides require
        // re-creating a guest's instance and clearing the previous target.
        if (!fieldsChanged)
        {
            ZDOMan.instance.ForceSendZDO(lease.Id);
            Release();
            return;
        }
        lease.Applied = true;
        if (_settings.Enabled.Value) _applied++; else _restored++;
        var view = ZNetScene.instance.FindInstance(lease.Record);
        if (view != null && view.IsValid() && view.IsOwner())
            view.InvokeRPC(ZNetView.Everybody, "RPC_SetTarget", ZDOID.None);
        foreach (var peer in _players.Presence.Peers)
        {
            if (!_authority.CanSend(peer))
                continue;
            var distance = peer.m_simulationDistance;
            if (peer != lease.PreviousPeer && !WorldObjectProvider.Current.WithinNear(peer.m_refPos, distance.NearSimulationDistance + 1, distance.IsClassic, lease.Record.GetPosition()))
                continue;
            lease.Refresh.Add(_authority.ResetClientInstance(peer, lease, now));
        }

        ZDOMan.instance.ForceSendZDO(lease.Id);
        Trace("policy_applied", $"ballista={lease.Id} previous_owner={lease.PreviousOwner} revision={lease.Record.DataRevision} targets_filtered={_settings.Enabled.Value} refill={refill} stock={lease.Record.GetInt(ZDOVars.s_ammo)} refresh_peers={lease.Refresh.Count}");
    }

    private void Release()
    {
        var lease = _lease;
        _lease = null;
        if (lease == null)
            return;
        foreach (float barrier in lease.Refresh)
            _authority.ForgetBarrier(barrier);
        _authority.ForgetBarrier(lease.Barrier);
        _authority.Release(lease.Id);
    }

    private void Observe(ZRpc rpc, ZPackage packet)
    {
        var peer = PlayerPeerLookup.Ready(_players.Presence.Peers, rpc);
        if (peer == null || !RoutedActivityReader.TryRead(packet, peer.m_uid, out var id, out int method))
            return;
        if (method != Pong && (method != Target || !_settings.Debug.ConfiguredEnabled))
            return;
        if (!RoutedActivityReader.TryReadParameters(packet, peer.m_uid, out _, out _, out var args) || args == null)
            return;
        if (method == Pong && id.IsNone() && args.Size() == sizeof(float))
            _authority.ObservePong(peer, args.ReadSingle());
        if (method == Target && args.Size() == 12 && ZDOMan.instance.GetZDO(id) is ZDO z && HeimDefenderBallistaPolicy.Eligible(z) && z.GetOwner() == peer.m_uid)
            TargetTrace(z, args.ReadZDOID(), "remote_owner");
    }

    private static bool CanSimulate(Turret __instance)
    {
        var active = _active;
        return active?._lease == null || __instance.GetComponent<ZNetView>()?.GetZDO() != active._lease.Record || !active._authority.Holds(active._lease);
    }

    private static void LocalTarget(Turret __instance, ZDOID character)
    {
        var self = _active;
        if (self == null || !self._settings.Debug.ConfiguredEnabled)
            return;
        var view = __instance.GetComponent<ZNetView>();
        if (view != null && view.IsValid() && view.IsOwner() && HeimDefenderBallistaPolicy.Eligible(view.GetZDO()))
            self.TargetTrace(view.GetZDO(), character, "host_owner");
    }

    private void TargetTrace(ZDO ballista, ZDOID target, string source)
    {
        if (!_budget.Reserve(Time.unscaledTime, "target", 24))
            return;
        DiagnosticLog.WriteObservation(_log, "HeimDefender", _settings.Debug, new LogTopic("target"), HeimDefenderDiagnostics.Target(ballista, target, source));
    }

    private void Trace(string eventName, string fields)
    {
        if (_settings.Debug.Enabled && _budget.Reserve(Time.unscaledTime, eventName, 12))
            DiagnosticLog.Write(_log, "HeimDefender", $"{eventName} world={ZNet.instance.GetWorldUID()} {fields}", level: LogLevel.Debug);
    }

    public void Dispose()
    {
        if (ReferenceEquals(_active, this))
            _active = null;
        _hooks.UnpatchSelf();
        _messages?.Dispose();
        _scan?.Dispose();
        _index?.Dispose();
        Release();
        _authority.Dispose();
        ModuleScheduler.Current.Forget(this);
    }
}
