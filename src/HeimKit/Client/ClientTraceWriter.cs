using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace HeimKit.Client;

internal sealed class ClientTraceWriter : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly string _file;
    private StreamWriter? _fallback;
    internal ClientTraceWriter(ManualLogSource log, string file) { _log = log; _file = file; }
    internal void Write(string message)
    {
        var entry = new LogEventArgs(message, LogLevel.Debug, _log);
        bool hasDisk = false;
        foreach (var listener in BepInEx.Logging.Logger.Listeners)
        {
            if (listener is not DiskLogListener disk) continue;
            hasDisk = true;
            // The player opted in to this trace. Preserve the filter for all other messages.
            if ((disk.DisplayedLogLevel & LogLevel.Debug) == 0)
                disk.LogWriter.WriteLine(entry.ToString());
        }
        if (!hasDisk)
        {
            _fallback ??= new StreamWriter(Path.Combine(Paths.BepInExRootPath, _file), true)
                { AutoFlush = true };
            _fallback.WriteLine(entry.ToString());
        }
        _log.LogDebug(message);
    }

    public void Dispose()
    {
        _fallback?.Dispose();
        _fallback = null;
    }
}
