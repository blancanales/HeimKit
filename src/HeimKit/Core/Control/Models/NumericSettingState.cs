using System;

namespace HeimKit.Core.Control.Models;

internal readonly struct NumericSettingState
{
    internal NumericSettingState(string module, string key, string label, string description, string applies,
        bool whole, double minimum, double maximum, double value)
    {
        Module = module;
        Key = key;
        Label = label;
        Description = description;
        Applies = applies;
        Whole = whole;
        Minimum = minimum;
        Maximum = maximum;
        Value = value;
    }

    internal string Module { get; }
    internal string Key { get; }
    internal string Label { get; }
    internal string Description { get; }
    internal string Applies { get; }
    internal bool Whole { get; }
    internal double Minimum { get; }
    internal double Maximum { get; }
    internal double Value { get; }

    internal bool AcceptsWhole(double value) => Accepts(value) && value == Math.Truncate(value);

    internal bool Accepts(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) &&
        value >= Minimum && value <= Maximum && (!Whole || value == Math.Truncate(value));
}
