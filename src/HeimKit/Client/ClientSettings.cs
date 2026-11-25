using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace HeimKit.Client;

internal sealed class ClientSettings
{
    internal const string FileName = "heimkit.client.cfg";
    private readonly ConfigFile _file;
    internal ClientSettings(string directory)
    {
        _file = new ConfigFile(Path.Combine(directory, FileName), true);
        MenuKey = _file.Bind("Client", "MenuKey", KeyCode.F7, "Open HeimKit Settings. None disables the menu.");
        TraceTameTravel = _file.Bind("Diagnostics", "TraceTameTravel", false,
            "Record TameTravel observations in this client’s log. Can be changed in F7.");
        TraceObjectInteractions = _file.Bind("Diagnostics", "TraceObjectInteractions", false,
            "Log sampled container interactions on this client. Can be changed in F7.");
    }

    internal string ConfigPath => Path.GetFullPath(_file.ConfigFilePath);
    internal ConfigEntry<KeyCode> MenuKey { get; }
    internal ConfigEntry<bool> TraceTameTravel { get; }
    internal ConfigEntry<bool> TraceObjectInteractions { get; }
    internal ConfigEntry<bool> BindModule(string name) =>
        _file.Bind("Client." + name, "Enabled", true, "Enable this client module when allowed by the server.");
}
