namespace ConPhone.Windows;

public sealed partial class MainForm
{
    // Exercise the real FormClosing handler and WinForms message loop, without
    // publishing BLE services or reading/writing the user's clipboard.
    internal static void RunCloseLifecycleSelfTest()
    {
        RunCloseLifecycleScenario("application exit");
        RunCloseLifecycleScenario("fatal UI error");
        RunCloseLifecycleScenario("user close then tray exit");
        RunCloseLifecycleScenario("connection recovery");
    }

    private static void RunCloseLifecycleScenario(string scenario)
    {
        using var form = new MainForm(previewMode: true)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-3000, -3000)
        };
        var pump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        form._pump = pump.Task;
        form.FormClosing += form.ClosingAsync;
        Exception? uiError = null;
        ThreadExceptionEventHandler captureError = (_, e) => uiError ??= e.Exception;
        Application.ThreadException += captureError;
        var forced = false;
        var fatalNotices = 0;
        var restartLaunches = 0;
        using var completePump = new System.Windows.Forms.Timer { Interval = 150 };
        completePump.Tick += (_, _) => { completePump.Stop(); pump.TrySetResult(); };
        using var watchdog = new System.Windows.Forms.Timer { Interval = 3000 };
        watchdog.Tick += (_, _) => {
            forced = true;
            form.FormClosing -= form.ClosingAsync;
            form.Close();
        };
        form.Shown += (_, _) => form.BeginInvoke(() => {
            watchdog.Start();
            completePump.Start();
            form.HandleUiThreadException(
                new System.Runtime.InteropServices.COMException("synthetic disconnected peer", unchecked((int)0x80010108)),
                _ => throw new InvalidOperationException("A transient disconnect was treated as fatal."));
            if (!form.Visible || form._lifetime.IsCancellationRequested || form._closing)
                throw new InvalidOperationException("A transient peer error hid the UI or permanently stopped BLE.");
            Console.WriteLine("PASS: RPC_E_DISCONNECTED keeps the window and server lifetime alive.");
            switch (scenario)
            {
                case "connection recovery":
                    try {
                        form.RestartConnectionAsync(() => throw new InvalidOperationException("synthetic launch failure")).GetAwaiter().GetResult();
                        throw new Exception("Expected launch failure");
                    }
                    catch (InvalidOperationException) {
                        if (form._exitRequested || form._closing || !form.Visible)
                            throw new InvalidOperationException("A failed restart launch closed the existing app.");
                        Console.WriteLine("PASS: Failed recovery launch keeps the existing window available.");
                    }
                    form.RestartConnectionAsync(() => restartLaunches++).GetAwaiter().GetResult();
                    form.RestartConnectionAsync(() => restartLaunches++).GetAwaiter().GetResult();
                    if (restartLaunches != 1)
                        throw new InvalidOperationException("Repeated recovery click launched another successor.");
                    break;
                case "fatal UI error":
                    form.HandleUiThreadException(new InvalidOperationException("synthetic UI bug"), _ => fatalNotices++);
                    break;
                case "user close then tray exit":
                    form.Close();
                    if (form.Visible || form._closing || form._lifetime.IsCancellationRequested)
                        throw new InvalidOperationException("User X no longer preserves the tray service.");
                    form.RequestExit();
                    break;
                default:
                    // Application.Exit triggers this reason, followed by an async Close().
                    form.OnFormClosing(new FormClosingEventArgs(CloseReason.ApplicationExitCall, false));
                    break;
            }
        });
        try { Application.Run(form); }
        finally
        {
            Application.ThreadException -= captureError;
            form._input.Dispose();
            form.EmergencyBleCleanup();
            form._tray.Dispose();
        }
        if (forced || uiError is not null || !form._closed || !form.IsDisposed ||
            fatalNotices != (scenario == "fatal UI error" ? 1 : 0))
            throw new InvalidOperationException(
                $"Async application shutdown left a hidden form/process: forced={forced}, closed={form._closed}, disposed={form.IsDisposed}.", uiError);
        Console.WriteLine($"PASS: {scenario} closes the window/message loop after cleanup, without a hidden process.");
    }
}
