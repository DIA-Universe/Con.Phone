namespace ConPhone.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

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
                Size = new Size(980, 800),
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
            RunClipboardSelfTest();
            return;
        }

        using var mutex = new Mutex(true, @"Local\DIAUniverse.ConPhone", out bool first);
        if (!first)
        {
            MessageBox.Show("Con.Phone이 이미 실행 중입니다.");
            return;
        }

        Application.Run(new MainForm());
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
        var marker = $"Con.Phone clipboard self-test {Guid.NewGuid():N}";
        Clipboard.SetText(marker, TextDataFormat.UnicodeText);

        using var history = new ClipboardHistoryService(
            limit: 10,
            captureText: true,
            captureImages: false);

        var syncEvents = 0;
        history.ContentCaptured += content =>
        {
            if (content.Kind == ConPhone.Core.ClipboardContentKind.TextUtf8 &&
                string.Equals(content.GetText(), marker, StringComparison.Ordinal))
                Interlocked.Increment(ref syncEvents);
        };

        history.CaptureNow();

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
        Clipboard.SetText(marker, TextDataFormat.UnicodeText);
        history.CaptureNow();

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

        Clipboard.SetText("temporary replacement", TextDataFormat.UnicodeText);
        if (!history.Restore(entry.Id))
            throw new InvalidOperationException("Clipboard history could not restore the captured item.");

        var restored = Clipboard.GetText(TextDataFormat.UnicodeText);
        if (!string.Equals(restored, marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Restored Windows clipboard text did not match the original.");

        history.Clear();
    }
}
