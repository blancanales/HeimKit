using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// Stable source identities and issued publications; never infer management from an icon.
public sealed class SharedMapPinState
{
    private const int Maximum = 32768;
    private const uint Magic = 0x53504B48;
    private static readonly (float X, float Z)[] Slots = { (0, 0), (2, 0), (2, 2), (0, 2), (-2, 2), (-2, 0), (-2, -2), (0, -2), (2, -2) };
    private sealed class State
    {
        public MapPin Source;
        public bool Target;
        public int Slot;
        public State(MapPin source, bool target, int slot = 0) { Source = source; Target = target; Slot = slot; }
        public MapPin Published => Copy(Source, Source.X + Slots[Slot].X, Source.Z + Slots[Slot].Z, Source.Checked);
    }
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _issued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _adopted = new(StringComparer.Ordinal);
    private readonly long _world;
    private readonly Action<string, MapPin, string>? _changed;
    private bool _dirty;
    public long Revision { get; private set; }
    private float _nextSave;
    public string FilePath { get; }
    public SharedMapPinState(string directory, long world, Action<string, MapPin, string>? changed = null)
    { _world = world; _changed = changed; FilePath = Path.Combine(directory, "world-" + world.ToString("X16", CultureInfo.InvariantCulture) + ".pin-state"); }
    private static bool Managed(long owner) => SharedMapTableData.ManagedOwner(owner);
    private static MapPin Copy(MapPin p, float x, float z, bool check) => new(p.Owner, p.Name, x, p.Y, z, p.Icon, check, p.Author);
    public static string MarkerKey(long owner, string identity) => SharedMapPinRemovals.Identity(new MapPin(owner, identity, 0, 0, 0, 0));
    private void Index(string key, State state, bool add)
    {
        foreach (string id in new[] { SharedMapPinRemovals.Identity(state.Source), SharedMapPinRemovals.Identity(state.Published) }.Distinct())
        {
            if (add) { if (!_issued.TryGetValue(id, out var keys)) _issued[id] = keys = new HashSet<string>(); keys.Add(key); }
            else if (_issued.TryGetValue(id, out var keys)) { keys.Remove(key); if (keys.Count == 0) _issued.Remove(id); }
        }
    }
    private string? Resolve(MapPin pin)
    {
        string id = SharedMapPinRemovals.Identity(pin);
        if (_adopted.TryGetValue(id, out var adopted)) return adopted;
        if (_issued.TryGetValue(id, out var keys)) return keys.Count == 1 ? keys.First() : null;
        return Managed(pin.Owner) ? null : id;
    }
    public int Accept(IReadOnlyList<MapPin> incoming, IReadOnlyList<MapPin> previous, bool enabled)
    {
        var incomingIds = new HashSet<string>(incoming.Select(SharedMapPinRemovals.Identity), StringComparer.Ordinal);
        var previousIds = new HashSet<string>(previous.Select(SharedMapPinRemovals.Identity), StringComparer.Ordinal);
        string Shape(MapPin p) => SharedMapPinRemovals.Identity(new MapPin(0, p.Name, p.X, p.Y, p.Z, p.Icon));
        var adoptedCandidates = previous.Where(p => Managed(p.Owner) && !incomingIds.Contains(SharedMapPinRemovals.Identity(p)))
            .GroupBy(Shape).ToDictionary(g => g.Key, g => g.Select(Resolve).Where(k => k != null).Distinct().ToArray());
        // Vanilla first adopts a grey pin (owner becomes the writer) before toggling its X.
        foreach (var pin in incoming.Where(p => !Managed(p.Owner)))
        {
            string fingerprint = SharedMapPinRemovals.Identity(pin);
            if (_adopted.ContainsKey(fingerprint) || previousIds.Contains(fingerprint) ||
                !adoptedCandidates.TryGetValue(Shape(pin), out var matches) || matches.Length != 1) continue;
            if (_adopted.Count >= Maximum) throw new InvalidDataException("Shared adopted-pin limit exceeded.");
            _adopted.Add(fingerprint, matches[0]!); _dirty = true; Revision++;
            _changed?.Invoke(matches[0]!, pin, "accepted_pin_adoption");
        }
        // Ownership adoption must still suppress duplicate managed copies when shared X marks are off.
        if (!enabled) return 0;
        var prior = previous.Select(p => (Key: Resolve(p), Pin: p)).Where(p => p.Key != null)
            .GroupBy(p => p.Key!).ToDictionary(g => g.Key, g => g.Select(p => p.Pin.Checked).Distinct().ToArray());
        int count = 0;
        foreach (var group in incoming.Select(p => (Key: Resolve(p), Pin: p)).Where(p => p.Key != null).GroupBy(p => p.Key!))
        {
            if (group.Select(p => p.Pin.Checked).Distinct().Count() != 1) continue;
            var pin = group.First().Pin;
            if (prior.TryGetValue(group.Key, out var old) && (old.Length != 1 || old[0] == pin.Checked)) continue;
            if (!prior.ContainsKey(group.Key) && !pin.Checked) continue;
            if (!_states.TryGetValue(group.Key, out var state))
            {
                if (Managed(pin.Owner)) continue;
                if (_states.Count >= Maximum) throw new InvalidDataException("Shared pin-state limit exceeded.");
                state = new State(Copy(pin, pin.X, pin.Z, false), false); _states.Add(group.Key, state); Index(group.Key, state, true);
            }
            if (state.Target == pin.Checked) continue;
            state.Target = pin.Checked; _dirty = true; Revision++; count++;
            _changed?.Invoke(group.Key, pin, "accepted_checkmark");
        }
        return count;
    }
    public bool Adopted(MapPin pin) => _adopted.ContainsKey(SharedMapPinRemovals.Identity(pin));
    public MapPin Publish(MapPin pin, string? markerIdentity, bool shareChecks)
    {
        string? key = markerIdentity == null ? Resolve(pin) : MarkerKey(pin.Owner, markerIdentity);
        if (key == null) return Copy(pin, pin.X, pin.Z, false);
        if (!_states.TryGetValue(key, out var state))
        {
            if (markerIdentity == null && !shareChecks) return Copy(pin, pin.X, pin.Z, false);
            if (_states.Count >= Maximum) throw new InvalidDataException("Shared pin-state limit exceeded.");
            state = new State(Copy(pin, pin.X, pin.Z, false), false); _states.Add(key, state); Index(key, state, true); _dirty = true; Revision++;
        }
        // Restore a player's original coordinates when their issued offset returns in an upload.
        MapPin source = markerIdentity == null ? new MapPin(pin.Owner, state.Source.Name, state.Source.X, state.Source.Y, state.Source.Z, pin.Icon, false, pin.Author) : pin;
        bool check = shareChecks && state.Target;
        if (source.Name != state.Source.Name || source.X != state.Source.X || source.Y != state.Source.Y || source.Z != state.Source.Z || check != state.Source.Checked)
        {
            var old = state.Published; Index(key, state, false);
            // Portal/resource label changes already use their own publication refresh.
            for (int attempt = 0; attempt < Slots.Length && (check != state.Source.Checked || source.Owner == SharedMapTableData.DungeonOwner); attempt++)
            {
                state.Slot = (state.Slot + 1) % Slots.Length;
                double dx = source.X + Slots[state.Slot].X - old.X, dz = source.Z + Slots[state.Slot].Z - old.Z;
                if (dx * dx + dz * dz > 1.21) break;
            }
            state.Source = Copy(source, source.X, source.Z, check); Index(key, state, true); _dirty = true; Revision++;
            _changed?.Invoke(key, state.Published, "pin_publication");
        }
        var published = state.Published;
        return new MapPin(pin.Owner, pin.Name, published.X, pin.Y, published.Z, pin.Icon, check, pin.Author);
    }
    public void Load()
    {
        if (!File.Exists(FilePath)) { if (File.Exists(FilePath + ".bak")) throw new InvalidDataException("Shared pin state missing beside backup."); return; }
        using var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 52 || input.Length > 150L * 1024 * 1024) throw new InvalidDataException("Invalid shared pin-state length.");
        using var reader = new BinaryReader(input); byte[] body = reader.ReadBytes((int)input.Length - 32);
        using var sha = SHA256.Create(); if (!sha.ComputeHash(body).SequenceEqual(reader.ReadBytes(32))) throw new InvalidDataException("Shared pin-state checksum mismatch.");
        using var fields = new BinaryReader(new MemoryStream(body, false), new UTF8Encoding(false, true));
        uint magic = fields.ReadUInt32(); int format = fields.ReadInt32();
        if (magic != Magic || (format != 1 && format != 2) || fields.ReadInt64() != _world) throw new InvalidDataException("Shared pin-state format/world mismatch.");
        int count = fields.ReadInt32(); if (count < 0 || count > Maximum) throw new InvalidDataException("Shared pin-state count invalid.");
        var loaded = new Dictionary<string, State>();
        for (int i = 0; i < count; i++)
        {
            byte[] id = fields.ReadBytes(32); if (id.Length != 32) throw new EndOfStreamException();
            string key = BitConverter.ToString(id).Replace("-", "").ToLowerInvariant();
            long owner = fields.ReadInt64(); int length = fields.ReadUInt16();
            if (length > 4096) throw new InvalidDataException("Shared pin-state label too long.");
            byte[] label = fields.ReadBytes(length); if (label.Length != length) throw new EndOfStreamException();
            var pin = new MapPin(owner, new UTF8Encoding(false, true).GetString(label), fields.ReadSingle(), fields.ReadSingle(), fields.ReadSingle(), fields.ReadInt32(), fields.ReadBoolean());
            bool target = fields.ReadBoolean(); int slot = fields.ReadByte();
            _ = SharedMapPinRemovals.Identity(pin);
            if (slot >= Slots.Length || loaded.ContainsKey(key)) throw new InvalidDataException("Invalid shared pin-state record.");
            loaded.Add(key, new State(pin, target, slot));
        }
        var adopted = new Dictionary<string, string>();
        if (format >= 2)
        {
            int aliases = fields.ReadInt32(); if (aliases < 0 || aliases > Maximum) throw new InvalidDataException("Invalid adopted pin count.");
            for (int i = 0; i < aliases; i++)
            {
                byte[] alias = fields.ReadBytes(32), target = fields.ReadBytes(32);
                if (alias.Length != 32 || target.Length != 32) throw new EndOfStreamException();
                string aliasKey = BitConverter.ToString(alias).Replace("-", "").ToLowerInvariant(), targetKey = BitConverter.ToString(target).Replace("-", "").ToLowerInvariant();
                if (!loaded.ContainsKey(targetKey) || adopted.ContainsKey(aliasKey)) throw new InvalidDataException("Invalid adopted pin mapping.");
                adopted.Add(aliasKey, targetKey);
            }
        }
        if (fields.BaseStream.Position != fields.BaseStream.Length) throw new InvalidDataException("Trailing shared pin-state bytes.");
        _states.Clear(); _issued.Clear(); foreach (var pair in loaded) { _states.Add(pair.Key, pair.Value); Index(pair.Key, pair.Value, true); } _dirty = false;
        _adopted.Clear(); foreach (var pair in adopted) _adopted.Add(pair.Key, pair.Value);
    }
    public bool SaveIfDue(float now, float interval) { if (now < _nextSave) return false; _nextSave = now + interval; return Save(); }
    public bool Save()
    {
        if (!_dirty) return false;
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
        {
            writer.Write(Magic); writer.Write(2); writer.Write(_world); writer.Write(_states.Count);
            foreach (var pair in _states.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                for (int j = 0; j < 64; j += 2) writer.Write(byte.Parse(pair.Key.Substring(j, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                var p = pair.Value.Source; writer.Write(p.Owner); byte[] label = Encoding.UTF8.GetBytes(p.Name);
                writer.Write((ushort)label.Length); writer.Write(label); writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); writer.Write(p.Icon); writer.Write(p.Checked);
                writer.Write(pair.Value.Target); writer.Write((byte)pair.Value.Slot);
            }
            writer.Write(_adopted.Count);
            foreach (var pair in _adopted.OrderBy(p => p.Key, StringComparer.Ordinal))
                foreach (string id in new[] { pair.Key, pair.Value })
                    for (int j = 0; j < 64; j += 2) writer.Write(byte.Parse(id.Substring(j, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = body.ToArray(); using var sha = SHA256.Create();
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); byte[] hash = sha.ComputeHash(bytes); file.Write(hash, 0, hash.Length); file.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak"); else File.Move(temporary, FilePath);
            _dirty = false; return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
