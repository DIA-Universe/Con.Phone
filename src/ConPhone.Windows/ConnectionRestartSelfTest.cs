using System.Diagnostics;

namespace ConPhone.Windows;

internal static class ConnectionRestartSelfTest
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS: {name}");
    }

    internal static async Task RunAsync()
    {
        using var parent = Process.Start(ConnectionRestart.StartInfo("--connection-restart-test-parent"))
            ?? throw new InvalidOperationException("Cannot start owned test process");
        var started = parent.StartTime.ToUniversalTime().Ticks;
        var deadline = TimeSpan.FromSeconds(10);
        Check(!await ConnectionRestart.WaitForExitAsync(Environment.ProcessId, started, deadline),
            "Restart cannot wait on itself");
        Check(!await ConnectionRestart.WaitForExitAsync(parent.Id, started + 1, deadline),
            "A mismatched process creation time is rejected");
        Check(!await ConnectionRestart.WaitForExitAsync(parent.Id, started, TimeSpan.FromMilliseconds(50)) && !parent.HasExited,
            "Timeout leaves the existing process alive and does not permit a competing startup");
        var wait = ConnectionRestart.WaitForExitAsync(parent.Id, started, deadline);
        Check(!wait.IsCompleted, "New app must wait while the previous process is alive");
        Check(await wait && parent.HasExited, "New app may start only after the previous process exits");
        Check(await ConnectionRestart.WaitForExitAsync(parent.Id, started, deadline),
            "Recovery accepts a parent that already finished normal shutdown");
        Console.WriteLine("PASS: Real-process restart handoff. Physical phone reconnection NOT tested.");
    }
}
