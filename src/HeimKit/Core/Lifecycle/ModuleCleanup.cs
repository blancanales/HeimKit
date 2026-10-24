using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using BepInEx.Logging;

namespace HeimKit.Core.Lifecycle;

// Cleanup failures must not prevent remaining registrations or modules from stopping.
public static class ModuleCleanup
{
    public static bool Run(ManualLogSource logger, string name, params Action[] steps)
    {
        bool succeeded = true;
        foreach (var step in steps)
        {
            try { step(); }
            catch (Exception error)
            {
                succeeded = false;
                DiagnosticLog.Write(logger, name, CoreLogTopics.Lifecycle, "Cleanup failed; remaining cleanup continues.", level: LogLevel.Error, error: error);
            }
        }
        return succeeded;
    }
}
