using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
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
