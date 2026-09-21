namespace ConPhone.Windows;

internal static class SessionDiagnostics
{
    private static readonly object Gate = new();
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConPhone", "diagnostics", "session.log");

    public static void Write(string message)
    {
        // Only connection/status messages are logged, never clipboard contents or keys.
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                    File.Move(LogPath, LogPath + ".previous", true);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { /* Diagnostics must not stop HID input. */ }
    }
}
