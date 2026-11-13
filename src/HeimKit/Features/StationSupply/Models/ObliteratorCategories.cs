using System;
namespace HeimKit.Features.StationSupply.Models;
[Flags]
public enum ObliteratorCategories
{
    None = 0,
    Materials = 1,
    Trophies = 2,
    FoodAndMead = 4,
    Weapons = 8,
    Armor = 16,
    Tools = 32,
    Ammo = 64,
    Other = 128,
    All = Materials | Trophies | FoodAndMead | Weapons | Armor | Tools | Ammo | Other
}
