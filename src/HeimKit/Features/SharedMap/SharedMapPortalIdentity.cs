using System;
using System.Globalization;

namespace HeimKit.Features.SharedMap;

// Native ZDO IDs are reassigned by world loading. Fixed portal locations survive that reassignment.
public static class SharedMapPortalIdentity
{
    public static string Create(int prefab, float x, float y, float z)
    {
        if (!SharedMapExploration.Finite(x) || !SharedMapExploration.Finite(y) || !SharedMapExploration.Finite(z))
            throw new ArgumentException("A portal identity requires finite coordinates.");
        return "portal:" + prefab.ToString("X8", CultureInfo.InvariantCulture) + ":" + Coordinate(x) + ":" + Coordinate(y) + ":" + Coordinate(z);
    }
    private static string Coordinate(float value) => BitConverter.SingleToInt32Bits(value == 0 ? 0f : value).ToString("X8", CultureInfo.InvariantCulture);
}
