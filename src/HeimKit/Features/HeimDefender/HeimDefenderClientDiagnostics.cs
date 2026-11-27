using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Client;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using UnityEngine;

namespace HeimKit.Features.HeimDefender;
internal sealed class HeimDefenderClientDiagnostics : IDisposable
{
    private readonly Harmony _patch = new("heimkit.heimdefender.client_trace");
    private readonly ConfigEntry<bool> _setting;
    private readonly ClientTraceWriter _trace;
    private readonly DiagnosticBudget _budget = new();
    private readonly ManualLogSource _log;
    private static HeimDefenderClientDiagnostics? _active;
    internal HeimDefenderClientDiagnostics(ConfigEntry<bool> setting, ManualLogSource log)
    {
        _setting = setting;
        _log = log;
        _trace = new(log, "HeimKit-HeimDefender-client.log");
        setting.SettingChanged += Change;
        Change(this, EventArgs.Empty);
    }

    private void Change(object sender, EventArgs args)
    {
        _patch.UnpatchSelf();
        _active = null;
        if (!_setting.Value)
            return;
        try
        {
            _patch.Patch(NativeAccess.Target(typeof(Turret), "RPC_SetTarget", typeof(long), typeof(ZDOID)), postfix: new HarmonyMethod(typeof(HeimDefenderClientDiagnostics), nameof(Observe)));
            _active = this;
            _budget.Reset();
        }
        catch (Exception error)
        {
            _log.LogWarning($"HeimDefender client target trace unavailable: {error.Message}");
        }
    }

    private static void Observe(Turret __instance, long sender, ZDOID character)
    {
        var self = _active;
        if (self == null || Player.m_localPlayer == null || !self._budget.Available(Time.unscaledTime, "target", 24))
            return;
        try
        {
            var view = __instance.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !HeimDefenderBallistaPolicy.Eligible(view.GetZDO()))
                return;
            self._budget.Reserve(Time.unscaledTime, "target", 24);
            var fields = HeimDefenderDiagnostics.Target(view.GetZDO(), character, view.IsOwner() ? "client_owner" : "client_observer");
            self._trace.Write($"{DateTime.Now:HH:mm:ss.fff} HeimDefender.target: {fields} sender={sender} target_players={__instance.m_targetPlayers} target_tames={__instance.m_targetTamed} trophy_tames={__instance.m_targetTamedConfig} stock={view.GetZDO().GetInt(ZDOVars.s_ammo)} capacity={__instance.m_maxAmmo} ammo_type={__instance.GetAmmoType()}");
        }
        catch (Exception error)
        {
            _active = null;
            self._log.LogWarning($"HeimDefender client target trace stopped: {error.Message}");
        }
    }

    public void Dispose()
    {
        _setting.SettingChanged -= Change;
        _active = null;
        _patch.UnpatchSelf();
        _trace.Dispose();
    }
}
