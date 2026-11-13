using UnityEngine;
namespace HeimKit.Features.StationSupply.Models;

internal readonly struct SupplyPlacement
{
    internal readonly Vector3 Station, Anchor, Outward;
    internal readonly float RangeSquared, Height;
    internal readonly bool Valid, HasSide;
    internal SupplyPlacement(Vector3 station, Vector3 anchor, float range, float height, bool valid)
    {
        Station = station; Anchor = anchor; RangeSquared = range * range; Height = height; Valid = valid;
        HasSide = anchor.sqrMagnitude > .0625f;
        Outward = HasSide ? anchor.normalized : Vector3.zero;
    }
}
