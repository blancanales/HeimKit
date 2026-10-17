using System;

namespace HeimKit.Features.SharedMap;

// Native Minimap pixel geometry, without a graphical minimap or a client map upload.
public sealed class SharedMapExploration
{
    public int Size { get; }
    public float PixelSize { get; }
    public float Radius { get; }
    public float Interval { get; }
    public int ExploredPixels { get; private set; }
    internal byte[] Pixels { get; }

    public SharedMapExploration(int size, float pixelSize, float radius, float interval)
    {
        if (size < 16 || size > 4096 || (size & (size - 1)) != 0 ||
            !Finite(pixelSize) || pixelSize < 1 || pixelSize > 128 ||
            !Finite(radius) || radius <= 0 || radius > 1000 ||
            !Finite(interval) || interval < .1f || interval > 60)
            throw new ArgumentOutOfRangeException(nameof(size), "Unsupported native minimap geometry.");
        Size = size; PixelSize = pixelSize; Radius = radius; Interval = interval;
        Pixels = new byte[size * size];
    }

    public bool Matches(int size, float pixelSize, float radius, float interval) =>
        Size == size && PixelSize == pixelSize && Radius == radius && Interval == interval;

    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private bool TryPixel(float x, float z, out int px, out int py)
    {
        px = py = 0;
        if (!Finite(x) || !Finite(z)) return false;
        float limit = Size / 2 * PixelSize;
        if (x < -limit || x >= limit || z < -limit || z >= limit) return false;
        px = Utils.RoundToInt(x / PixelSize + Size / 2);
        py = Utils.RoundToInt(z / PixelSize + Size / 2);
        return px >= 0 && px < Size && py >= 0 && py < Size;
    }

    public bool IsExplored(float x, float z) => TryPixel(x, z, out int px, out int py) && Pixels[py * Size + px] != 0;
    public bool IsExploredPixel(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size && Pixels[y * Size + x] != 0;

    internal int Import(byte[] source, int offset)
    {
        if (offset < 0 || source.Length - offset < Pixels.Length) throw new ArgumentException("Invalid terrain array.");
        int added = 0;
        for (int i = 0; i < Pixels.Length; i++)
            if (source[offset + i] != 0 && Pixels[i] == 0) { Pixels[i] = 1; added++; }
        ExploredPixels += added;
        return added;
    }

    public byte[] ExportPacked()
    {
        var packed = new byte[(Pixels.Length + 7) / 8];
        for (int i = 0; i < Pixels.Length; i++) if (Pixels[i] != 0) packed[i / 8] |= (byte)(1 << (i % 8));
        return packed;
    }

    public void ImportPacked(byte[] packed)
    {
        if (packed.Length != (Pixels.Length + 7) / 8) throw new ArgumentException("Invalid packed terrain length.");
        int added = 0;
        for (int i = 0; i < Pixels.Length; i++)
            if ((packed[i / 8] & (1 << (i % 8))) != 0 && Pixels[i] == 0) { Pixels[i] = 1; added++; }
        ExploredPixels += added;
    }

    public int Reveal(float x, float z)
    {
        if (!TryPixel(x, z, out int px, out int py)) return 0;
        int radius = (int)Math.Ceiling(Radius / PixelSize);
        int added = 0;
        for (int y = Math.Max(0, py - radius); y <= Math.Min(Size - 1, py + radius); y++)
        for (int column = Math.Max(0, px - radius); column <= Math.Min(Size - 1, px + radius); column++)
        {
            int dx = column - px, dy = y - py;
            if (dx * dx + dy * dy > radius * radius) continue;
            int index = y * Size + column;
            if (Pixels[index] != 0) continue;
            Pixels[index] = 1; added++;
        }
        ExploredPixels += added;
        return added;
    }
}
