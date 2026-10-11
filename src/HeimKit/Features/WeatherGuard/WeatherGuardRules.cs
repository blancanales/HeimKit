using HeimKit.Features.WeatherGuard.Models;
namespace HeimKit.Features.WeatherGuard;

public static class WeatherGuardRules
{
    public const int MaximumRegions = 8;
    public const int MaximumObjects = 32768;
    public const int MaximumProtected = 8192;
    public static bool Eligible(long creator, bool persistent, bool staticPiece, bool hasWeatherWear, float health, bool buildingCategory, bool container)
        => Exclusion(creator, persistent, staticPiece, hasWeatherWear, health, buildingCategory, container) == "eligible";
    public static string Exclusion(long creator, bool persistent, bool staticPiece, bool hasWeatherWear, float health, bool buildingCategory, bool container)
    {
        if (container) return "container";
        if (!buildingCategory) return "non-building category";
        if (!persistent) return "nonpersistent";
        if (!staticPiece) return "moving piece";
        if (!hasWeatherWear) return "weather immune";
        if (health <= 0 || float.IsNaN(health) || float.IsInfinity(health)) return "dead/invalid health";
        return creator == 0 ? "world-created" : "eligible";
    }
    public static bool MayAcquire(bool instanceLoaded, bool terrainReady, bool surroundingObjectsReady, bool withinCapacity)
        => instanceLoaded && terrainReady && surroundingObjectsReady && withinCapacity;
    public static Routing Route(int method, long target, long server)
    {
        if (method == "RPC_HealthChanged".GetStableHashCode() || method == "RPC_SetSnow".GetStableHashCode() || method == "RPC_CreateFragments".GetStableHashCode())
            return Routing.Drop;
        return target != 0 && target != server ? Routing.Server : Routing.Forward;
    }
    public static (uint Data, ushort Owner) ReassertRevisions(uint serverData, ushort serverOwner, uint clientData, ushort clientOwner)
    {
        if (clientData >= serverData && clientData == uint.MaxValue || clientOwner >= serverOwner && clientOwner == ushort.MaxValue)
            throw new System.InvalidOperationException("Client revision overflow prevents authoritative handover.");
        return (clientData >= serverData ? clientData + 1 : serverData,
            clientOwner >= serverOwner ? (ushort)(clientOwner + 1) : serverOwner);
    }
}
