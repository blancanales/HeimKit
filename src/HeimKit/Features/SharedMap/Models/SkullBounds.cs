namespace HeimKit.Features.SharedMap.Models;

public readonly struct SkullBounds
{
    public readonly float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

    internal SkullBounds(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        MinX = minX; MinY = minY; MinZ = minZ;
        MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
    }

    private static bool ValidAxis(float min, float max) =>
        min >= -100128 && max <= 100128 && max > min && max - min <= 128;

    internal bool Valid => ValidAxis(MinX, MaxX) && ValidAxis(MinY, MaxY) && ValidAxis(MinZ, MaxZ);

    internal bool Contains(ResourceDeposit tissue) =>
        tissue.X >= MinX && tissue.X <= MaxX && tissue.Y >= MinY && tissue.Y <= MaxY &&
        tissue.Z >= MinZ && tissue.Z <= MaxZ;
}
