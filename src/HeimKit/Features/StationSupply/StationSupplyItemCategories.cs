using HeimKit.Core.Prefabs;
using HeimKit.Features.StationSupply.Models;
namespace HeimKit.Features.StationSupply;
internal sealed class StationSupplyItemCategories
{
    private readonly PrefabCatalog<bool> _foodInputs = PrefabRegistry.Current.Catalog<bool>("StationSupply.food-inputs");
    internal StationSupplyItemCategories()
    {
        foreach (string name in new[] { "piece_cookingstation", "piece_cookingstation_iron", "piece_oven" })
        {
            var station = PrefabRegistry.Component<CookingStation>(PrefabRegistry.Current.Prefab(name));
            if (station == null) continue;
            foreach (var conversion in station.m_conversion)
                if (conversion.m_from != null && conversion.m_to != null && conversion.m_to.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable)
                    _foodInputs.Add(conversion.m_from.gameObject.name.GetStableHashCode(), true);
        }
        var fermenter = PrefabRegistry.Component<Fermenter>(PrefabRegistry.Current.Prefab("fermenter"));
        if (fermenter != null)
            foreach (var conversion in fermenter.m_conversion)
                if (conversion.m_from != null && conversion.m_to != null && conversion.m_to.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable)
                    _foodInputs.Add(conversion.m_from.gameObject.name.GetStableHashCode(), true);
    }
    internal static ObliteratorCategories Category(ItemDrop.ItemData.ItemType type) => type switch
    {
        ItemDrop.ItemData.ItemType.Material => ObliteratorCategories.Materials,
        ItemDrop.ItemData.ItemType.Trophy => ObliteratorCategories.Trophies,
        ItemDrop.ItemData.ItemType.Consumable or ItemDrop.ItemData.ItemType.Fish => ObliteratorCategories.FoodAndMead,
        ItemDrop.ItemData.ItemType.OneHandedWeapon or ItemDrop.ItemData.ItemType.TwoHandedWeapon or
        ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft or ItemDrop.ItemData.ItemType.Bow => ObliteratorCategories.Weapons,
        ItemDrop.ItemData.ItemType.Shield or ItemDrop.ItemData.ItemType.Helmet or ItemDrop.ItemData.ItemType.Chest or
        ItemDrop.ItemData.ItemType.Legs or ItemDrop.ItemData.ItemType.Hands or ItemDrop.ItemData.ItemType.Shoulder => ObliteratorCategories.Armor,
        ItemDrop.ItemData.ItemType.Tool or ItemDrop.ItemData.ItemType.Torch => ObliteratorCategories.Tools,
        ItemDrop.ItemData.ItemType.Ammo or ItemDrop.ItemData.ItemType.AmmoNonEquipable => ObliteratorCategories.Ammo,
        _ => ObliteratorCategories.Other
    };
    internal static bool Allowed(ItemDrop.ItemData.ItemType type, ObliteratorCategories selected) => (Category(type) & selected) != 0;
    internal static ObliteratorCategories Category(ItemDrop.ItemData.ItemType type, Skills.SkillType skill, bool foodInput)
    {
        if (foodInput && type == ItemDrop.ItemData.ItemType.Material) return ObliteratorCategories.FoodAndMead;
        if (skill == Skills.SkillType.Pickaxes) return ObliteratorCategories.Tools;
        var category = Category(type);
        return skill == Skills.SkillType.Axes ? category | ObliteratorCategories.Tools : category;
    }
    internal bool Allowed(int prefab, ObliteratorCategories selected)
    {
        var definition = PrefabRegistry.Current.Prefab(prefab); var item = PrefabRegistry.Component<ItemDrop>(definition);
        if (definition == null || item == null) return false;
        var shared = item.m_itemData.m_shared;
        return (Category(shared.m_itemType, shared.m_skillType, _foodInputs.ContainsKey(prefab)) & selected) != 0;
    }
}
