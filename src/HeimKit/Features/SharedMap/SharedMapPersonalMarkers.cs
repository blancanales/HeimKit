using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HeimKit.Features.SharedMap.Models;
using UnityEngine;

namespace HeimKit.Features.SharedMap;
internal sealed class SharedMapPersonalMarker
{
    internal readonly string Id;
    internal readonly MapPin Pin;
    internal bool TableOnly, Adopted, PendingLocal;
    internal Vector3 Position => new(Pin.X, Pin.Y, Pin.Z);

    internal SharedMapPersonalMarker(string id, MapPin pin)
    {
        Id = id;
        Pin = pin;
    }
}

internal static class SharedMapPersonalMarkers
{
    internal static bool SameAliases(Dictionary<string, string> previous, Dictionary<string, string> next)
    {
        if (previous.Count != next.Count) return false;
        foreach (var pair in next)
            if (!previous.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
        return true;
    }

    internal static int Category(string setting) => setting switch
    {
        "DungeonMarkerSync" => 1 << 0,
        "SpawnerMarkerSync" => 1 << 1,
        "PortalMarkerSync" => 1 << 2,
        "ResourceMarkerSync" => 1 << 3,
        "RunestoneMarkerSync" => 1 << 4,
        "DvergrGuardTowerMarkerSync" => 1 << 5,
        "DvergrLighthouseMarkerSync" => 1 << 6,
        "DvergrExcavationMarkerSync" => 1 << 7,
        "FulingVillageMarkerSync" => 1 << 8,
        "OtherStructureMarkerSync" => 1 << 9,
        _ => 0
    };

    internal static int Category(MapPin pin)
    {
        string setting = pin.Owner switch
        {
            SharedMapTableData.DungeonOwner => "DungeonMarkerSync",
            SharedMapTableData.SpawnerOwner => "SpawnerMarkerSync",
            SharedMapTableData.PortalOwner or SharedMapTableData.UnconnectedPortalOwner => "PortalMarkerSync",
            SharedMapTableData.ResourceOwner => "ResourceMarkerSync",
            SharedMapTableData.RunestoneOwner => "RunestoneMarkerSync",
            SharedMapTableData.StructureOwner => pin.Name switch
            {
                "Dvergr Guard Tower" => "DvergrGuardTowerMarkerSync",
                "Dvergr Lighthouse" => "DvergrLighthouseMarkerSync",
                "Dvergr Excavation" => "DvergrExcavationMarkerSync",
                SharedMapLandmarkPrefab.FulingVillage => "FulingVillageMarkerSync",
                _ => "OtherStructureMarkerSync"
            },
            _ => string.Empty
        };
        return Category(setting);
    }

    internal const string Query = "HeimKit_MapNearby1", Reply = "HeimKit_MapMarkers1";
    internal const string PolicyQuery = "HeimKit_MapPolicyQuery2", PolicyReply = "HeimKit_MapPolicy2";
    // At most 8192 removal fingerprints, 32768 issued source keys and 65536 retired spawners.
    internal const int PolicyBytes = 20 + 32 * (8192 + 32768 + 65536 + 4096 + 65536) + 64 * (4096 + 65536);
    internal const int Maximum = 8192, Bytes = 3 * 1024 * 1024;
    internal static bool Nearby(Vector3 a, Vector3 b, float radius)
    {
        float x = a.x - b.x, z = a.z - b.z;
        return x * x + z * z <= radius * radius;
    }

    internal static void Write(BinaryWriter w, SharedMapPersonalMarker m)
    {
        var p = m.Pin;
        w.Write(m.Id);
        w.Write(p.Owner);
        w.Write(p.Name);
        w.Write(p.X);
        w.Write(p.Y);
        w.Write(p.Z);
        w.Write(p.Icon);
        w.Write(p.Checked);
        w.Write(m.TableOnly);
        w.Write(m.Adopted);
    }

    internal static SharedMapPersonalMarker Read(BinaryReader r)
    {
        string id = r.ReadString();
        long owner = r.ReadInt64();
        string label = r.ReadString();
        float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
        int icon = r.ReadInt32();
        byte check = r.ReadByte();
        if (!SharedMapPinRemovals.ValidId(id) || label.Length > 4096 || !SharedMapTableData.ManagedOwner(owner) || !SharedMapDungeonPrefab.Finite(new Vector3(x, y, z)) || (icon != (int)Minimap.PinType.Icon1 && icon != (int)Minimap.PinType.Icon2 && icon != (int)Minimap.PinType.Icon3 && icon != (int)Minimap.PinType.Icon4) || check > 1)
            throw new InvalidDataException("Invalid managed pin.");
        return new(id, new MapPin(owner, label, x, y, z, icon, check == 1))
        {
            TableOnly = r.ReadBoolean(),
            Adopted = r.ReadBoolean()
        };
    }

    internal static byte[] Encode(long world, long character, int request, Vector3 centre, float radius, List<SharedMapPersonalMarker> markers)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write(request == 0 ? 2 : 1);
        w.Write(world);
        w.Write(character);
        w.Write(request);
        w.Write(centre.x);
        w.Write(centre.y);
        w.Write(centre.z);
        w.Write(radius);
        w.Write(markers.Count);
        foreach (var m in markers)
        {
            Write(w, m);
            if (request == 0) w.Write(m.PendingLocal);
            if (stream.Length > Bytes)
                throw new InvalidDataException("Marker packet limit exceeded.");
        }

        return stream.ToArray();
    }

    internal static List<SharedMapPersonalMarker> Decode(byte[] data, long world, long character, int request, out Vector3 centre, out float radius)
    {
        if (data.Length > Bytes)
            throw new InvalidDataException("Marker data limit exceeded.");
        using var r = new BinaryReader(new MemoryStream(data, false), new UTF8Encoding(false, true));
        int version = r.ReadInt32();
        if ((version != 1 && !(request == 0 && version == 2)) || r.ReadInt64() != world || r.ReadInt64() != character || r.ReadInt32() != request)
            throw new InvalidDataException("Marker session mismatch.");
        centre = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        radius = r.ReadSingle();
        int count = r.ReadInt32();
        if (!SharedMapDungeonPrefab.Finite(centre) || !SharedMapExploration.Finite(radius) || radius < 0 || radius > 1024 || count < 0 || count > Maximum)
            throw new InvalidDataException("Invalid marker scope.");
        var result = new List<SharedMapPersonalMarker>(count);
        var seen = new HashSet<string>();
        for (int i = 0; i < count; i++)
        {
            var m = Read(r);
            if (version == 2) m.PendingLocal = r.ReadBoolean();
            if (request != 0 && (m.TableOnly || m.Adopted || !Nearby(centre,m.Position,radius)))
                throw new InvalidDataException("Marker outside the delivered discovery scope.");
            if (!seen.Add(m.Id))
                throw new InvalidDataException("Duplicate marker ID.");
            result.Add(m);
        }

        if (r.BaseStream.Position != data.Length)
            throw new InvalidDataException("Trailing marker data.");
        return result;
    }
}
