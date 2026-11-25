using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Core.Control;
using HeimKit.Core.Control.Models;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.World;
using UnityEngine;

namespace HeimKit.Client;

internal sealed class ControlPanel : IDisposable
{
    private static bool _open;
    private static int _escapeFrame = -1;
    private readonly ClientSettings _settings;
    private readonly ClientLink _link;
    private readonly ManualLogSource _log;
    private readonly string _version;
    private readonly ControlPanelView _view;
    private Harmony? _hooks;
    private Hud? _hiddenReticleHud;
    private bool _crosshairEnabled, _bowCrosshairEnabled;
    private bool _patchFailed, _refreshPending;

    internal ControlPanel(GameObject owner, ClientSettings settings, ClientLink link, string version, ManualLogSource log)
    {
        _settings = settings; _link = link; _version = version; _log = log;
        _view = owner.AddComponent<ControlPanelView>();
        _view.Moderate = _link.Moderate;
        _link.ModerationCleared += _view.ClearModeration;
        _view.Click = entry => _link.Set(entry, Time.unscaledTime);
        _view.SetInteractionTrace = value => _settings.TraceObjectInteractions.Value = value;
        _view.SetTameTravelTrace = value => _settings.TraceTameTravel.Value = value;
        _view.ApplyNumber = (entry, value) => _link.Set(entry, value, Time.unscaledTime);
        _settings.MenuKey.SettingChanged += SettingChanged;
        _settings.TraceObjectInteractions.SettingChanged += TraceSettingChanged;
        _settings.TraceTameTravel.SettingChanged += TraceSettingChanged;
        _link.Changed += QueueRefresh;
    }

    internal void Tick(float now)
    {
        if (_refreshPending)
        {
            _refreshPending = false;
            Refresh();
        }
        var key = _settings.MenuKey.Value;
        if (_open)
        {
            bool escape = ZInput.GetKeyDown(KeyCode.Escape, false);
            if (escape) _escapeFrame = Time.frameCount;
            if (_link.State == LinkState.NotInWorld || Player.m_localPlayer == null || NativeUiOpen() ||
                escape || (key != KeyCode.None && ZInput.GetKeyDown(key, false)))
                Close();
            else
            {
                HideReticle();
                _view.UpdateSliderPointer();
                _view.ScrollUnderPointer();
            }
            return;
        }

        if (key == KeyCode.None || !ZInput.GetKeyDown(key, false) || _patchFailed) return;
        if (_link.State == LinkState.NotInWorld || Game.instance == null || ZNet.instance == null ||
            Player.m_localPlayer == null || NativeUiOpen()) return;
        try
        {
            Install();
            if (!_view.Open(Close)) return;
        }
        catch (Exception error)
        {
            _patchFailed = true;
            DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Control, "HeimKit Settings could not open.", level: LogLevel.Error, error: error);
            return;
        }
        _open = true;
        HideReticle();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        _link.Refresh(now);
        Refresh();
    }

    private static bool NativeUiOpen() =>
        global::Console.IsVisible() || TextInput.IsVisible() || Minimap.InTextInput() || Minimap.IsOpen() ||
        InventoryGui.IsVisible() || Menu.IsVisible() || Menu.IsActive() || Hud.IsPieceSelectionVisible() ||
        (Chat.instance != null && Chat.instance.HasFocus());

    private void Install()
    {
        if (_hooks != null) return;
        var hooks = new Harmony("heimkit.client.control");
        try
        {
            var input = new HarmonyMethod(typeof(ControlPanel), nameof(Input));
            hooks.Patch(AccessTools.Method(typeof(PlayerController), "TakeInput", new[] { typeof(bool) }), prefix: input);
            hooks.Patch(AccessTools.Method(typeof(Player), "TakeInput", Type.EmptyTypes), prefix: input);
            hooks.Patch(AccessTools.Method(typeof(GameCamera), "UpdateMouseCapture"), prefix: new HarmonyMethod(typeof(ControlPanel), nameof(Mouse)));
            hooks.Patch(AccessTools.Method(typeof(Menu), "Update"), prefix: new HarmonyMethod(typeof(ControlPanel), nameof(PauseMenu)));
            hooks.Patch(AccessTools.Method(typeof(GameCamera), "UpdateCamera", new[] { typeof(float) }),
                transpiler: new HarmonyMethod(typeof(ControlPanel), nameof(CameraScroll)));
            _hooks = hooks;
        }
        catch { hooks.UnpatchSelf(); throw; }
    }

    private static IEnumerable<CodeInstruction> CameraScroll(IEnumerable<CodeInstruction> instructions)
    {
        var native = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel));
        var guarded = AccessTools.Method(typeof(ControlPanel), nameof(CameraWheel));
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, native)) instruction.operand = guarded;
            yield return instruction;
        }
    }

    private static float CameraWheel() => _open ? 0f : ZInput.GetMouseScrollWheel();

    private static bool Input(ref bool __result)
    {
        if (!_open) return true;
        __result = false;
        return false;
    }

    private static bool Mouse()
    {
        if (!_open) return true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        return false;
    }

    private static bool PauseMenu() => !_open && (_escapeFrame < 0 || ClearConsumedEscape());

    private static bool ClearConsumedEscape()
    {
        if (_escapeFrame == Time.frameCount) return false;
        _escapeFrame = -1;
        return true;
    }

    private void HideReticle()
    {
        var hud = Hud.instance;
        if (hud == _hiddenReticleHud) return;
        RestoreReticle();
        if (hud == null || hud.m_crosshair == null || hud.m_crosshairBow == null) return;
        _hiddenReticleHud = hud;
        _crosshairEnabled = hud.m_crosshair.enabled;
        _bowCrosshairEnabled = hud.m_crosshairBow.enabled;
        // Let Hud update the active objects, hover information and bow state normally.
        hud.m_crosshair.enabled = hud.m_crosshairBow.enabled = false;
    }

    private void RestoreReticle()
    {
        var hud = _hiddenReticleHud;
        _hiddenReticleHud = null;
        if (hud == null) return;
        if (hud.m_crosshair != null) hud.m_crosshair.enabled = _crosshairEnabled;
        if (hud.m_crosshairBow != null) hud.m_crosshairBow.enabled = _bowCrosshairEnabled;
    }

    private void Close()
    {
        _open = false;
        RestoreReticle();
        _view.Close();
    }

    private void SettingChanged(object sender, EventArgs args)
    {
        if (_open && _settings.MenuKey.Value == KeyCode.None) Close();
        Refresh();
    }

    // A local setting save can update the snapshot synchronously from a
    // Unity click callback. Rebuild at the panel tick, after input dispatch.
    private void TraceSettingChanged(object sender, EventArgs args) => QueueRefresh();

    private void QueueRefresh() => _refreshPending = true;

    private void Refresh()
    {
        if (_link.State == LinkState.NotInWorld) { Close(); _view.ResetRows(); return; }
        if (!_open) return;
        var snapshot = _link.Snapshot;
        bool local = _link.State == LinkState.LocalHostedWorld;
        _view.ConnectionType = !local ? "Server" :
            WorldSession.Identify(ZNet.instance) == WorldSessionRole.NetworkHost ? "Hosted world" : "Local world";
        _view.ConnectionStatus = _link.State switch
        {
            LinkState.Connected or LinkState.LocalHostedWorld => "Connected",
            LinkState.Checking => "Checking…",
            _ => "Not connected"
        };
        bool canAdminister = snapshot?.IsAdmin == true && snapshot.AdminChangesAllowed;
        _view.Moderation = _link.Moderation;
        _view.Access = canAdminister ? "Admin" : "Read-only";
        string server = snapshot?.ServerVersion ?? _link.DisabledServerVersion ?? "Unknown";
        _view.Footer = local ? $"Client version: {_version}" : $"Client version: {_version}   |   Server version: {server}";
        _view.SettingsSource = _link.State switch
        {
            LinkState.Connected => "Server config",
            LinkState.ServerDisabled => "Client support disabled",
            LinkState.Checking => "Awaiting server",
            LinkState.VersionMismatch => "Protocol mismatch",
            LinkState.LocalHostedWorld => snapshot != null ? "Local config" :
                string.IsNullOrEmpty(_link.LocalStatus) ? "Starting local runtime" : _link.LocalStatus,
            _ => "Unavailable"
        };
        _view.MenuShortcut = _settings.MenuKey.Value.ToString();
        _view.TraceTameTravel = _settings.TraceTameTravel.Value;
        _view.TraceInteractions = _settings.TraceObjectInteractions.Value;
        _view.LocalConfigHelp = $"Change [Client] MenuKey and restart Valheim.\n\n{_settings.ConfigPath}";
        _view.AdminMessage = snapshot?.IsAdmin == true && !snapshot.AdminChangesAllowed
            ? "Admin changes are turned off in the server configuration." : "";
        _view.CanEdit = canAdminister && !_link.Waiting;
        var rows = new List<ControlPanelView.Row>();
        if (_link.State is LinkState.ClientOnly or LinkState.ServerDisabled || local && snapshot == null)
            foreach (string name in ControlProtocol.ModuleNames)
                rows.Add(new ControlPanelView.Row(name, _link.State switch
                {
                    LinkState.ServerDisabled => "Server settings unavailable while client support is disabled",
                    LinkState.LocalHostedWorld => _link.LocalStatus,
                    _ => "Needs HeimKit on the server"
                }));
        if ((_link.State == LinkState.Connected || local) && snapshot != null)
        {
            ControlPanelView.Row? row = null;
            foreach (var entry in snapshot.Switches)
            {
                string feedback = Feedback(entry);
                if (entry.Definition.IsModuleSwitch)
                {
                    row = new ControlPanelView.Row(entry.Definition.Module, Status(entry)) { Entry = entry, Feedback = feedback };
                    rows.Add(row);
                }
                else if (row != null && row.Name == entry.Definition.Module)
                    row.Options.Add(new ControlPanelView.Option(entry, feedback));
            }
            foreach (var entry in snapshot.Numbers)
            {
                var owner = rows.Find(candidate => candidate.Name == entry.Module);
                if (owner != null)
                    owner.Numbers.Add((entry, Feedback(entry.Module, entry.Key)));
            }
        }
        if (local && snapshot != null)
        {
            var ordered = new List<ControlPanelView.Row>();
            foreach (string name in ControlProtocol.ModuleNames)
                ordered.Add(rows.Find(row => row.Name == name) ??
                    new ControlPanelView.Row(name, "This feature is unavailable in player-hosted worlds."));
            rows = ordered;
        }
        _view.Rows = rows;
        _view.Refresh();
    }

    private string Feedback(SwitchState entry) =>
        Feedback(entry.Definition.Module, entry.Definition.Key, entry.PendingValue.HasValue);

    private string Feedback(string module, string key, bool hasPending = false)
    {
        const string pending = "Waiting for current work to finish…";
        if (_link.RequestModule != module || _link.RequestKey != key)
            return hasPending ? pending : "";
        if (_link.TimedOut) return "Couldn’t confirm the change.";
        return _link.Result switch
        {
            SetResult.Pending => pending,
            SetResult.Busy => "The feature was busy. Try again.",
            SetResult.NotAdmin => "Only server admins can change this setting.",
            SetResult.AdminChangesDisabled => "Admin changes are turned off in the server configuration.",
            SetResult.UnknownSwitch => "That setting is unavailable on this server.",
            SetResult.RateLimited => "Too many requests. Try again shortly.",
            SetResult.Unavailable => "The feature is unavailable on the server.",
            SetResult.Failed => "The server could not apply the setting.",
            SetResult.InvalidValue => "The server rejected this value. Check the allowed range.",
            _ => hasPending ? pending : _link.Waiting ? "Applying…" : ""
        };
    }

    private string Status(SwitchState entry) => entry.Reason switch
    {
        UnavailableReason.UnsupportedGameVersion => "The host is running an unsupported game version.",
        UnavailableReason.StoppedAfterError => "The feature stopped. See the host's HeimKit log for the error.",
        UnavailableReason.FailureProtection when _link.State == LinkState.LocalHostedWorld =>
            "Disabled for this world session. See your game's HeimKit log for the error.",
        UnavailableReason.FailureProtection =>
            "Server failure protection is holding this feature off. See the server's HeimKit log for the error.",
        _ => ""
    };

    public void Dispose()
    {
        Close();
        _escapeFrame = -1;
        _link.Changed -= QueueRefresh;
        _link.ModerationCleared -= _view.ClearModeration;
        _settings.MenuKey.SettingChanged -= SettingChanged;
        _settings.TraceObjectInteractions.SettingChanged -= TraceSettingChanged;
        _settings.TraceTameTravel.SettingChanged -= TraceSettingChanged;
        try { _hooks?.UnpatchSelf(); }
        finally { UnityEngine.Object.Destroy(_view); }
    }
}
