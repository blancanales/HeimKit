using System.Reflection;
using HarmonyLib;
using HeimKit.Features.SharedMap;
using UnityEngine;

internal static class SharedMapDungeonLayoutTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static byte[] Layout(int rooms, bool invalidLast = false)
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true))
        {
            writer.Write(rooms);
            for (int i = 0; i < rooms; i++)
            {
                writer.Write(1000 + i);
                // Use the installed native vector writer for both vectors. Native quaternion
                // writing extracts Euler angles before passing them to this same overload.
                Utils.Write(writer, new Vector3(i + .25f, 5000 + i, -i - .75f));
                Utils.Write(writer, new Vector3(15 + i, 90 + i, invalidLast && i == rooms - 1 ? float.NaN : 270 - i));
            }
        }
        return data.ToArray();
    }
    public static void Run()
    {
        foreach (int count in new[] { 1, 12, 32, 40, 1024 })
        {
            byte[] bytes = Layout(count);
            Check(bytes.Length == 4 + 28 * count && SharedMapDungeonLayout.RoomBytes == 28, "Wrong native room stride.");
            Check(SharedMapDungeonLayout.TryRead(bytes, out var rooms, out string reason) && rooms.Length == count && reason == "saved_room_layout", "Valid native dungeon layout rejected.");
            var last = rooms[^1]; int n = count - 1;
            Check(last.Hash == 1000 + n && last.Position.x == n + .25f && last.Position.y == 5000 + n && last.Position.z == -n - .75f &&
                last.RotationEuler.x == 15 + n && last.RotationEuler.y == 90 + n && last.RotationEuler.z == 270 - n,
                "Multi-room decoding lost alignment or converted Euler values to quaternion components.");
        }
        byte[] valid = Layout(2);
        foreach (byte[] invalid in new[] { Array.Empty<byte>(), new byte[] { 1, 2, 3 }, BitConverter.GetBytes(-1), BitConverter.GetBytes(int.MaxValue),
            Layout(0), Layout(1025), valid[..^1], valid.Concat(new byte[4]).ToArray(), Layout(2, true) })
            Check(!SharedMapDungeonLayout.TryRead(invalid, out var rooms, out _) && rooms.Length == 0, "Malformed layout produced partial dungeon evidence.");
        Check(!SharedMapDungeonLayout.TryRead(null, out _, out string absent) && absent == "layout_not_saved", "Missing layout was not distinguished from invalid data.");
        System.Console.WriteLine("PASS: audited native-format fixtures, 28-byte room records, 12/32/40-room layouts, bounds, truncation, trailing bytes and fail-closed transforms.");
    }
}
