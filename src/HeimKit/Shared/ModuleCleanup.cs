using System;
using BepInEx.Logging;

namespace HeimKit.Shared;

// Cleanup failures must not prevent remaining registrations or modules from stopping.
public static class ModuleCleanup
{
    public static void Run(ManualLogSource logger, string name, params Action[] steps)
    {
        foreach (var step in steps)
        {
            try { step(); }
            catch (Exception error)
            {
                DiagnosticLog.Write(logger, name, DiagnosticEvent.Lifecycle,
                    "Cleanup failed; remaining cleanup continues: " + error, level: LogLevel.Error);
            }
        }
    }
}
