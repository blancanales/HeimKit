using BepInEx.Configuration;
using HeimKit.Features.WeatherGuard.Models;

namespace HeimKit.Features.WeatherGuard;

// BepInEx's generic value list requires IEquatable<T>, which enums do not implement.
internal sealed class WeatherGuardDebugLogLevelValues : AcceptableValueBase
{
    public WeatherGuardDebugLogLevelValues() : base(typeof(DebugLogLevel)) { }
    public override bool IsValid(object value)
        => value is DebugLogLevel level && (level == DebugLogLevel.Medium || level == DebugLogLevel.Detailed);
    public override object Clamp(object value) => IsValid(value) ? value : DebugLogLevel.Detailed;
    public override string ToDescriptionString() => "# Acceptable values: Medium, Detailed";
}
