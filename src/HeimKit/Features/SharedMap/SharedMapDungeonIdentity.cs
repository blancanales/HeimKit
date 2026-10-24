using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HeimKit.Features.SharedMap;

public static class SharedMapDungeonIdentity
{
    // Root coordinates and hierarchy indices survive native ZDO-ID reassignment. No proximity merge.
    public static string Create(string prefab, float x, float y, float z, string gateway)
    {
        if (prefab.Length == 0 || prefab.Length > 128 || gateway.Length == 0 || gateway.Length > 512 ||
            !SharedMapExploration.Finite(x) || !SharedMapExploration.Finite(y) || !SharedMapExploration.Finite(z))
            throw new ArgumentException("Invalid dungeon source identity.");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(prefab); writer.Write(x == 0 ? 0f : x); writer.Write(y == 0 ? 0f : y);
            writer.Write(z == 0 ? 0f : z); writer.Write(gateway);
        }
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
    }
}
