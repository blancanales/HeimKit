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
            bool wet = true, float maximum = 5f)
            => (string?)reject.Invoke(null, new object[] { name, refill, infinite, toggle, wet, maximum });
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

        var fresh = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false)
            { SaveOnConfigSet = false };
        var settings = new InfiniteTorchesSettings(fresh);
        var defaults = settings.Prefabs.Value.Split(',');
        if (defaults.Length != 8 || !braziers.All(defaults.Contains) || defaults.Contains("fire_pit") || defaults.Contains("hearth"))
            throw new Exception("Default light-source scope is incorrect.");
        var custom = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false)
            { SaveOnConfigSet = false };
        custom.Bind("Torches", "Prefabs", "piece_walltorch");
        if (new InfiniteTorchesSettings(custom).Prefabs.Value != "piece_walltorch")
            throw new Exception("Upgrade overwrote the operator's prefab list.");
        System.Console.WriteLine("PASS: all three brazier variants qualify; fuel, toggle, finite-capacity and exact wet-flame scope guards remain; fresh defaults include braziers and existing lists are preserved.");
    }
}
