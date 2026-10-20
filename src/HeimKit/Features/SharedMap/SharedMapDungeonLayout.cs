using System;
using System.IO;
using HeimKit.Features.SharedMap.Models;
using UnityEngine;

namespace HeimKit.Features.SharedMap;

// DungeonGenerator uses Utils.Write(Quaternion), which stores three Euler floats.
public static class SharedMapDungeonLayout
{
    public const int RoomBytes = 28; // hash + XYZ position + XYZ Euler rotation
    public const int MaximumRooms = 1024;
    public static bool TryRead(byte[]? data, out DungeonRoomPlacement[] rooms, out string reason)
    {
        rooms = Array.Empty<DungeonRoomPlacement>(); reason = "layout_not_saved";
        if (data == null || data.Length < 4) return false;
        reason = "invalid_layout";
        if (data.Length > 4 + MaximumRooms * RoomBytes) return false;
        using var reader = new BinaryReader(new MemoryStream(data, false));
        int count = reader.ReadInt32();
        if (count < 1 || count > MaximumRooms || data.Length != 4 + count * RoomBytes) return false;
        var parsed = new DungeonRoomPlacement[count];
        for (int i = 0; i < count; i++)
        {
            int hash = reader.ReadInt32();
            Vector3 position = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 euler = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!Finite(position) || !Finite(euler)) { reason = "invalid_room_transform"; return false; }
            parsed[i] = new DungeonRoomPlacement(hash, position, euler);
        }
        rooms = parsed; reason = "saved_room_layout"; return true;
    }
    private static bool Finite(Vector3 point) => SharedMapExploration.Finite(point.x) && SharedMapExploration.Finite(point.y) && SharedMapExploration.Finite(point.z);
}
