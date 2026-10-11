using BepInEx.Configuration;
using HeimKit.Configuration;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuardSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }

    public WeatherGuardSettings(ConfigFile config)
    {
        Enabled = config.Bind("WeatherGuard", "Enabled", false,
            "Enable automatic rain/water-wear protection for player-built structures using server processing. Preserve creators. World-created repair claims are inactive. Restart after edits.");
        DebugMode = DiagnosticsSettings.Bind(config);
    }
}
