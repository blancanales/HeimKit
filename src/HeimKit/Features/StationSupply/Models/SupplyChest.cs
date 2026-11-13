using UnityEngine;
namespace HeimKit.Features.StationSupply.Models;

// One current-scope selection value, rebuilt before discovery/final revalidation.
internal readonly struct SupplyChest
{
    internal readonly ZDO Record;
    internal readonly Vector3 Position;
    internal readonly string? Rejection;
    internal SupplyChest(ZDO record, string? rejection)
    { Record = record; Position = record.GetPosition(); Rejection = rejection; }
}
