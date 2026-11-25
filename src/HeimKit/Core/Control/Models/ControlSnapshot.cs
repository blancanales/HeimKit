using System;

namespace HeimKit.Core.Control.Models;

internal sealed class ControlSnapshot
{
    internal int Protocol { get; set; }
    internal string ServerVersion { get; set; } = "";
    internal long Revision { get; set; }
    internal bool IsAdmin { get; set; }
    internal bool AdminChangesAllowed { get; set; }
    internal int RequestId { get; set; }
    internal SetResult Result { get; set; }
    internal NumericSettingState[] Numbers { get; set; } = Array.Empty<NumericSettingState>();
    internal SwitchState[] Switches { get; set; } = Array.Empty<SwitchState>();
}
