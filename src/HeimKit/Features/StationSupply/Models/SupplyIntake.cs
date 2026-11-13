using UnityEngine;
namespace HeimKit.Features.StationSupply.Models;
internal sealed class SupplyIntake
{
    internal SupplyKind Kind;
    internal Vector3 LocalPoint;
    internal int Capacity;
    internal int InventoryWidth, InventoryHeight;
    internal float QuietSeconds;
    internal int[] MaximumStacks = System.Array.Empty<int>();
    internal float DefaultFuel;
    internal int[] Items = System.Array.Empty<int>();
    internal string[] ItemNames = System.Array.Empty<string>();
}
