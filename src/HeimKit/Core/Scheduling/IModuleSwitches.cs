using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace HeimKit.Core.Scheduling;

public interface IModuleSwitches
{
    IReadOnlyList<SwitchBinding> Switches { get; }
    SwitchApplyMode ApplyMode { get; }
}

public interface IAdministrativeReconciliation
{
    void ScheduleReconciliation();
}

public enum SwitchApplyMode { Reinitialize, SelfReconciling }

public sealed class SwitchBinding
{
    public SwitchBinding(string label, ConfigEntry<bool> entry, bool applyLive = false, Func<bool>? applyInPlace = null)
    {
        Label = label;
        Entry = entry;
        ApplyLive = applyLive;
        ApplyInPlace = applyInPlace;
    }

    public bool ApplyLive { get; }
    public Func<bool>? ApplyInPlace { get; }

    public string Label { get; }
    public ConfigEntry<bool> Entry { get; }
}
