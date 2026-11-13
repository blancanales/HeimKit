internal static class LogCapture
{
    internal static string Text(object? value)
    {
        string text = value?.ToString() ?? "";
        return text.Length >= 9 && text[2] == ':' && text[5] == ':' && text[8] == ' ' ? text.Substring(9) : text;
    }
    // DebugVerbosity is process-wide; rebinding an unsaved file restores the Normal default for later tests.
    internal static void ResetVerbosity() =>
        HeimKit.Configuration.DiagnosticsSettings.Bind(new BepInEx.Configuration.ConfigFile(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false });
}
