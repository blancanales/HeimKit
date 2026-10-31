using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.WeatherGuard.Models;
namespace HeimKit.Features.WeatherGuard;

public static class WeatherGuardRules
{
    public const int MaximumRegions = 8;
    public const int MaximumObjects = 32768;
    public const int MaximumProtected = 8192;
    public static bool SuppressWeatherDamage(bool nativeWeather, bool protectRain, bool protectUnderwater, bool rainWet, bool underwater)
        => nativeWeather && (protectRain && protectUnderwater ||
            (rainWet || underwater) && (!rainWet || protectRain) && (!underwater || protectUnderwater));
    public static System.Collections.Generic.HashSet<string> ParseExcludedPrefabs(string value, out int rejected)
    {
        var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        rejected = 0;
        if (string.IsNullOrWhiteSpace(value)) return names;
        foreach (string token in value.Split(','))
        {
            string name = token.Trim();
            bool valid = name.Length > 0;
            foreach (char c in name) if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_')) valid = false;
            if (!valid || names.Count >= 1024 && !names.Contains(name)) { rejected++; continue; }
            names.Add(name);
        }
        return names;
    }
    public static string SelectionReason(string nativeReason, string prefab, System.Collections.Generic.HashSet<string> excluded, bool rain, bool underwater)
    {
        if (nativeReason != "eligible" && nativeReason != "world-created") return nativeReason;
        if (excluded.Contains(prefab)) return "excluded_prefab";
        if (!rain && !underwater) return "weather_protection_disabled";
        return nativeReason;
    }
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
    public static bool RequiresContext(int colliders, bool supports, bool shield)
        => colliders != 0 || supports || shield;
    public static bool ColliderUsesContextMask(int layer, int mask)
        // Before native initialization, retain every potentially relevant collider.
        => mask == 0 || layer < 0 || layer > 31 || (mask & (1 << layer)) != 0;
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
