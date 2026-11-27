using System;
using System.IO;
using System.Text;
using HeimKit.Core.Control.Models;

namespace HeimKit.Core.Control;

internal static class ControlProtocol
{
    internal const string DiagnosticsGroup = "Server Diagnostics";
    internal const int Version = 2, MaxBytes = 16 * 1024, MaxEntries = 256;
    internal const string Disabled = "HeimKit_Disabled";
    internal const string Identify = "HeimKit_Identify";
    internal const string Hello = "HeimKit_Hello", Set = "HeimKit_Set", State = "HeimKit_State";
    internal static readonly System.Collections.Generic.IReadOnlyList<string> ModuleNames = Array.AsReadOnly(new string[]
    {
        "InfiniteTorches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch", "SharedMap",
        "DoorKeeper", "ForestRenewal", "SeaSalvage", "TameTravel", "HeimMender", "StationSupply", "HeimDefender"
    });
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static ZPackage WriteHello(string version) => Write(w =>
    {
        w.Write(Version);
        WriteString(w, version, 32);
    });

    internal static ZPackage WriteSet(int id, string module, string key, bool value) => Write(w =>
    {
        w.Write(id);
        WriteString(w, module, 64);
        WriteString(w, key, 64);
        w.Write(value);
    });

    internal static ZPackage WriteNumericSet(int id, string module, string key, double value) => Write(w =>
    {
        w.Write(id);
        WriteString(w, module, 64);
        WriteString(w, key, 64);
        w.Write(value);
    });

    internal static bool TryReadNumericSet(ZPackage package, out (int Id, string Module, string Key, double Value) request) =>
        TryRead(package, r =>
        {
            int id = r.ReadInt32();
            if (id <= 0) throw new InvalidDataException("Invalid request ID.");
            return (id, ReadString(r, 64), ReadString(r, 64), r.ReadDouble());
        }, out request);

    internal static ZPackage WriteState(ControlSnapshot state, bool versionOnly = false) => Write(w =>
    {
        w.Write(state.Protocol);
        WriteString(w, state.ServerVersion, 32);
        if (versionOnly) return;
        w.Write(state.Revision);
        w.Write(state.IsAdmin);
        w.Write(state.AdminChangesAllowed);
        w.Write(state.RequestId);
        w.Write((byte)state.Result);
        if (state.Switches.Length + state.Numbers.Length > MaxEntries) throw new InvalidDataException("Too many switches.");
        w.Write(state.Switches.Length);
        foreach (var entry in state.Switches)
        {
            var definition = entry.Definition;
            WriteString(w, definition.Module, 64);
            WriteString(w, definition.Key, 64);
            WriteString(w, definition.Label, 64);
            WriteString(w, definition.Description, 200);
            w.Write(definition.IsModuleSwitch);
            w.Write(entry.Value);
            w.Write((byte)(entry.PendingValue.HasValue ? entry.PendingValue.Value ? 2 : 1 : 0));
            w.Write((byte)entry.Status);
            w.Write((byte)entry.Reason);
        }
        w.Write(state.Numbers.Length);
        foreach (var number in state.Numbers)
        {
            WriteString(w, number.Module, 64);
            WriteString(w, number.Key, 64);
            WriteString(w, number.Label, 64);
            WriteString(w, number.Description, 200);
            WriteString(w, number.Applies, 100);
            w.Write(number.Whole);
            w.Write(number.Minimum);
            w.Write(number.Maximum);
            w.Write(number.Value);
        }
    });

    internal static bool TryReadRoutedPayload(ZPackage packet, int offset, out ZPackage payload)
    {
        payload = null!;
        int remaining = packet.Size() - offset;
        if (offset < 0 || remaining < sizeof(int) * 2 || remaining > MaxBytes + sizeof(int) * 2) return false;
        packet.SetPos(offset);
        int outerLength = packet.ReadInt();
        int innerLength = packet.ReadInt();
        if (outerLength != remaining - sizeof(int) || innerLength != remaining - sizeof(int) * 2) return false;
        payload = new ZPackage(packet.ReadByteArray(innerLength));
        return true;
    }

    internal static bool TryReadHello(ZPackage package, out (int Protocol, string Version) hello) =>
        TryRead(package, r => (r.ReadInt32(), ReadString(r, 32)), out hello);

    internal static bool TryReadSet(ZPackage package, out (int Id, string Module, string Key, bool Value) request) =>
        TryRead(package, r =>
        {
            int id = r.ReadInt32();
            if (id <= 0) throw new InvalidDataException("Invalid request ID.");
            return (id, ReadString(r, 64), ReadString(r, 64), ReadBool(r));
        }, out request);

    internal static bool TryReadState(ZPackage package, out ControlSnapshot state) =>
        TryRead(package, ReadState, out state);

    private static ControlSnapshot ReadState(BinaryReader r)
    {
        var state = new ControlSnapshot { Protocol = r.ReadInt32(), ServerVersion = ReadString(r, 32) };
        if (state.Protocol != Version) return state;
        state.Revision = r.ReadInt64();
        state.IsAdmin = ReadBool(r);
        state.AdminChangesAllowed = ReadBool(r);
        state.RequestId = r.ReadInt32();
        state.Result = (SetResult)ReadCode(r, (byte)SetResult.InvalidValue);
        int count = r.ReadInt32();
        if (count < 0 || count > MaxEntries || state.Revision < 0 || state.RequestId < 0)
            throw new InvalidDataException("Invalid snapshot header.");
        state.Switches = new SwitchState[count];
        for (int i = 0; i < count; i++)
        {
            var definition = new SwitchDefinition(ReadString(r, 64), ReadString(r, 64),
                ReadString(r, 64), ReadString(r, 200), ReadBool(r));
            bool value = ReadBool(r);
            byte pending = ReadCode(r, 2);
            var status = (SwitchStatus)ReadCode(r, (byte)SwitchStatus.Unavailable);
            var reason = (UnavailableReason)ReadCode(r, (byte)UnavailableReason.FailureProtection);
            state.Switches[i] = new SwitchState(definition, value, pending == 0 ? (bool?)null : pending == 2, status, reason);
        }
        int numericCount = r.ReadInt32();
        if (numericCount < 0 || numericCount > MaxEntries - count)
            throw new InvalidDataException("Invalid numeric setting count.");
        state.Numbers = new NumericSettingState[numericCount];
        for (int i = 0; i < numericCount; i++)
        {
            var number = new NumericSettingState(ReadString(r, 64), ReadString(r, 64),
                ReadString(r, 64), ReadString(r, 200), ReadString(r, 100), ReadBool(r),
                r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
            if (double.IsNaN(number.Minimum) || double.IsInfinity(number.Minimum) ||
                double.IsNaN(number.Maximum) || double.IsInfinity(number.Maximum) || !number.Accepts(number.Value))
                throw new InvalidDataException("Invalid numeric setting bounds or value.");
            state.Numbers[i] = number;
        }
        return state;
    }

    private static ZPackage Write(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        write(writer);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Control package too large.");
        return new ZPackage(stream.ToArray());
    }

    private static bool TryRead<T>(ZPackage package, Func<BinaryReader, T> read, out T value)
    {
        value = default!;
        if (package == null || package.Size() > MaxBytes) return false;
        try
        {
            using var stream = new MemoryStream(package.GetArray(), false);
            using var reader = new BinaryReader(stream, Utf8);
            T decoded = read(reader);
            if (stream.Position != stream.Length) return false;
            value = decoded;
            return true;
        }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException)
        {
            return false;
        }
    }

    private static void WriteString(BinaryWriter writer, string value, int limit)
    {
        if (value.Length > limit) throw new InvalidDataException("Control string too long.");
        byte[] bytes = Utf8.GetBytes(value);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, int limit)
    {
        int size = reader.ReadUInt16();
        if (size > limit * 4 || size > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid control string length.");
        string value = Utf8.GetString(reader.ReadBytes(size));
        if (value.Length > limit) throw new InvalidDataException("Control string too long.");
        return value;
    }

    private static bool ReadBool(BinaryReader reader) => ReadCode(reader, 1) != 0;

    private static byte ReadCode(BinaryReader reader, byte maximum)
    {
        byte value = reader.ReadByte();
        if (value > maximum) throw new InvalidDataException("Invalid control value.");
        return value;
    }
}
