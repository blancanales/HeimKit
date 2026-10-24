using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;

namespace HeimKit.Features.HeimMender;
internal sealed class HeimMenderAdministration : IDisposable
{
    private readonly Dictionary<string, Terminal.ConsoleCommand> _commands;
    private readonly Terminal.ConsoleCommand _command;
    public HeimMenderAdministration(Action<string, float?> request, Func<string> status)
    {
        NativeAccess.Method<Action>(typeof(Terminal), "InitTerminal")();
        _commands = NativeAccess.StaticField<Dictionary<string, Terminal.ConsoleCommand>>(typeof(Terminal), "commands")();
        if (_commands.ContainsKey("heimmender")) throw new InvalidOperationException("Another provider registered heimmender.");
        _command = new Terminal.ConsoleCommand("heimmender", "status | repair <peerUID-or-name> [radius] (dedicated-server terminal)", (Terminal.ConsoleEvent)(args =>
        {
            if (args.Length == 2 && args[1] == "status") { args.Context?.AddString(status()); return; }
            if (args.Length < 3 || args.Length > 4 || args[1] != "repair") { args.Context?.AddString("Usage: heimmender repair <peerUID-or-name> [radius]"); return; }
            float? radius = null;
            if (args.Length == 4)
            { if (!float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) { args.Context?.AddString("Invalid radius."); return; } radius = value; }
            request(args[2], radius);
        }), onlyServer: true);
    }
    public void Dispose()
    { if (_commands.TryGetValue("heimmender", out var command) && ReferenceEquals(command, _command)) _commands.Remove("heimmender"); }
}
