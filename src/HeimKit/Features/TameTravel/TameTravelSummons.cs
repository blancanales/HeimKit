using System;
using System.Collections;
using System.Collections.Generic;
using HeimKit.Core.Native;
using HeimKit.Core.Prefabs;
using HeimKit.Features.TameTravel.Models;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelSummons
{
    internal static readonly int Skeleton = "Skeleton_Friendly".GetStableHashCode();
    private static readonly int FloorLayers = LayerMask.GetMask("terrain", "Default", "static_solid", "Default_small", "piece");
    internal static readonly int Summoner = "heimkit_deadraiser_summoner".GetStableHashCode();
    private readonly TameTravelSettings _settings;
    private readonly List<(ZDO Record, ZDOID Id, ushort OwnerRevision)> _followers = new();
    private readonly List<Vector3> _landings = new();
    private Player? _caster;
    private Player? _traveller;
    private ZDOID _character;
    private Vector3 _from, _exit;
    private Quaternion _rotation;
    private Func<bool>? _pairValid;
    private float _deadline;

    internal TameTravelSummons(TameTravelSettings settings) => _settings = settings;
    internal bool Pending => _followers.Count != 0;

    // Unity advances this iterator across several frames. Attribute commands only inside a
    // MoveNext call of this exact staff ability, never during a yield or a later native toggle.
    internal IEnumerator ObserveCast(IEnumerator spawn, Player caster)
    {
        try
        {
            while (true)
            {
                var previous = _caster;
                _caster = caster;
                bool advanced;
                try { advanced = spawn.MoveNext(); }
                finally { _caster = previous; }
                if (!advanced) yield break;
                yield return spawn.Current;
            }
        }
        finally { (spawn as IDisposable)?.Dispose(); }
    }

    internal void SpawnCommand(Player player, ZDO? record)
    {
        if (!_settings.SummonTravel.Value || player != _caster || player.GetPlayerID() == 0 || record == null || !record.IsValid() ||
            !record.IsOwner() || record.GetPrefab() != Skeleton || record.GetLong(Summoner) != 0) return;
        record.Set(Summoner, player.GetPlayerID());
    }

    internal void Begin(Player player, Vector3 from, TameTravelSummonGate gate)
    {
        Cancel();
        if (!_settings.SummonTravel.Value || player.IsDead() || !player.IsTeleporting() || player.GetPlayerID() == 0 || !gate.Valid()) return;
        var nearby = TameTravelRules.Nearby(from, static (record, _) => record.GetPrefab() == Skeleton);
        if (nearby == null) return;
        _traveller = player;
        _character = player.GetZDOID();
        _from = gate.Source;
        _exit = gate.Exit;
        _rotation = gate.Rotation;
        _pairValid = gate.Valid;
        _deadline = Time.unscaledTime + _settings.TravelWindowSeconds.Value;
        foreach (var record in nearby)
        {
            if (!Eligible(record, player) || !WithinRange(record, from)) continue;
            var view = ZNetScene.instance.FindInstance(record);
            if (view == null || view.GetComponent<MonsterAI>()?.GetFollowTarget() != player.gameObject ||
                view.GetComponent<Character>() is not { } skeleton || skeleton.IsDead()) continue;
            _followers.Add((record, record.m_uid, record.OwnerRevision));
            if (_followers.Count == _settings.MaxFollowersPerTraversal.Value) break;
        }
        if (!Pending) Cancel();
    }

    private bool Eligible(ZDO record, Player player) => record.IsValid() && record.Persistent &&
        record.GetPrefab() == Skeleton && record.IsOwner() && NativeCharacterHealth.IsAlive(record) &&
        record.GetLong(Summoner) == player.GetPlayerID() && record.GetString(ZDOVars.s_follow) == player.GetPlayerName() &&
        WithinRange(record, _from);

    private bool WithinRange(ZDO record, Vector3 point) =>
        (record.GetPosition() - point).sqrMagnitude <= _settings.SummonRangeMeters.Value * _settings.SummonRangeMeters.Value;

    internal void Forget(ZDO? record)
    {
        if (record == null) return;
        for (int i = _followers.Count - 1; i >= 0; i--)
            if (ReferenceEquals(record, _followers[i].Record)) _followers.RemoveAt(i);
        if (!Pending) Cancel();
    }

    internal void Cancel()
    {
        _followers.Clear();
        _landings.Clear();
        _traveller = null;
        _pairValid = null;
    }

    internal void Expire()
    {
        if (_traveller != null && Pending && (!SamePlayer(Player.m_localPlayer) || !_traveller.IsTeleporting())) Cancel();
        else if (Pending && _traveller == null) Cancel();
    }

    private bool SamePlayer(Player player) => player != null && player == _traveller &&
        player.GetZDOID() == _character && !player.IsDead() && Time.unscaledTime <= _deadline;

    internal bool DeferDistanceCheck(Tameable tame)
    {
        if (!Pending || !SamePlayer(Player.m_localPlayer) || !Player.m_localPlayer.IsTeleporting()) return false;
        var record = tame.GetComponent<ZNetView>()?.GetZDO();
        if (record == null || record.GetPrefab() != Skeleton) return false;
        foreach (var follower in _followers)
            if (ReferenceEquals(record, follower.Record) && record.m_uid == follower.Id &&
                record.OwnerRevision == follower.OwnerRevision && Eligible(record, Player.m_localPlayer))
                return true;
        return false;
    }

    internal int Finish(Player player, Action<ZDOID, ZDOID, Vector3> relocated)
    {
        try
        {
            if (!Pending || !SamePlayer(player) || player.IsTeleporting() || _pairValid?.Invoke() != true ||
                (player.transform.position - _exit).sqrMagnitude > 1 || !ZNetScene.instance.IsAreaReady(_exit) ||
                !ZoneSystem.instance.FindFloor(_exit, out _)) return 0;
            foreach (var follower in _followers)
            {
                var record = follower.Record;
                if (!ReferenceEquals(ZDOMan.instance.GetZDO(follower.Id), record) || record.m_uid != follower.Id ||
                    record.OwnerRevision != follower.OwnerRevision || !Eligible(record, player)) continue;
                var instance = ZNetScene.instance.FindInstance(record);
                if (instance != null && (instance.GetComponent<MonsterAI>()?.GetFollowTarget() != player.gameObject ||
                    instance.GetComponent<Character>() is not { } skeleton || skeleton.IsDead())) continue;
                if (!Landing(record, player.transform.position, _rotation, _landings, out var position)) continue;
                TameTravelPlacement.Move(record, position, _rotation, _character, waiting: false);
                ZDOMan.instance.ForceSendZDO(follower.Id);
                _landings.Add(position);
                relocated(_character, follower.Id, position);
            }
            return _landings.Count;
        }
        finally { Cancel(); }
    }

    internal static bool Landing(ZDO record, Vector3 origin, Quaternion rotation, List<Vector3> landings, out Vector3 position)
    {
        position = default;
        var prefab = PrefabRegistry.Current.Prefab(Skeleton);
        var capsule = PrefabRegistry.Component<CapsuleCollider>(prefab);
        if (capsule == null || capsule.direction != 1 || !TameTravelRules.Scale(record, out var scale)) return false;
        float radius = capsule.radius * scale.x;
        float height = Mathf.Max(2 * radius, capsule.height * scale.y);
        for (int i = 0; i < 16; i++)
        {
            var offset = Quaternion.Euler(0, (i % 8) * 45f, 0) * Vector3.forward * (i < 8 ? 1.6f : 2.6f);
            var point = origin + offset;
            if (!Physics.Raycast(point + Vector3.up, Vector3.down, out var floor, 2, FloorLayers, QueryTriggerInteraction.Ignore) ||
                floor.normal.y < .8f || Mathf.Abs(floor.point.y - origin.y) > .65f ||
                floor.rigidbody != null || floor.point.y <= ZoneSystem.instance.m_waterLevel) continue;
            var bottom = floor.point + Vector3.up * (radius + .12f);
            var top = bottom + Vector3.up * (height - 2 * radius);
            if (Physics.CheckCapsule(bottom, top, radius, Physics.AllLayers, QueryTriggerInteraction.Ignore) ||
                Physics.CapsuleCast(bottom - offset, top - offset, radius, offset.normalized, offset.magnitude,
                    FloorLayers, QueryTriggerInteraction.Ignore)) continue;
            position = (bottom + top) * .5f - rotation * Vector3.Scale(capsule.center, scale);
            bool occupied = false;
            foreach (var other in landings) if ((other - position).sqrMagnitude < 4 * radius * radius + .25f) occupied = true;
            if (!occupied) return true;
        }
        return false;
    }
}
