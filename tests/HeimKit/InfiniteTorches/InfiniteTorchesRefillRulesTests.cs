using System.Reflection;
using BepInEx.Configuration;
using HeimKit.Features.InfiniteTorches;

internal static class InfiniteTorchesRefillRulesTests
{
    public static void Run()
    {
        var rules = typeof(InfiniteTorches).Assembly.GetType("HeimKit.Features.InfiniteTorches.InfiniteTorchesRefillRules")!;
        var reject = rules.GetMethod("RejectionReason", BindingFlags.Static | BindingFlags.NonPublic)!;
        string? Reason(string name, bool refill = true, bool infinite = false, bool toggle = false,
            bool wet = true, float maximum = 5f, bool includeFireplaces = false)
            => (string?)reject.Invoke(null, new object[] { name, refill, infinite, toggle, wet, maximum, includeFireplaces });
        string[] braziers = { "piece_brazierceiling01", "piece_brazierfloor01", "piece_brazierfloor02" };
        foreach (string name in braziers)
        {
            if (Reason(name) != null) throw new Exception("Refillable native wet-flame brazier rejected: " + name);
            if (Reason(name, refill: false) == null || Reason(name, infinite: true) == null ||
                Reason(name, toggle: true) == null)
                throw new Exception("Brazier exception bypassed fuel/toggle safeguards.");
            foreach (float maximum in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                if (Reason(name, maximum: maximum) == null) throw new Exception("Invalid brazier capacity accepted.");
        }
        foreach (string name in new[] { "fire_pit", "hearth", "prop_piece_brazierfloor01",
            "piece_brazierfloor01_custom", "PIECE_BRAZIERFLOOR01", "piece_walltorch" })
            if (Reason(name) == null) throw new Exception("Wet-flame exception escaped exact brazier scope: " + name);
        if (Reason("piece_walltorch", wet: false, maximum: 10f) != null)
            throw new Exception("Ordinary torch eligibility regressed.");

        foreach (string name in new[] { "fire_pit", "hearth", "bonfire" })
        {
            if (Reason(name, includeFireplaces: true) != null)
                throw new Exception("Enabled fireplace rejected: " + name);
            if (Reason(name, wet: false) == null)
                throw new Exception("Disabled fireplace accepted through a custom list: " + name);
            if (Reason(name, refill: false, includeFireplaces: true) == null ||
                Reason(name, infinite: true, includeFireplaces: true) == null ||
                Reason(name, toggle: true, includeFireplaces: true) == null ||
                Reason(name, maximum: float.NaN, includeFireplaces: true) == null)
                throw new Exception("Fireplace exception bypassed native fuel safeguards.");
        }
        foreach (string name in new[] { "fire_pit_custom", "HEARTH", "prop_bonfire", "piece_walltorch" })
            if (Reason(name, includeFireplaces: true) == null)
                throw new Exception("Fireplace exception escaped its exact scope: " + name);

        var fresh = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false)
            { SaveOnConfigSet = false };
        var settings = new InfiniteTorchesSettings(fresh);
        if (settings.IncludeFireplaces.Value) throw new Exception("Fireplaces must default disabled.");
        if (!settings.Campfires.Value || !settings.Hearths.Value || !settings.Bonfires.Value)
            throw new Exception("Individual fire switches must default enabled behind the disabled master.");
        var enabled = typeof(InfiniteTorchesSettings).GetMethod("FireplaceEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int mask = 0; mask < 16; mask++)
        {
            settings.IncludeFireplaces.Value = (mask & 8) != 0;
            settings.Campfires.Value = (mask & 1) != 0;
            settings.Hearths.Value = (mask & 2) != 0;
            settings.Bonfires.Value = (mask & 4) != 0;
            string[] fires = { "fire_pit", "hearth", "bonfire" };
            for (int i = 0; i < fires.Length; i++)
            {
                bool allowed = (bool)enabled.Invoke(settings, new object[] { fires[i] })!;
                bool expected = (mask & 8) != 0 && (mask & (1 << i)) != 0;
                if (allowed != expected || (Reason(fires[i], includeFireplaces: allowed) == null) != expected)
                    throw new Exception("Fireplace master/type selection mismatch.");
            }
        }
        var defaults = settings.Prefabs.Value.Split(',');
        if (defaults.Length != 8 || !braziers.All(defaults.Contains) || defaults.Contains("fire_pit") || defaults.Contains("hearth"))
            throw new Exception("Default light-source scope is incorrect.");
        var custom = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false)
            { SaveOnConfigSet = false };
        custom.Bind("Torches", "Prefabs", "piece_walltorch");
        custom.Bind("Torches", "IncludeFireplaces", true);
        custom.Bind("Torches", "Hearths", false);
        if (new InfiniteTorchesSettings(custom).Prefabs.Value != "piece_walltorch")
            throw new Exception("Upgrade overwrote the operator's prefab list.");
        var retained = new InfiniteTorchesSettings(custom);
        if (!retained.IncludeFireplaces.Value || retained.Hearths.Value)
            throw new Exception("Existing master/type fireplace settings were overwritten.");
        System.Console.WriteLine("PASS: braziers and configurable campfire, hearth and bonfire qualify; fuel, toggle, finite-capacity and exact wet-flame scope guards remain; fresh defaults include braziers and existing lists are preserved.");
    }
}
