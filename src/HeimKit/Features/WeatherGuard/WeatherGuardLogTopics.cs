using HeimKit.Core.Diagnostics;

namespace HeimKit.Features.WeatherGuard;

public static class WeatherGuardLogTopics
{
    public static readonly LogTopic Protection = new("protection");
    public static readonly LogTopic Ownership = new("ownership");
    public static readonly LogTopic Wear = new("wear");
    public static readonly LogTopic Rain = new("rain");
    public static readonly LogTopic Coverage = new("coverage");
}
