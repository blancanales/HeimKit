namespace HeimKit.Core.Control.Models;

internal readonly struct SwitchDefinition
{
    internal SwitchDefinition(string module, string key, string label, string description, bool isModuleSwitch)
    {
        Module = module;
        Key = key;
        Label = label;
        Description = description;
        IsModuleSwitch = isModuleSwitch;
    }

    internal string Module { get; }
    internal string Key { get; }
    internal string Label { get; }
    internal string Description { get; }
    internal bool IsModuleSwitch { get; }
}
