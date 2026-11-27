using HeimKit.Core.Prefabs;

namespace HeimKit.Features.HeimDefender;
internal static class HeimDefenderBallistaPolicy
{
    internal static readonly int Prefab = "piece_turret".GetStableHashCode();
    private static readonly int Backup = "heimkit.ballista.original".GetStableHashCode();
    private static readonly int Free = "heimkit.ballista.free".GetStableHashCode();
    private static readonly int Maximum = "Turret.m_maxAmmo".GetStableHashCode();
    private static readonly int DefaultAmmo = "Turret.m_defaultAmmo".GetStableHashCode();
    private static readonly int[] Fields =
    {
        "Turret.m_targetPlayers".GetStableHashCode(),
        "Turret.m_targetTamed".GetStableHashCode(),
        "Turret.m_targetTamedConfig".GetStableHashCode()
    };
    internal const string Wood = "TurretBoltWood";
    internal static bool Eligible(ZDO z) => z.IsValid() && z.Persistent && z.GetPrefab() == Prefab && z.GetLong(ZDOVars.s_creator) != 0;
    private static readonly int NextRefill = "heimkit.ballista.nextRefill".GetStableHashCode();
    private static readonly int Cooldown = "Turret.m_attackCooldown".GetStableHashCode();

    internal static bool FieldChangesNeeded(ZDO z, HeimDefenderSettings settings)
    {
        if (z.GetBool(Free))
            return true; // Restore the native magazine from previous HeimKit builds.
        if (settings.Enabled.Value != z.GetInt(Backup, out _))
            return true;
        if (settings.Enabled.Value && (!z.GetBool("HasFields") || !z.GetBool("HasFieldsTurret")))
            return true;
        if (settings.Enabled.Value)
            foreach (int field in Fields)
                if (!z.GetBool(field, out bool value) || value)
                    return true;
        return false;
    }

    internal static string RefillStatus(ZDO z, HeimDefenderSettings settings, Turret prefab, out int capacity, out int batch, out double interval)
    {
        capacity = z.GetInt(Maximum, prefab.m_maxAmmo);
        batch = System.Math.Min(settings.BoltsPerReload.Value, capacity);
        // The first shot can fire immediately. A single-bolt batch still observes one cooldown.
        interval = (double)System.Math.Max(1, batch - 1) * z.GetFloat(Cooldown, prefab.m_attackCooldown);
        if (!settings.Enabled.Value || !settings.FreeWoodBolts.Value) return "disabled";
        if (capacity <= 0 || !(interval > 0) || double.IsInfinity(interval)) return "invalid_magazine";
        if (prefab.m_defaultAmmo != null || z.GetString(DefaultAmmo).Length > 0) return "fixed_ammo_type";
        int stock = z.GetInt(ZDOVars.s_ammo);
        if (stock < 0) return "invalid_stock";
        if (ZNet.instance.GetTime().Ticks < z.GetLong(NextRefill)) return "cooldown";
        if (stock >= batch) return "at_target";
        string ammo = settings.SelectedAmmo;
        if (stock > 0 && z.GetString(ZDOVars.s_ammoType) != ammo) return "different_ammo_loaded";
        return prefab.m_allowedAmmo.Exists(entry => entry.m_ammo != null && entry.m_ammo.name == ammo)
            ? "due" : "unsupported_ammo_type";
    }

    internal static void Fill(ZDO z, string ammo, int batch, double interval)
    {
        long now = ZNet.instance.GetTime().Ticks;
        double delay = System.Math.Ceiling(interval * System.TimeSpan.TicksPerSecond);
        long next = delay >= long.MaxValue - now ? long.MaxValue : now + (long)delay;
        if (z.GetInt(ZDOVars.s_ammo) == 0) z.Set(ZDOVars.s_ammoType, ammo);
        z.Set(ZDOVars.s_ammo, batch);
        z.Set(NextRefill, next);
    }

    internal static void ApplyFields(ZDO z, HeimDefenderSettings settings)
    {
        bool prior = z.GetInt(Backup, out int original);
        if (settings.Enabled.Value)
        {
            if (!prior)
            {
                for (int i = 0; i < Fields.Length; i++)
                    if (z.GetBool(Fields[i], out bool value))
                        original |= (value ? 2 : 1) << (2 * i);
                z.Set(Backup, original);
            }

            z.Set("HasFields", true);
            z.Set("HasFieldsTurret", true);
            foreach (int field in Fields)
                z.Set(field, false);
        }
        else if (prior)
        {
            for (int i = 0; i < Fields.Length; i++)
            {
                if (!z.GetBool(Fields[i], out bool current) || current)
                    continue;
                int state = (original >> (2 * i)) & 3;
                if (state == 0)
                    z.RemoveBool(Fields[i]);
                else
                    z.Set(Fields[i], state == 2);
            }

            z.RemoveInt(Backup);
        }

        if (z.GetBool(Free))
        {
            if (z.GetInt(Maximum, -1) == 0)
                z.RemoveInt(Maximum);
            if (z.GetString(DefaultAmmo) == Wood)
                z.RemoveString(DefaultAmmo);
            z.RemoveBool(Free);
        }

        // Native Remove* does not advance the revision. Publish the complete transaction once.
        z.DataRevision++;
        ZDOMan.instance.SetDirtySector(z);
        var view = ZNetScene.instance.FindInstance(z);
        if (view != null && view.IsValid() && view.GetComponent<Turret>() is Turret live && PrefabRegistry.Current.TryGet(Prefab, out var info) && PrefabRegistry.Component<Turret>(info.Prefab) is Turret defaults)
        {
            live.m_targetPlayers = defaults.m_targetPlayers;
            live.m_targetTamed = defaults.m_targetTamed;
            live.m_targetTamedConfig = defaults.m_targetTamedConfig;
            live.m_maxAmmo = defaults.m_maxAmmo;
            live.m_defaultAmmo = defaults.m_defaultAmmo;
            view.LoadFields();
        }
    }
}
