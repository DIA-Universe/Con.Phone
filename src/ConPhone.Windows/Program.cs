namespace ConPhone.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length == 1 && args[0] == "--ui-lifecycle-selftest")
        {
            try { MainForm.RunCloseLifecycleSelfTest(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            return;
        }

        if (args.Length == 1 && args[0] == "--ble-lifecycle-selftest")
        {
            using var testMutex = new Mutex(true, @"Local\DIAUniverse.ConPhone", out var canTest);
            if (!canTest) { Console.Error.WriteLine("Exit LINKO before the native BLE lifecycle test."); Environment.ExitCode = 1; return; }
            try { BleLifecycleSelfTest.RunAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            return;
        }

        if (args.Length == 3 && args[0] == "--clipboard-selftest-write")
        {
            try
            {
                var value = new DataObject();
                value.SetData(args[1] == "html" ? DataFormats.Html : DataFormats.UnicodeText, false, args[2]);
                Clipboard.SetDataObject(value, true);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            return;
        }

        if (args.Length == 1 && args[0] == "--release-selftest")
        {
            try { ReleaseSelfTest.RunAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            return;
        }

        if (args.Length == 1 && args[0] == "--input-selftest")
        {
            try { InputSelfTest.Run(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Environment.ExitCode = 1;
            }
            return;
        }

        if (args.Length >= 4 &&
            args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[2], out var oldProcessId))
        {
            try
            {
                LocalUpdateService.ApplyFromStagedProcess(
                    args[1],
                    oldProcessId,
                    args[3]);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Con.Phone 업데이트 적용 실패.\r\n{ex.Message}",
                    "업데이트",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return;
        }

        if (args.Length >= 2 && args[0].Equals("--snapshot", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(directory);

            using var form = new MainForm(previewMode: true)
            {
                Size = args.Length >= 4 && int.TryParse(args[2], out var width) && int.TryParse(args[3], out var height)
                    ? new Size(Math.Max(900, width), Math.Max(720, height)) : new Size(1060, 860),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-2000, -2000)
            };

            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            Application.DoEvents();

            var tabs = FindFirst<TabControl>(form)
                ?? throw new InvalidOperationException("Preview TabControl not found.");

            for (var i = 0; i < tabs.TabPages.Count; i++)
            {
                tabs.SelectedIndex = i;
                Application.DoEvents();
                form.PerformLayout();
                Application.DoEvents();

                using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
                var safeName = new string(tabs.TabPages[i].Text.Select(ch =>
                    Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
                bitmap.Save(
                    Path.Combine(directory, $"{i + 1:00}-{safeName}.png"),
                    System.Drawing.Imaging.ImageFormat.Png);
            }

            form.Close();
            return;
        }

        if (args.Length >= 1 && args[0].Equals("--clipboard-selftest", StringComparison.OrdinalIgnoreCase))
        {
            try { RunClipboardSelfTest(); Console.WriteLine("PASS: external-process text/HTML clipboard notifications, recopy and echo suppression."); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            return;
        }

        using var mutex = new Mutex(true, @"Local\DIAUniverse.ConPhone", out bool first);
        if (!first)
        {
            MessageBox.Show("Con.Phone이 이미 실행 중입니다.");
            return;
        }

        using var mainForm = new MainForm();
        EventHandler exiting = (_, _) => mainForm.EmergencyBleCleanup();
        UnhandledExceptionEventHandler unhandled = (_, args) => {
            if (args.ExceptionObject is Exception error) SessionDiagnostics.WriteException("Unhandled process exception", error);
            mainForm.EmergencyBleCleanup();
        };
        Microsoft.Win32.SessionEndingEventHandler sessionEnding = (_, _) => mainForm.EmergencyBleCleanup();
        ThreadExceptionEventHandler uiFailure = (_, args) => mainForm.HandleUiThreadException(args.Exception);
        AppDomain.CurrentDomain.ProcessExit += exiting;
        AppDomain.CurrentDomain.UnhandledException += unhandled;
        Application.ApplicationExit += exiting;
        Application.ThreadException += uiFailure;
        Microsoft.Win32.SystemEvents.SessionEnding += sessionEnding;
        try { Application.Run(mainForm); }
        finally
        {
            mainForm.EmergencyBleCleanup();
            AppDomain.CurrentDomain.ProcessExit -= exiting;
            AppDomain.CurrentDomain.UnhandledException -= unhandled;
            Application.ApplicationExit -= exiting;
            Application.ThreadException -= uiFailure;
            Microsoft.Win32.SystemEvents.SessionEnding -= sessionEnding;
        }
    }

    private static T? FindFirst<T>(Control root) where T : Control
    {
        if (root is T found) return found;
        foreach (Control child in root.Controls)
        {
            var nested = FindFirst<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static void RunClipboardSelfTest()
    {
        // Do not leave test markers in the user's clipboard after verification.
        var original = Clipboard.GetDataObject();
        var snapshot = new DataObject();
        if (original is not null)
            foreach (var format in original.GetFormats(false))
            {
                var value = original.GetData(format, false);
                if (value is not null) snapshot.SetData(format, false, value);
            }
        try { RunClipboardSelfTestCore(); }
        finally
        {
            if (original is null) Clipboard.Clear();
            else Clipboard.SetDataObject(snapshot, true, 10, 100);
        }
    }

    private static void RunClipboardSelfTestCore()
    {
        var marker = $"Con.Phone 한글🙂 clipboard self-test {Guid.NewGuid():N}";
        Clipboard.SetText(marker, TextDataFormat.UnicodeText);

        using var history = new ClipboardHistoryService(
            limit: 10,
            captureText: true,
            captureImages: false,
            storageRoot: Path.Combine(Path.GetTempPath(), "ConPhone-selftest", Guid.NewGuid().ToString("N")));

        var syncEvents = 0;
        var remoteSyncEvents = 0;
        var remoteText = marker + " remote";
        var selfTestStatus = new List<string>();
        history.Status += status => selfTestStatus.Add(status);

        history.ContentCaptured += content =>
        {
            if (content.Kind !=
                ConPhone.Core.ClipboardContentKind.TextUtf8)
                return;

            var text = content.GetText();
            if (string.Equals(
                    text,
                    marker,
                    StringComparison.Ordinal))
                Interlocked.Increment(ref syncEvents);
            else if (string.Equals(
                         text,
                         remoteText,
                         StringComparison.Ordinal))
                Interlocked.Increment(ref remoteSyncEvents);
        };

        WriteClipboardFromOtherProcess("text", marker);

        var deadline = DateTime.UtcNow.AddSeconds(4);
        ClipboardEntry? entry = null;

        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(40);
            entry = history.Entries.FirstOrDefault(x => x.Text == marker);
            if (entry is not null && Volatile.Read(ref syncEvents) >= 1) break;
        }

        if (entry is null)
            throw new InvalidOperationException("Clipboard history did not capture a real Windows text clipboard item.");

        if (Volatile.Read(ref syncEvents) < 1)
            throw new InvalidOperationException("Clipboard sync event was not emitted for the first copy.");

        // Re-copy identical text. History should remain deduplicated, but sync intent
        // must be emitted again so the same value can be sent to a phone later.
        WriteClipboardFromOtherProcess("html", "<html><!--StartFragment--><span>" + System.Net.WebUtility.HtmlEncode(marker) + "</span><!--EndFragment--></html>");

        deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline &&
               Volatile.Read(ref syncEvents) < 2)
        {
            Application.DoEvents();
            Thread.Sleep(40);
        }

        if (Volatile.Read(ref syncEvents) < 2)
            throw new InvalidOperationException(
                "Repeated identical clipboard copy did not emit a new sync event.");

        if (history.Entries.Count(x => x.Text == marker) != 1)
            throw new InvalidOperationException(
                "Repeated identical clipboard copy duplicated the history row.");

        var remoteMarker =
            ConPhone.Core.ClipboardContent.FromText(
                remoteText);

        if (!history.ApplyRemote(
                remoteMarker,
                out var remoteChanged) ||
            !remoteChanged)
            throw new InvalidOperationException(
                "First remote clipboard apply was not reported as changed.");

        if (!history.ApplyRemote(
                remoteMarker,
                out var replayChanged) ||
            replayChanged)
            throw new InvalidOperationException(
                "Remote clipboard replay incorrectly created a new change.");

        // A fresh local copy between identical remote values must not be
        // mistaken for an ACK replay and leave the local value stuck.
        Clipboard.SetText("new local value between remote copies", TextDataFormat.UnicodeText);
        if (!history.ApplyRemote(remoteMarker, out var reapplied) || !reapplied ||
            Clipboard.GetText(TextDataFormat.UnicodeText) != remoteText)
            throw new InvalidOperationException("Remote recopy after local replacement was incorrectly suppressed.");

        // Simulate a delayed WM_CLIPBOARDUPDATE arriving after ApplyRemote
        // released its immediate self-write guard.
        history.CaptureNow();
        deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(40);
        }

        if (Volatile.Read(ref remoteSyncEvents) != 0)
            throw new InvalidOperationException(
                "A delayed self clipboard notification was incorrectly published as a new sync intent.");

        // Once the one-shot suppression token is consumed, a real later user
        // copy of the identical value must still be publishable.
        Clipboard.SetText(
            remoteText,
            TextDataFormat.UnicodeText);
        history.CaptureNow();

        deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline &&
               Volatile.Read(ref remoteSyncEvents) < 1)
        {
            Application.DoEvents();
            Thread.Sleep(40);
        }

        if (Volatile.Read(ref remoteSyncEvents) < 1)
            throw new InvalidOperationException(
                "A real re-copy after self-write suppression was incorrectly hidden. " +
                $"Clipboard still matches: {Clipboard.GetText(TextDataFormat.UnicodeText) == remoteText}; " +
                $"Events: local={syncEvents}, remote={remoteSyncEvents}; " +
                string.Join(" | ", selfTestStatus));

        Clipboard.SetText("temporary replacement", TextDataFormat.UnicodeText);
        if (!history.Restore(entry.Id))
            throw new InvalidOperationException("Clipboard history could not restore the captured item.");

        var restored = Clipboard.GetText(TextDataFormat.UnicodeText);
        if (!string.Equals(restored, marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Restored Windows clipboard text did not match the original.");

        // A valid signature alone must not allow truncated image data onto the clipboard.
        var invalidPng = ConPhone.Core.ClipboardContent.FromImage(
            ConPhone.Core.ClipboardContentKind.Png,
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 });
        if (history.ApplyRemote(invalidPng, out var invalidChanged) || invalidChanged)
            throw new InvalidOperationException("Truncated PNG was accepted by the Windows receiver.");
        if (Clipboard.GetText(TextDataFormat.UnicodeText) != marker)
            throw new InvalidOperationException("Invalid PNG changed the Windows clipboard.");

        history.Clear();
    }

    private static void WriteClipboardFromOtherProcess(string format, string value)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LINKO.dll"));
        start.ArgumentList.Add("--clipboard-selftest-write");
        start.ArgumentList.Add(format);
        start.ArgumentList.Add(value);
        using var child = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Clipboard source process did not start.");
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!child.HasExited && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
        if (!child.HasExited) { child.Kill(); throw new TimeoutException("External clipboard source timed out."); }
        if (child.ExitCode != 0) throw new InvalidOperationException("External clipboard source failed: " + child.StandardError.ReadToEnd());
    }
}
