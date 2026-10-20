namespace ConPhone.Windows;

internal static class SessionDiagnostics
{
    private static readonly System.Threading.Channels.Channel<string> Pending =
        System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(2048)
        { SingleReader = true, FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
    private static readonly Task Writer = Task.Run(WriteLoopAsync);
    static SessionDiagnostics() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        Pending.Writer.TryComplete();
        try { Writer.Wait(200); } catch { }
    };
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConPhone", "diagnostics", "session.log");

    public static void Write(string message)
    {
        // Only connection/status messages are logged, never clipboard contents or keys.
        Pending.Writer.TryWrite($"{DateTimeOffset.Now:O} mono={Environment.TickCount64} {message}{Environment.NewLine}");
    }

    private static async Task WriteLoopAsync()
    {
        await foreach (var line in Pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                    File.Move(LogPath, LogPath + ".previous", true);
                await File.AppendAllTextAsync(LogPath, line).ConfigureAwait(false);
            }
            catch { /* Logging must never backpressure input or BLE callbacks. */ }
        }
    }

    public static void WriteException(string operation, Exception error)
    {
        // Exception messages/data may contain user payloads. Log only code sites,
        // types and HRESULTs; omit values, file paths and clipboard text.
        for (var depth = 0; depth < 4 && error is not null; depth++, error = error.InnerException!)
            Write($"{operation} [{depth}] {error.GetType().Name} HRESULT=0x{error.HResult:X8}\n" +
                new System.Diagnostics.StackTrace(error, false));
    }
}
