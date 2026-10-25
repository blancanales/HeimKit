using System.Collections.Generic;

namespace HeimKit.Core.Diagnostics;

public readonly struct LogField
{
    public string Key { get; }
    public object? Value { get; }
    public bool Always { get; }
    public LogField(string key, object? value, bool always = false) { Key = key; Value = value; Always = always; }
    public static implicit operator LogField((string Key, object? Value) field) => new(field.Key, field.Value);
}

// Concrete sources keep enumeration allocation-free. Build arguments only after gating.
public readonly struct LogFields
{
    private readonly (string Key, object? Value)[]? _array;
    private readonly List<(string Key, object? Value)>? _list;
    private readonly Dictionary<string, object?>? _dictionary;
    private readonly LogField[]? _fields;
    private readonly int _skip, _limit;
    private LogFields((string, object?)[] value) { _array = value; _list = null; _dictionary = null; _fields = null; _skip = _limit = 0; }
    private LogFields(List<(string, object?)> value) { _array = null; _list = value; _dictionary = null; _fields = null; _skip = _limit = 0; }
    private LogFields(Dictionary<string, object?> value) { _array = null; _list = null; _dictionary = value; _fields = null; _skip = _limit = 0; }
    private LogFields(LogField[] value) { _array = null; _list = null; _dictionary = null; _fields = value; _skip = _limit = 0; }
    private LogFields(LogFields owner, int skip, int limit) { _array = owner._array; _list = owner._list; _dictionary = owner._dictionary; _fields = owner._fields; _skip = skip; _limit = limit; }
    public LogFields Take(int count) => new(this, _skip, count);
    public LogFields Skip(int count) => new(this, _skip + count, _limit);
    public static implicit operator LogFields((string Key, object? Value)[]? value) => value == null ? default : new(value);
    public static implicit operator LogFields(List<(string Key, object? Value)>? value) => value == null ? default : new(value);
    public static implicit operator LogFields(Dictionary<string, object?>? value) => value == null ? default : new(value);
    public static implicit operator LogFields(LogField[]? value) => value == null ? default : new(value);
    public Enumerator GetEnumerator() => new(this);
    public struct Enumerator
    {
        private readonly LogFields _owner;
        private Dictionary<string, object?>.Enumerator _dictionary;
        private int _index;
        private int _skip, _remaining;
        public LogField Current { get; private set; }
        internal Enumerator(LogFields owner) { _owner = owner; _dictionary = owner._dictionary?.GetEnumerator() ?? default; _index = -1; _skip = owner._skip; _remaining = owner._limit == 0 ? int.MaxValue : owner._limit; Current = default; }
        public bool MoveNext()
        {
            while (_skip > 0) { if (!Next()) return false; _skip--; }
            if (_remaining <= 0 || !Next()) return false;
            _remaining--; return true;
        }
        private bool Next()
        {
            if (_owner._dictionary != null)
            {
                if (!_dictionary.MoveNext()) return false;
                Current = new(_dictionary.Current.Key, _dictionary.Current.Value); return true;
            }
            _index++;
            if (_owner._array != null && _index < _owner._array.Length) { Current = _owner._array[_index]; return true; }
            if (_owner._list != null && _index < _owner._list.Count) { Current = _owner._list[_index]; return true; }
            if (_owner._fields != null && _index < _owner._fields.Length) { Current = _owner._fields[_index]; return true; }
            return false;
        }
        public void Dispose() => _dictionary.Dispose();
    }
}
