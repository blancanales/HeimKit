using System;
using System.Collections.Generic;
using HeimKit.Core.Native;
using HeimKit.Core.Prefabs;
using HeimKit.Features.StationSupply.Models;
using UnityEngine;
namespace HeimKit.Features.StationSupply;
internal sealed class StationSupplyPrefabs
{
    internal readonly PrefabCatalog<SupplyStation> Stations = PrefabRegistry.Current.Catalog<SupplyStation>("StationSupply.stations");
    internal readonly PrefabCatalog<Container> Chests = PrefabRegistry.Current.Catalog<Container>("StationSupply.chests");
    internal readonly PrefabCatalog<float> Wards = PrefabRegistry.Current.Catalog<float>("StationSupply.wards");
    internal StationSupplyPrefabs(StationSupplySettings settings)
    {
        Smelter("smelter", settings.Smelters.Value);
        Smelter("charcoal_kiln", settings.CharcoalKilns.Value);
        Smelter("blastfurnace", settings.BlastFurnaces.Value);
        Smelter("windmill", settings.Windmills.Value);
        Smelter("piece_spinningwheel", settings.SpinningWheels.Value);
        Smelter("eitrrefinery", settings.EitrRefineries.Value);
        Smelter("piece_FrostKiln", settings.FrigidKilns.Value);
        if (settings.ShieldGenerators.Value)
        {
            var prefab = Persistent("piece_shieldgenerator");
            var shield = PrefabRegistry.Component<ShieldGenerator>(prefab);
            if (prefab == null || shield == null || shield.m_addFuelSwitch == null || shield.m_maxFuel < 1 || shield.m_fuelItems.Count == 0) throw new InvalidOperationException("Native shield generator intake is unavailable.");
            var port = Intake(prefab, shield.m_addFuelSwitch.transform, SupplyKind.Fuel, shield.m_maxFuel, shield.m_fuelItems);
            port.DefaultFuel = shield.m_defaultFuel;
            Stations.Add(prefab.name.GetStableHashCode(), new SupplyStation { Name = prefab.name, Intakes = new[] { port } });
        }
        if (settings.Fermenters.Value)
        {
            var prefab = Persistent("fermenter"); var fermenter = PrefabRegistry.Component<Fermenter>(prefab);
            if (prefab == null || fermenter == null || fermenter.m_addSwitch == null) throw new InvalidOperationException("Native fermenter intake is unavailable.");
            var items = new List<ItemDrop>(); foreach (var conversion in fermenter.m_conversion) if (conversion.m_from != null) items.Add(conversion.m_from);
            Stations.Add(prefab.name.GetStableHashCode(), new SupplyStation { Name = prefab.name, Intakes = new[] { Intake(prefab, fermenter.m_addSwitch.transform, SupplyKind.Fermenter, 1, items) } });
        }
        if (settings.Obliterators.Value)
        {
            var prefab = Persistent("incinerator"); var incinerator = PrefabRegistry.Component<Incinerator>(prefab);
            if (prefab == null || incinerator == null || incinerator.m_container == null || incinerator.m_incinerateSwitch == null ||
                incinerator.m_container.m_privacy != Container.PrivacySetting.Public || incinerator.m_container.m_rootObjectOverride != PrefabRegistry.Component<ZNetView>(prefab))
                throw new InvalidOperationException("Native Obliterator container/root contract is unavailable.");
            var container = incinerator.m_container;
            if (container.m_width < 1 || container.m_height < 1 || container.m_width * container.m_height > 512)
                throw new InvalidOperationException("Unsupported native Obliterator inventory dimensions.");
            if (!HeimKit.Core.World.PlayerWorldScope.Finite(incinerator.m_effectDelayMax) || incinerator.m_effectDelayMax < 0 || incinerator.m_effectDelayMax > 60)
                throw new InvalidOperationException("Unsupported native Obliterator activation delay.");
            var port = new SupplyIntake { Kind = SupplyKind.Obliterator,
                LocalPoint = prefab.transform.InverseTransformPoint(container.transform.position),
                Capacity = container.m_width * container.m_height, InventoryWidth = container.m_width, InventoryHeight = container.m_height,
                QuietSeconds = Math.Max(10, incinerator.m_effectDelayMax + 4.5f) };
            Stations.Add(prefab.name.GetStableHashCode(), new SupplyStation { Name = prefab.name, Intakes = new[] { port } });
        }
        foreach (string name in new[] { "piece_chest_wood", "piece_chest", "piece_chest_blackmetal", "piece_chest_grausten", "piece_chest_barrel" })
        {
            var prefab = Persistent(name); var chest = PrefabRegistry.Component<Container>(prefab);
            if (prefab != null && chest != null && chest.m_privacy == Container.PrivacySetting.Public && chest.m_rootObjectOverride == null && chest.m_wagon == null && !chest.m_autoDestroyEmpty && chest.m_width > 0 && chest.m_height > 0)
                Chests.Add(name.GetStableHashCode(), chest);
        }
        if (Chests.Count == 0) throw new InvalidOperationException("No supported native public chests are available.");
        foreach (string name in new[] { "guard_stone", "dverger_guardstone", "guard_stone_test" })
        {
            var wardPrefab = PrefabRegistry.Current.Prefab(name); var ward = PrefabRegistry.Component<PrivateArea>(wardPrefab);
            if (ward == null) { if (name == "guard_stone") throw new InvalidOperationException("Native ward metadata is unavailable."); continue; }
            if (float.IsNaN(ward.m_radius) || ward.m_radius <= 0 || ward.m_radius > 64) throw new InvalidOperationException("Unsupported native ward coverage: " + name);
            Wards.Add(name.GetStableHashCode(), ward.m_radius);
        }
    }
    internal bool Accept(ZDO z) => Stations.ContainsKey(z.GetPrefab()) || Chests.ContainsKey(z.GetPrefab()) || Wards.ContainsKey(z.GetPrefab());
    private static GameObject? Persistent(string name)
    {
        var prefab = PrefabRegistry.Current.Prefab(name); var view = PrefabRegistry.Component<ZNetView>(prefab);
        return prefab != null && view != null && view.m_persistent && PrefabRegistry.Component<Piece>(prefab) != null ? prefab : null;
    }
    private void Smelter(string name, bool enabled)
    {
        if (!enabled) return;
        var prefab = Persistent(name); var smelter = PrefabRegistry.Component<Smelter>(prefab);
        if (prefab == null || smelter == null) throw new InvalidOperationException("Native station definition is unavailable: " + name);
        var ports = new List<SupplyIntake>();
        if (smelter.m_maxOre > 0)
        {
            if (smelter.m_addOreSwitch == null) throw new InvalidOperationException("Native material switch is unavailable: " + name);
            var items = new List<ItemDrop>(); foreach (var conversion in smelter.m_conversion) if (conversion.m_from != null) items.Add(conversion.m_from);
            ports.Add(Intake(prefab, smelter.m_addOreSwitch.transform, SupplyKind.Material, smelter.m_maxOre, items));
        }
        if (smelter.m_maxFuel > 0)
        {
            if (smelter.m_addWoodSwitch == null || smelter.m_fuelItem == null) throw new InvalidOperationException("Native fuel switch is unavailable: " + name);
            ports.Add(Intake(prefab, smelter.m_addWoodSwitch.transform, SupplyKind.Fuel, smelter.m_maxFuel, new[] { smelter.m_fuelItem }));
        }
        if (ports.Count == 0) throw new InvalidOperationException("Native station has no supported intakes: " + name);
        Stations.Add(name.GetStableHashCode(), new SupplyStation { Name = name, Intakes = ports.ToArray() });
    }
    private static SupplyIntake Intake(GameObject prefab, Transform point, SupplyKind kind, int capacity, IReadOnlyList<ItemDrop> items)
    {
        if (items.Count == 0 || items.Count > 128) throw new InvalidOperationException("Unsupported native intake conversion list: " + prefab.name);
        var result = new SupplyIntake { Kind = kind, Capacity = capacity, LocalPoint = prefab.transform.InverseTransformPoint(point.position), Items = new int[items.Count], ItemNames = new string[items.Count], MaximumStacks = new int[items.Count] };
        for (int i = 0; i < items.Count; i++) { result.ItemNames[i] = items[i].gameObject.name; result.Items[i] = result.ItemNames[i].GetStableHashCode(); result.MaximumStacks[i] = items[i].m_itemData.m_shared.m_maxStackSize; }
        return result;
    }
}
