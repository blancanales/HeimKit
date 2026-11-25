using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Core.Diagnostics;
using UnityEngine;

namespace HeimKit.Client;

internal sealed class ClientInteractionDiagnostics : IDisposable
{
    private static ClientInteractionDiagnostics? _active;
    private readonly ConfigEntry<bool> _enabled;
    private readonly ManualLogSource _log;
    private readonly DiagnosticBudget _budget = new();
    private Harmony? _hooks;
    private readonly ClientTraceWriter _writer;

    internal ClientInteractionDiagnostics(ConfigEntry<bool> enabled, ManualLogSource log)
    {
        _enabled = enabled;
        _log = log;
        _writer = new(log, "HeimKit-interactions.log");
        _enabled.SettingChanged += Changed;
        Configure();
    }

    private void Changed(object sender, EventArgs args) => Configure();

    private void Configure()
    {
        if (!_enabled.Value) { Stop(); return; }
        if (_hooks != null) return;
        try
        {
            _hooks = new Harmony("heimkit.client.interactions");
            Patch(typeof(Container), "Interact", nameof(Interact));
            Patch(typeof(Container), "RPC_RequestOpen", nameof(Request));
            Patch(typeof(Container), "RPC_RequestOpen", nameof(RequestHandled), after: true);
            Patch(typeof(Container), "RPC_OpenResponse", nameof(Response));
            Patch(typeof(InventoryGui), "Show", nameof(Shown), after: true);
            Patch(typeof(InventoryGui), "CloseContainer", nameof(Closing));
            _writer.Write($"{DateTime.Now:HH:mm:ss.fff} [HeimKit] [interaction] Client object interaction logging enabled.");
            _active = this;
        }
        catch (Exception error)
        {
            Stop();
            _enabled.Value = false;
            _log.LogWarning("Client interaction logging could not start: " + error.Message);
        }
    }

    private void Patch(Type type, string method, string callback, bool after = false)
    {
        var observer = new HarmonyMethod(typeof(ClientInteractionDiagnostics), callback);
        _hooks!.Patch(AccessTools.DeclaredMethod(type, method), prefix: after ? null : observer, postfix: after ? observer : null);
    }

    private static void Interact(Container __instance, Humanoid __0, bool __1)
    {
        if (!__1 && __0 == Player.m_localPlayer) Record(__instance, "interact");
    }
    private static void Request(Container __instance, long __0) => Record(__instance, "request_received", requester: __0);
    private static void RequestHandled(Container __instance, long __0) => Record(__instance, "request_handler_finished", requester: __0);
    private static void Response(Container __instance, long __0, bool __1) =>
        Record(__instance, __1 ? "granted" : "denied", responder: __0);
    private static void Shown(Container __0, Container ___m_currentContainer)
    {
        if (__0 != null && __0 == ___m_currentContainer) Record(__0, "inventory_open");
    }
    private static void Closing(Container ___m_currentContainer) => Record(___m_currentContainer, "inventory_closing");

    private static void Record(Container container, string stage, long? requester = null, long? responder = null)
    {
        var active = _active;
        if (active == null || container == null) return;
        try
        {
            float now = Time.realtimeSinceStartup;
            if (!active._budget.Available(now, stage, 24)) return;
            var view = container.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !active._budget.Reserve(now, stage, 24, false)) return;
            var obj = view.GetZDO();
            long peer = ZDOMan.GetSessionID();
            active._writer.Write(FormattableString.Invariant(
                $"{DateTime.Now:HH:mm:ss.fff} [HeimKit] [interaction] client {stage} object={obj.m_uid} requester={requester ?? peer} local_peer={peer} responder={responder} owner={obj.GetOwner()} in_use={obj.GetInt(ZDOVars.s_inUse)} local_ms={(long)(now * 1000)}"));
        }
        catch (Exception error)
        {
            _active = null;
            active._enabled.Value = false;
            active._log.LogWarning("Client interaction logging stopped: " + error.Message);
        }
    }

    private void Stop()
    {
        if (ReferenceEquals(_active, this)) _active = null;
        _hooks?.UnpatchSelf();
        _hooks = null;
        _writer.Dispose();
        _budget.Reset();
    }

    public void Dispose()
    {
        _enabled.SettingChanged -= Changed;
        Stop();
    }
}
