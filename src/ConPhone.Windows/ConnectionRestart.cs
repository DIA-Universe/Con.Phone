using System.Diagnostics;
using System.Globalization;

namespace ConPhone.Windows;

// Explicit recovery only. A fresh process drops every old WinRT provider and
// session wrapper, including references held by outstanding native callbacks.
// Never restart automatically on a transient disconnect or missing subscription.
internal static class ConnectionRestart
{
    internal static ProcessStartInfo StartInfo(params string[] arguments)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("LINKO 실행 파일을 찾을 수 없습니다.");
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LINKO.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    internal static void Launch()
    {
        using var current = Process.GetCurrentProcess();
        using var child = Process.Start(StartInfo("--restart-after",
            current.Id.ToString(CultureInfo.InvariantCulture),
            current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)))
            ?? throw new InvalidOperationException("LINKO 재시작을 준비하지 못했습니다.");
    }

    internal static async Task<bool> WaitForExitAsync(int processId, long startedUtcTicks, TimeSpan timeout)
    {
        if (processId <= 0 || processId == Environment.ProcessId || startedUtcTicks <= 0)
            return false;
        Process parent;
        try { parent = Process.GetProcessById(processId); }
        catch (ArgumentException) { return true; } // The old app has already exited.
        using (parent)
        {
            try
            {
                // A PID alone can be reused. Do not wait on or terminate another process.
                if (parent.StartTime.ToUniversalTime().Ticks != startedUtcTicks) return false;
                using var deadline = new CancellationTokenSource(timeout);
                await parent.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (InvalidOperationException) when (parent.HasExited) { return true; }
        }
    }
}
