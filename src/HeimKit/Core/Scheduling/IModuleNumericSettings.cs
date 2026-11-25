using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace HeimKit.Core.Scheduling;

internal interface IModuleNumericSettings
{
    IReadOnlyList<NumericSettingBinding> NumericSettings { get; }
}

internal sealed class NumericSettingBinding
{
    internal static NumericSettingBinding Create<T>(string label, ConfigEntry<T> entry,
        string description, Func<string> applies, Action? onChanged = null) where T : IConvertible, IComparable
    {
        var range = (AcceptableValueRange<T>)entry.Description.AcceptableValues;
        return new NumericSettingBinding(label, entry, description, applies, onChanged,
            typeof(T) == typeof(int), Convert.ToDouble(range.MinValue), Convert.ToDouble(range.MaxValue));
    }

    private NumericSettingBinding(string label, ConfigEntryBase entry, string description,
        Func<string> applies, Action? onChanged, bool whole, double minimum, double maximum)
    {
        Label = label;
        Entry = entry;
        Description = description;
        Applies = applies;
        OnChanged = onChanged;
        Whole = whole;
        Minimum = minimum;
        Maximum = maximum;
    }

    internal string Label { get; }
    internal ConfigEntryBase Entry { get; }
    internal string Description { get; }
    internal Func<string> Applies { get; }
    internal Action? OnChanged { get; }
    internal bool Whole { get; }
    internal double Minimum { get; }
    internal double Maximum { get; }
    internal double Value => Convert.ToDouble(Entry.BoxedValue);
    internal object Box(double value) => Whole ? (object)(int)value : (float)value;
}
