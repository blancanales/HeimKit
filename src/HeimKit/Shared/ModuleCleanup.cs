using System;
using BepInEx.Logging;

namespace HeimKit.Shared;

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
                DiagnosticLog.Write(logger, name, DiagnosticEvent.Lifecycle,
                    "Cleanup failed; remaining cleanup continues: " + error, level: LogLevel.Error);
            }
        }
        return succeeded;
    }
}
