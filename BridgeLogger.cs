static class BridgeLogger
{
    private static readonly object Sync = new();

    public static string LogFilePath => Path.Combine(AppInfo.TempFolder, "bridge.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.TempFolder);
            var line = $"{DateTimeOffset.Now:O} {message}";

            lock (Sync)
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostic logging must never interrupt capture.
        }
    }
}
