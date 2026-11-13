using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.StationSupply.Models;

namespace HeimKit.Features.StationSupply;

public sealed class StationSupplySettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public ConfigEntry<float> ChestRangeMeters { get; }
    public ConfigEntry<float> ChestHeightToleranceMeters { get; }
    public ConfigEntry<float> ScanRangeMeters { get; }
    public ConfigEntry<float> IntervalSeconds { get; }
    public ConfigEntry<int> MaxStationsPerPass { get; }
    public ConfigEntry<int> MaxItemsPerVisit { get; }
    public ConfigEntry<int> MaxActiveTransactions { get; }
    public ConfigEntry<bool> Smelters { get; }
    public ConfigEntry<bool> CharcoalKilns { get; }
    public ConfigEntry<bool> BlastFurnaces { get; }
    public ConfigEntry<bool> Windmills { get; }
    public ConfigEntry<bool> SpinningWheels { get; }
    public ConfigEntry<bool> EitrRefineries { get; }
    public ConfigEntry<bool> FrigidKilns { get; }
    public ConfigEntry<bool> ShieldGenerators { get; }
    public ConfigEntry<bool> Fermenters { get; }
    public ConfigEntry<bool> Obliterators { get; }
    public ConfigEntry<ObliteratorCategories> ObliteratorCategories { get; }
    public StationSupplySettings(ConfigFile config, ManualLogSource log)
    {
        Enabled = config.Bind("StationSupply", "Enabled", true, "Feed production intakes from one nearby public chest each. Restart after edits.");
        DebugMode = config.Bind("StationSupply", "DebugMode", false, "Enable StationSupply diagnostics through the shared policy.");
        ChestRangeMeters = Number(config, "ChestRangeMeters", 2f, 0.5f, 4f, "Horizontal chest-pivot distance from the actual resource intake, not the station centre.");
        ChestHeightToleranceMeters = Number(config, "ChestHeightToleranceMeters", 1.5f, 0.25f, 3f, "Maximum chest-pivot height difference from the station floor. High intakes accept floor-level chests.");
        ScanRangeMeters = Number(config, "ScanRangeMeters", 64f, 16f, 128f, "Processing radius around a rotating ready player.");
        IntervalSeconds = Number(config, "IntervalSeconds", 2f, 0.5f, 10f, "Seconds between bounded station discovery passes.");
        MaxStationsPerPass = config.Bind("StationSupply", "MaxStationsPerPass", 4, new ConfigDescription("Maximum stations considered per pass. Restart after edits.", new AcceptableValueRange<int>(1, 16)));
        MaxItemsPerVisit = config.Bind("StationSupply", "MaxItemsPerVisit", 10, new ConfigDescription("Maximum real items per atomic intake visit, limited by native room. Restart after edits.", new AcceptableValueRange<int>(1, 64)));
        MaxActiveTransactions = config.Bind("StationSupply", "MaxActiveTransactions", 2, new ConfigDescription("Maximum active transactions over disjoint station and chest records. Restart after edits.", new AcceptableValueRange<int>(1, 4)));
        Smelters = Switch(config, "Smelters", "smelters");
        CharcoalKilns = Switch(config, "CharcoalKilns", "charcoal kilns");
        BlastFurnaces = Switch(config, "BlastFurnaces", "blast furnaces");
        Windmills = Switch(config, "Windmills", "windmills");
        SpinningWheels = Switch(config, "SpinningWheels", "spinning wheels");
        EitrRefineries = Switch(config, "EitrRefineries", "eitr refineries");
        FrigidKilns = Switch(config, "FrigidKilns", "Frigid Kiln ice intakes");
        ShieldGenerators = Switch(config, "ShieldGenerators", "player-built shield generators");
        Fermenters = config.Bind("StationSupply", "Fermenters", false, "Fill empty sheltered fermenters from their selected chest. Never tap or collect output. Restart after edits.");
        Obliterators = config.Bind("StationSupply", "Obliterators", false, "Move items from one nearby supply chest into an Obliterator. Lever activation stays manual. Restart after edits.");
        ObliteratorCategories = config.Bind("StationSupply", "ObliteratorCategories", Models.ObliteratorCategories.All,
            "Choose the item categories eligible for Obliterator feeding. All includes every category; None transfers nothing.\n" +
            "To enable multiple categories, separate their names with commas, without quotes.\n" +
            "Example: ObliteratorCategories = Materials, Trophies, Ammo\n" +
            "See docs/features/station-supply.md#obliterator-feeding for the full category list and combination instructions.\n" +
            "Set Obliterators = true to enable feeding. Restart after edits.");
        if ((ObliteratorCategories.Value & ~Models.ObliteratorCategories.All) != 0) throw new ArgumentException("Unsupported Obliterator category flags.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "StationSupply", DiagnosticsSettings.Bind(config), log, DebugMode);
    }
    private static ConfigEntry<bool> Switch(ConfigFile config, string key, string description)
        => config.Bind("StationSupply", key, true, "Feed " + description + ". Restart after edits.");
    private static ConfigEntry<float> Number(ConfigFile config, string key, float value, float min, float max, string description)
    {
        var entry = config.Bind("StationSupply", key, value, new ConfigDescription(description + " Restart after edits.", new AcceptableValueRange<float>(min, max)));
        if (float.IsNaN(entry.Value) || float.IsInfinity(entry.Value)) throw new ArgumentException("StationSupply settings must be finite: " + key);
        return entry;
    }
}
