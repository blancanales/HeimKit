using System;
using System.Collections;
using HarmonyLib;
using HeimKit.Core.Native;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelLocalActions : IDisposable
{
    private readonly Harmony _hooks = new("heimkit.tametravel.local");
    private readonly TameTravelController _controller;
    private readonly Action<Exception> _fail;
    private readonly ZDOMan _world = ZDOMan.instance;
    internal TameTravelSummons Summons { get; }
    private PortalAttempt? _attempt;
    private static TameTravelLocalActions? _active;

    private readonly struct PortalAttempt
    {
        internal readonly TameTravelActor Host;
        internal readonly ZDO Source;
        internal readonly ZDOID Destination;
        internal readonly Vector3 From, Exit;
        internal readonly Quaternion Rotation;

        internal PortalAttempt(TameTravelActor host, ZDO source, ZDOID destination, Vector3 from, Vector3 exit, Quaternion rotation)
        {
            Host = host;
            Source = source;
            Destination = destination;
            From = from;
            Exit = exit;
            Rotation = rotation;
        }
    }

    internal TameTravelLocalActions(TameTravelController controller, TameTravelSettings settings, Action<Exception> fail)
    {
        _controller = controller;
        Summons = new(settings);
        _fail = fail;
        if (_active != null) throw new InvalidOperationException("Local tame travel hooks are already active.");
        try
        {
            _hooks.Patch(NativeAccess.Target(typeof(Chat), "SendText", typeof(Talker.Type), typeof(string)),
                postfix: Hook(nameof(ChatSent)));
            _hooks.Patch(NativeAccess.Target(typeof(Player), "StartEmote", typeof(string), typeof(bool)),
                postfix: Hook(nameof(EmoteStarted)));
            _hooks.Patch(NativeAccess.Target(typeof(Tameable), "Command", typeof(Humanoid), typeof(bool)),
                prefix: Hook(nameof(BeforeTameCommand)));
            _hooks.Patch(NativeAccess.Target(typeof(TeleportWorld), "Teleport", typeof(Player)),
                prefix: Hook(nameof(BeforePortal)), finalizer: Hook(nameof(AfterPortal)));
            _hooks.Patch(NativeAccess.Target(typeof(Player), "TeleportTo", typeof(Vector3), typeof(Quaternion), typeof(bool)),
                postfix: Hook(nameof(TeleportAccepted)));
            _hooks.Patch(NativeAccess.Target(typeof(Player), "UpdateTeleport", typeof(float)),
                prefix: Hook(nameof(BeforeTeleportUpdate)), postfix: Hook(nameof(AfterTeleportUpdate)));
            _hooks.Patch(NativeAccess.Target(typeof(SpawnAbility), "Spawn"), postfix: Hook(nameof(ObserveSpawn)));
            _hooks.Patch(NativeAccess.Target(typeof(ZNetScene), "Destroy", typeof(GameObject)), prefix: Hook(nameof(BeforeDestroy)));
            _hooks.Patch(NativeAccess.Target(typeof(Tameable), "RPC_Command", typeof(long), typeof(ZDOID), typeof(bool)),
                postfix: Hook(nameof(CommandApplied)));
            _hooks.Patch(NativeAccess.Target(typeof(Tameable), "UpdateSummon"), prefix: Hook(nameof(CheckSummonDistance)));
            _hooks.Patch(NativeAccess.Target(typeof(Teleport), "Interact", typeof(Humanoid), typeof(bool), typeof(bool)),
                postfix: Hook(nameof(DungeonAccepted)));
            _active = this;
        }
        catch { Dispose(); throw; }
    }

    private static HarmonyMethod Hook(string method) => new(typeof(TameTravelLocalActions), method);
    private static TameTravelLocalActions? Active =>
        _active != null && ReferenceEquals(_active._world, ZDOMan.instance) ? _active : null;

    private static void ObserveSpawn(SpawnAbility __instance, Character ___m_owner, ItemDrop.ItemData ___m_weapon,
        ref IEnumerator __result)
    {
        var adapter = Active;
        if (adapter == null || ___m_owner is not Player caster || caster != Player.m_localPlayer ||
            ___m_weapon?.m_dropPrefab == null || Utils.GetPrefabName(___m_weapon.m_dropPrefab) != "StaffSkeleton" ||
            !__instance.m_commandOnSpawn || Utils.GetPrefabName(__instance.gameObject) != "staff_skeleton_spawn") return;
        __result = adapter.Summons.ObserveCast(__result, caster);
    }

    private static void BeforeDestroy(GameObject go)
    {
        var adapter = Active;
        if (adapter?.Summons.Pending != true || go == null) return;
        try { adapter.Summons.Forget(go.GetComponent<ZNetView>()?.GetZDO()); }
        catch (Exception error) { adapter._fail(error); }
    }

    private static void CommandApplied(Tameable __instance)
    {
        var adapter = Active;
        if (adapter?.Summons.Pending != true) return;
        try
        {
            if (__instance.GetComponent<MonsterAI>()?.GetFollowTarget() != Player.m_localPlayer?.gameObject)
                adapter.Summons.Forget(__instance.GetComponent<ZNetView>()?.GetZDO());
        }
        catch (Exception error) { adapter._fail(error); }
    }

    private static bool CheckSummonDistance(Tameable __instance)
    {
        var adapter = Active;
        if (adapter == null) return true;
        try { return !adapter.Summons.DeferDistanceCheck(__instance); }
        catch (Exception error) { adapter._fail(error); return true; }
    }

    private static void DungeonAccepted(Teleport __instance, Humanoid character, bool __result)
    {
        var adapter = Active;
        if (!__result || adapter == null || !adapter._controller.AcceptSummons ||
            !TameTravelActor.TryLocal(character as Player, out var actor)) return;
        try
        {
            if (!adapter._controller.SummonerReady(actor)) return;
            var player = actor.Local!;
            var from = player.transform.position;
            var gate = TameTravelSummonGate.Dungeon(__instance, from, 8);
            if (gate == null) return;
            adapter.Summons.Begin(player, from, gate.Exit, gate.Rotation, gate.Valid);
            adapter._controller.GuestSummons?.LocalAccepted(actor, gate);
        }
        catch (Exception error) { adapter._fail(error); }
    }

    private static void ChatSent(string text)
    {
        var adapter = Active;
        if (adapter == null || !TameTravelActor.TryLocal(Player.m_localPlayer, out var host)) return;
        try { adapter._controller.LocalCommand(host, text, emote: false); }
        catch (Exception error) { adapter._fail(error); }
    }

    private static void EmoteStarted(Player __instance, string emote, bool __result)
    {
        var adapter = Active;
        if (!__result || adapter == null || !TameTravelActor.TryLocal(__instance, out var host)) return;
        try { adapter._controller.LocalCommand(host, emote, emote: true); }
        catch (Exception error) { adapter._fail(error); }
    }

    private static void BeforeTameCommand(Tameable __instance, Humanoid user, bool message)
    {
        var adapter = Active;
        if (adapter == null || !TameTravelActor.TryLocal(user as Player, out var host)) return;
        try
        {
            var record = __instance.GetComponent<ZNetView>()?.GetZDO();
            adapter.Summons.SpawnCommand(host.Local!, record);
            adapter._controller.LocalToggle(host, record, message);
        }
        catch (Exception error) { adapter._fail(error); }
    }

    private static void BeforePortal(TeleportWorld __instance, Player player, out PortalAttempt? __state)
    {
        var adapter = Active;
        __state = adapter?._attempt;
        if (adapter == null) return;
        adapter._attempt = null;
        if (!TameTravelActor.TryLocal(player, out var host)) return;
        try
        {
            var source = __instance.GetComponent<ZNetView>()?.GetZDO();
            if (source == null || !source.IsValid()) return;
            var destination = adapter._world.GetZDO(source.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal));
            if (destination == null || !destination.IsValid()) return;
            var rotation = destination.GetRotation();
            var exit = destination.GetPosition() + rotation * Vector3.forward * __instance.m_exitDistance + Vector3.up;
            adapter._attempt = new PortalAttempt(host, source, destination.m_uid, player.transform.position, exit, rotation);
        }
        catch (Exception error) { adapter._fail(error); }
    }

    private static void AfterPortal(PortalAttempt? __state)
    {
        if (Active is { } adapter) adapter._attempt = __state;
    }

    private static void TeleportAccepted(Player __instance, Vector3 pos, Quaternion rot, bool distantTeleport, bool __result)
    {
        var adapter = Active;
        if (__result && adapter != null && __instance == Player.m_localPlayer) adapter.Summons.Cancel();
        if (!__result || !distantTeleport || adapter?._attempt is not { } attempt ||
            !ReferenceEquals(__instance, attempt.Host.Local) || (pos - attempt.Exit).sqrMagnitude > .0001f ||
            Quaternion.Angle(rot, attempt.Rotation) > .01f) return;
        adapter._attempt = null;
        try
        {
            adapter._controller.LocalPortalAccepted(attempt.Host, attempt.Source, attempt.Destination, attempt.From, pos);
            var sourceId = attempt.Source.m_uid;
            var tag = attempt.Source.GetString(ZDOVars.s_tag);
            bool Linked() => ReferenceEquals(adapter._world.GetZDO(sourceId), attempt.Source) &&
                TameTravelRules.Portal(attempt.Source) != null && attempt.Source.GetString(ZDOVars.s_tag) == tag &&
                TameTravelController.PortalPairRejection(attempt.Source, out var destination) == null &&
                destination!.m_uid == attempt.Destination &&
                (destination.GetPosition() + destination.GetRotation() * Vector3.forward *
                    TameTravelRules.Portal(attempt.Source)!.m_exitDistance + Vector3.up - pos).sqrMagnitude < .0001f;
            if (adapter._controller.AcceptSummons && adapter._controller.SummonerReady(attempt.Host))
            {
                adapter.Summons.Begin(__instance, attempt.From, pos, rot, Linked);
                adapter._controller.GuestSummons?.LocalAccepted(attempt.Host, new(attempt.From, pos, rot, Linked));
            }
        }
        catch (Exception error) { adapter._fail(error); }
    }

    private static void BeforeTeleportUpdate(Player __instance, out bool __state) =>
        __state = Active != null && ReferenceEquals(__instance, Player.m_localPlayer) && __instance.IsTeleporting();

    private static void AfterTeleportUpdate(Player __instance, bool __state)
    {
        var adapter = Active;
        if (!__state || __instance.IsTeleporting() || adapter == null ||
            !TameTravelActor.TryLocal(__instance, out var host)) return;
        try
        {
            if (adapter.Summons.Pending && adapter._controller.SummonerReady(host))
                adapter._controller.SummonsArrived(adapter.Summons.Finish(__instance, adapter._controller.RecordSummonMove), __instance);
            else adapter.Summons.Cancel();
            adapter._controller.GuestSummons?.LocalFinished(host, __instance.transform.position);
            adapter._controller.LocalPortalFinished(host, __instance.transform.position);
        }
        catch (Exception error) { adapter._fail(error); }
    }

    public void Dispose()
    {
        if (ReferenceEquals(_active, this)) _active = null;
        _attempt = null;
        Summons.Cancel();
        _hooks.UnpatchSelf();
    }
}
