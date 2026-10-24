using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.WeatherGuard.Models;

public sealed class RainEvidence
{
    public System.Collections.Generic.Dictionary<string, object?> Fields { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, object?> Identity { get; set; } = new();
    public bool? FreshRoof { get; set; }
    public float FreshRoofAt { get; set; }
    public string Eligibility { get; set; } = "";
    public float Started { get; set; }
    public float FirstHealth { get; set; }
    public float MaximumHealth { get; set; }
    public float Health { get; set; }
    public float MinimumHealth { get; set; }
    public float LastGateTime { get; set; } = -1;
    public float WetSeconds { get; set; }
    public float LongestWetSeconds { get; set; }
    public long Gates { get; set; }
    public long WetGates { get; set; }
    public long WeatherBypasses { get; set; }
    public float HealthLost { get; set; }
    public long HealthLossObservations { get; set; }
    public string ProtectionReason { get; set; } = "not_observed";
    public long Owner { get; set; }
    public int OwnerChanges { get; set; }
    public int ProtectionChanges { get; set; }
    public bool Protected { get; set; }
    public bool CreatorChanged { get; set; }
    public long Creator { get; set; }

    public void ObserveGate(float time, bool wet, bool branchBypassed = false)
    {
        Gates++;
        if (branchBypassed) WeatherBypasses++;
        if (wet) WetGates++;
        // A missed processing interval must not count as continuous protection.
        float gap = time - LastGateTime;
        if (!wet) WetSeconds = 0;
        else if (LastGateTime < 0 || gap < 0 || gap > 10) WetSeconds = 0;
        else WetSeconds += gap;
        LastGateTime = wet ? time : -1;
        if (WetSeconds > LongestWetSeconds) LongestWetSeconds = WetSeconds;
    }

    public void ObserveState(float health, long owner, bool protectedPiece, long creator)
    {
        if (health < Health) { HealthLost += Health - health; HealthLossObservations++; }
        Health = health;
        if (health < MinimumHealth) MinimumHealth = health;
        if (Owner != owner) { OwnerChanges++; WetSeconds = 0; LastGateTime = -1; }
        if (Protected != protectedPiece) { ProtectionChanges++; WetSeconds = 0; LastGateTime = -1; }
        CreatorChanged |= Creator != creator;
        Owner = owner;
        Protected = protectedPiece;
    }
}
