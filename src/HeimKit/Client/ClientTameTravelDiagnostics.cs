using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Core.Diagnostics;
using UnityEngine;

namespace HeimKit.Client;

internal sealed class ClientTameTravelDiagnostics : IDisposable
{
    private static ClientTameTravelDiagnostics? _active;
    private static readonly HashSet<int> Animals = new()
    {
        "Skeleton_Friendly".GetStableHashCode(), "Wolf".GetStableHashCode(), "Boar".GetStableHashCode(),
        "Lox".GetStableHashCode(), "Asksvin".GetStableHashCode(), "Hen".GetStableHashCode(), "Moose".GetStableHashCode()
    };
    private readonly ConfigEntry<bool> _enabled;
    private readonly ManualLogSource _log;
    private readonly ClientTraceWriter _writer;
    private readonly DiagnosticBudget _budget = new();
    private readonly List<ZDOID> _watch = new();
    private Harmony? _hooks;
    private string? _gateway;
    private Player? _player;
    private ZDOMan? _world;
    private ZDOID _character;
    private Vector3 _source, _exit;
    private float _deadline, _next;
    private bool _finished;

    internal ClientTameTravelDiagnostics(ConfigEntry<bool> enabled, ManualLogSource log)
    {
        _enabled = enabled;
        _log = log;
        _writer = new(log, "HeimKit-tametravel.log");
        enabled.SettingChanged += Changed;
        Configure();
    }

    private void Changed(object sender, EventArgs args) => Configure();
    private void Configure()
    {
        if (!_enabled.Value) { Stop(); return; }
        if (_hooks != null) return;
        try
        {
            _hooks = new Harmony("heimkit.client.tametravel.trace");
            Gateway(typeof(TeleportWorld), "Teleport", nameof(Portal));
            Gateway(typeof(Teleport), "Interact", nameof(Dungeon));
            After(typeof(Player), "TeleportTo", nameof(Accepted));
            After(typeof(SpawnAbility), "Setup", nameof(Cast));
            After(typeof(Tameable), "RPC_Command", nameof(Command));
            _active = this;
            Write("enabled");
        }
        catch (Exception error) { Fail(error); }
    }

    private static HarmonyMethod Hook(string name) => new(typeof(ClientTameTravelDiagnostics), name);
    private void Gateway(Type type, string method, string prefix) => _hooks!.Patch(AccessTools.DeclaredMethod(type, method),
        prefix: Hook(prefix), finalizer: Hook(nameof(EndGateway)));
    private void After(Type type, string method, string callback) =>
        _hooks!.Patch(AccessTools.DeclaredMethod(type, method), postfix: Hook(callback));

    private static void Portal(Player player, out string? __state) => Enter(player, "portal", out __state);
    private static void Dungeon(Humanoid character, out string? __state)
    {
        __state = _active?._gateway;
        var c = _active;
        if (c == null) return;
        try { Enter(character as Player, Character.InInterior(character.transform.position) ? "dungeon_exit" : "dungeon_entry", out __state); }
        catch (Exception error) { c.Fail(error); }
    }
    private static void Enter(Player? player, string gate, out string? previous)
    {
        var c = _active;
        previous = c?._gateway;
        if (c != null) c._gateway = ReferenceEquals(player, Player.m_localPlayer) ? gate : null;
    }
    private static void EndGateway(string? __state) { if (_active is { } c) c._gateway = __state; }

    private static void Cast(SpawnAbility __instance, Character owner, ItemDrop.ItemData item)
    {
        var c = _active;
        if (c == null || owner != Player.m_localPlayer) return;
        try
        {
            if (item?.m_dropPrefab != null && Utils.GetPrefabName(item.m_dropPrefab) == "StaffSkeleton" &&
                Utils.GetPrefabName(__instance.gameObject) == "staff_skeleton_spawn")
                c.Event("dead_raiser_cast", $"character={owner.GetZDOID()}");
        }
        catch (Exception error) { c.Fail(error); }
    }

    private static void Command(Tameable __instance, ZDOID characterID, bool message)
    {
        var c = _active;
        if (c == null) return;
        try
        {
            var z = __instance.GetComponent<ZNetView>()?.GetZDO();
            if (z != null && z.IsOwner() && Animals.Contains(z.GetPrefab()))
                c.Event("native_command", $"character={characterID} message={message} animal={z.m_uid} prefab={Utils.GetPrefabName(__instance.gameObject)} owner={z.GetOwner()} follow='{z.GetString(ZDOVars.s_follow)}'");
        }
        catch (Exception error) { c.Fail(error); }
    }

    private static void Accepted(Player __instance, Vector3 pos, bool __result)
    {
        var c = _active;
        if (c == null || __instance != Player.m_localPlayer) return;
        try
        {
            if (__result) c.ResetTrip();
            if (c._gateway == null) return;
            c.Event(__result ? "player_travel_accepted" : "player_travel_rejected",
                $"gateway={c._gateway} character={__instance.GetZDOID()} from={__instance.transform.position} target={pos}");
            if (!__result) return;
            c._world = ZDOMan.instance;
            c._player = __instance;
            c._character = __instance.GetZDOID();
            c._source = __instance.transform.position;
            c._exit = pos;
            c._deadline = Time.unscaledTime + 90;
            foreach (var animal in Character.GetAllCharacters())
            {
                if (animal == null || (animal.transform.position - c._source).sqrMagnitude > 32 * 32) continue;
                var z = animal.GetComponent<ZNetView>()?.GetZDO();
                if (z == null || !Animals.Contains(z.GetPrefab()) || !animal.IsTamed() || animal.IsDead()) continue;
                c._watch.Add(z.m_uid);
                c.Animal("departure_scene", animal, z);
                if (c._watch.Count == 32) break;
            }
        }
        catch (Exception error) { c.Fail(error); }
    }

    internal void Tick(float now)
    {
        if (_world == null) return;
        try
        {
            if (!ReferenceEquals(_world, ZDOMan.instance) || _player == null || _player != Player.m_localPlayer ||
                _player.GetZDOID() != _character || _player.IsDead()) { ResetTrip(); return; }
            if (now >= _deadline)
            {
                Event("observation_ended", $"character={_character} departure_animals_not_seen_at_destination={string.Join(",", _watch)} player_finished={_finished}");
                ResetTrip(); return;
            }
            if (_player.IsTeleporting()) return;
            if (!_finished)
            {
                var actual = _player.transform.position;
                bool arrived = (actual - _exit).sqrMagnitude <= 4;
                string result = arrived ? "player_arrived" : (actual - _source).sqrMagnitude <= 4 ? "player_returned_to_source" : "player_finished_elsewhere";
                Event(result, $"character={_character} actual={actual} target={_exit}");
                if (!arrived) { ResetTrip(); return; }
                _finished = true;
                _deadline = now + 60;
            }
            if (now < _next) return;
            _next = now + .25f;
            for (int i = _watch.Count - 1; i >= 0; i--)
            {
                var obj = ZNetScene.instance.FindInstance(_watch[i]);
                if (obj == null || (obj.transform.position - _exit).sqrMagnitude > 32 * 32 ||
                    (obj.transform.position - _exit).sqrMagnitude >= (obj.transform.position - _source).sqrMagnitude) continue;
                var animal = obj.GetComponent<Character>();
                var z = obj.GetComponent<ZNetView>()?.GetZDO();
                if (animal == null || z == null || z.m_uid != _watch[i] || !Animals.Contains(z.GetPrefab())) continue;
                Animal("destination_scene", animal, z);
                _watch.RemoveAt(i);
            }
            if (_watch.Count == 0) ResetTrip();
        }
        catch (Exception error) { Fail(error); }
    }

    private void Animal(string stage, Character animal, ZDO z) => Event(stage,
        $"character={_character} animal={z.m_uid} prefab={Utils.GetPrefabName(animal.gameObject)} owner={z.GetOwner()} follow='{z.GetString(ZDOVars.s_follow)}' health={animal.GetHealth()} level={animal.GetLevel()} pos={animal.transform.position} record_pos={z.GetPosition()}");
    private void Event(string stage, string fields)
    {
        if (_budget.Reserve(Time.realtimeSinceStartup, stage, 96, false))
            Write($"{stage} local_peer={ZDOMan.GetSessionID()} {fields}");
    }
    private void Write(string text) => _writer.Write($"{DateTime.Now:HH:mm:ss.fff} [HeimKit] [TameTravel.client] {text}");
    private void ResetTrip()
    {
        _watch.Clear(); _world = null; _player = null; _finished = false; _next = 0;
    }
    private void Fail(Exception error)
    {
        Stop(); _enabled.Value = false;
        _log.LogWarning("Client TameTravel logging stopped: " + error.Message);
    }
    private void Stop()
    {
        if (ReferenceEquals(_active, this)) _active = null;
        _hooks?.UnpatchSelf(); _hooks = null; _gateway = null;
        ResetTrip(); _budget.Reset(); _writer.Dispose();
    }
    public void Dispose() { _enabled.SettingChanged -= Changed; Stop(); }
}
