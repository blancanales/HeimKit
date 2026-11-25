using BepInEx.Configuration;

namespace HeimKit.Configuration;

internal sealed class ControlSettings
{
    internal ControlSettings(ConfigFile config)
    {
        Enabled = config.Bind("Client", "Enabled", true, "Answer HeimKit clients and send feature status. Restart after file edits.");
        AllowAdminChanges = config.Bind("Client", "AllowAdminChanges", true, "Allow players on the game admin list to change features in HeimKit Settings. Restart after file edits.");
    }

    internal ConfigEntry<bool> Enabled { get; }
    internal ConfigEntry<bool> AllowAdminChanges { get; }
}
