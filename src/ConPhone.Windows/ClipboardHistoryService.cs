using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConPhone.Core;

namespace ConPhone.Windows;

public enum ClipboardEntryKind
{
    Text,
    Image
}

public sealed class ClipboardEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public ClipboardEntryKind Kind { get; set; }
    public string Preview { get; set; } = "";
    public string? Text { get; set; }
    public string? ImageFileName { get; set; }
    public string Hash { get; set; } = "";

    public override string ToString() =>
        $"{CapturedAt:MM-dd HH:mm:ss}  {(Kind == ClipboardEntryKind.Text ? "텍스트" : "이미지")}  {Preview}";
}

public sealed class ClipboardHistoryService : IDisposable
{
    private readonly object _gate = new();
    private readonly List<ClipboardEntry> _entries = [];
    private readonly ClipboardSink _sink;
    private readonly System.Windows.Forms.Timer _retryTimer;

    private int _limit;
    private bool _captureText;
    private bool _captureImages;
    private int _attempt;
    private bool _selfWrite;
    private bool _disposed;

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConPhone");

    private static readonly string ImageRoot = Path.Combine(Root, "ClipboardImages");
    private static readonly string HistoryPath = Path.Combine(Root, "clipboard-history.json");

    public event Action? Changed;
    public event Action<string>? Status;
    public event Action<ClipboardContent>? ContentCaptured;

    public ClipboardHistoryService(int limit, bool captureText, bool captureImages)
    {
        if (Application.OleRequired() != ApartmentState.STA)
            throw new InvalidOperationException("클립보드 기록은 STA UI 스레드에서 시작해야 합니다.");

        _limit = Math.Clamp(limit, 10, 200);
        _captureText = captureText;
        _captureImages = captureImages;

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImageRoot);
        Load();

        _sink = new ClipboardSink(this);
        _retryTimer = new System.Windows.Forms.Timer { Interval = 90 };
        _retryTimer.Tick += (_, _) => TryCaptureClipboard();
    }

    public IReadOnlyList<ClipboardEntry> Entries
    {
        get
        {
            lock (_gate)
                return _entries.Select(Clone).ToArray();
        }
    }

    public void Configure(int limit, bool captureText, bool captureImages)
    {
        lock (_gate)
        {
            _limit = Math.Clamp(limit, 10, 200);
            _captureText = captureText;
            _captureImages = captureImages;
            TrimLocked();
            SaveLocked();
        }

        Changed?.Invoke();
    }

    public void CaptureNow() => ScheduleCapture();

    private void ScheduleCapture()
    {
        if (_disposed || _selfWrite) return;
        _attempt = 0;
        _retryTimer.Stop();
        _retryTimer.Start();
    }

    private void TryCaptureClipboard()
    {
        if (_disposed || _selfWrite)
        {
            _retryTimer.Stop();
            return;
        }

        _attempt++;

        try
        {
            if (_captureText && Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                _retryTimer.Stop();
                if (!string.IsNullOrEmpty(text)) AddText(text, publish: true);
                return;
            }

            if (_captureImages && Clipboard.ContainsImage())
            {
                using var image = Clipboard.GetImage();
                _retryTimer.Stop();
                if (image is not null) AddImage(image, publish: true);
                return;
            }

            // A supported clipboard item is not present. No retry is needed.
            _retryTimer.Stop();
        }
        catch (ExternalException)
        {
            // WM_CLIPBOARDUPDATE can arrive while the source application still owns the clipboard.
            // Retry on the STA UI thread instead of silently losing the item.
            if (_attempt >= 12)
            {
                _retryTimer.Stop();
                Status?.Invoke("클립보드가 계속 사용 중이라 이번 항목을 읽지 못했습니다.");
            }
        }
        catch (Exception ex)
        {
            _retryTimer.Stop();
            Status?.Invoke($"클립보드 읽기 실패: {ex.Message}");
        }
    }

    private void AddText(string text, bool publish)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("text\0" + text)));

        var preview = text
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Trim();

        if (preview.Length > 100) preview = preview[..100] + "…";
        if (preview.Length == 0) preview = "(공백 텍스트)";

        lock (_gate)
        {
            if (_entries.FirstOrDefault()?.Hash == hash) return;

            _entries.RemoveAll(x => x.Hash == hash);
            _entries.Insert(0, new ClipboardEntry
            {
                Kind = ClipboardEntryKind.Text,
                Text = text,
                Preview = preview,
                Hash = hash,
                CapturedAt = DateTime.Now
            });

            TrimLocked();
            SaveLocked();
        }

        Status?.Invoke("텍스트 클립보드를 기록했습니다.");
        Changed?.Invoke();
        if (publish)
            ContentCaptured?.Invoke(ClipboardContent.FromText(text));
    }

    private void AddImage(Image image, bool publish)
    {
        using var ms = new MemoryStream();
        image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        var bytes = ms.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        lock (_gate)
        {
            if (_entries.FirstOrDefault()?.Hash == hash) return;

            foreach (var duplicate in _entries.Where(x => x.Hash == hash).ToArray())
                DeleteImageLocked(duplicate);

            _entries.RemoveAll(x => x.Hash == hash);

            var id = Guid.NewGuid().ToString("N");
            var fileName = id + ".png";
            File.WriteAllBytes(Path.Combine(ImageRoot, fileName), bytes);

            _entries.Insert(0, new ClipboardEntry
            {
                Id = id,
                Kind = ClipboardEntryKind.Image,
                ImageFileName = fileName,
                Preview = $"{image.Width} × {image.Height} 이미지",
                Hash = hash,
                CapturedAt = DateTime.Now
            });

            TrimLocked();
            SaveLocked();
        }

        Status?.Invoke("이미지/캡처 클립보드를 기록했습니다.");
        Changed?.Invoke();
        if (publish)
            ContentCaptured?.Invoke(ClipboardContent.FromImage(ClipboardContentKind.Png, bytes));
    }

    public bool TryGetContent(string id, out ClipboardContent? content)
    {
        ClipboardEntry? entry;
        lock (_gate)
        {
            var found = _entries.FirstOrDefault(x => x.Id == id);
            entry = found is null ? null : Clone(found);
        }

        content = null;
        if (entry is null) return false;

        if (entry.Kind == ClipboardEntryKind.Text && entry.Text is not null)
        {
            content = ClipboardContent.FromText(entry.Text);
            return true;
        }

        if (entry.Kind == ClipboardEntryKind.Image &&
            TryResolveImage(entry.ImageFileName, out var path))
        {
            content = ClipboardContent.FromImage(ClipboardContentKind.Png, File.ReadAllBytes(path));
            return true;
        }

        return false;
    }

    public bool TryGetLatestContent(out ClipboardContent? content)
    {
        content = null;
        string? id;
        lock (_gate)
            id = _entries.FirstOrDefault()?.Id;

        return id is not null && TryGetContent(id, out content);
    }

    public bool ApplyRemote(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            _selfWrite = true;
            _retryTimer.Stop();

            if (content.Kind == ClipboardContentKind.TextUtf8)
            {
                var text = content.GetText();
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                AddText(text, publish: false);
                Status?.Invoke("휴대폰에서 받은 텍스트를 Windows 클립보드에 적용했습니다.");
                return true;
            }

            using var stream = new MemoryStream(content.Data, writable: false);
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
            EnsureSafeImageDimensions(source.Width, source.Height);
            using var copy = new Bitmap(source);
            Clipboard.SetImage(copy);
            AddImage(copy, publish: false);
            Status?.Invoke("휴대폰에서 받은 이미지를 Windows 클립보드에 적용했습니다.");
            return true;
        }
        catch (ExternalException)
        {
            Status?.Invoke("다른 프로그램이 클립보드를 사용 중이라 휴대폰 내용을 적용하지 못했습니다.");
        }
        catch (Exception ex)
        {
            Status?.Invoke($"휴대폰 클립보드 적용 실패: {ex.Message}");
        }
        finally
        {
            _selfWrite = false;
        }

        return false;
    }

    private static void EnsureSafeImageDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 ||
            (long)width * height > 40_000_000L)
            throw new InvalidDataException(
                $"이미지 해상도가 안전 한도(40MP)를 초과합니다: {width}x{height}");
    }

    public bool Restore(string id)
    {
        ClipboardEntry? entry;
        lock (_gate)
        {
            var found = _entries.FirstOrDefault(x => x.Id == id);
            entry = found is null ? null : Clone(found);
        }

        if (entry is null) return false;

        try
        {
            _selfWrite = true;
            _retryTimer.Stop();

            if (entry.Kind == ClipboardEntryKind.Text && entry.Text is not null)
            {
                Clipboard.SetText(entry.Text, TextDataFormat.UnicodeText);
                Status?.Invoke("선택한 텍스트를 Windows 클립보드로 복원했습니다.");
                return true;
            }

            if (entry.Kind == ClipboardEntryKind.Image &&
                TryResolveImage(entry.ImageFileName, out var path))
            {
                using var source = Image.FromFile(path);
                using var copy = new Bitmap(source);
                Clipboard.SetImage(copy);
                Status?.Invoke("선택한 이미지를 Windows 클립보드로 복원했습니다.");
                return true;
            }
        }
        catch (ExternalException)
        {
            Status?.Invoke("다른 프로그램이 클립보드를 사용 중이라 복원하지 못했습니다.");
        }
        catch (Exception ex)
        {
            Status?.Invoke($"클립보드 복원 실패: {ex.Message}");
        }
        finally
        {
            _selfWrite = false;
        }

        return false;
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(x => x.Id == id);
            if (entry is null) return;

            DeleteImageLocked(entry);
            _entries.Remove(entry);
            SaveLocked();
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _entries) DeleteImageLocked(entry);
            _entries.Clear();
            SaveLocked();
        }

        Changed?.Invoke();
    }

    private void TrimLocked()
    {
        while (_entries.Count > _limit)
        {
            var entry = _entries[^1];
            DeleteImageLocked(entry);
            _entries.RemoveAt(_entries.Count - 1);
        }
    }

    private static bool TryResolveImage(string? fileName, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeName, StringComparison.Ordinal)) return false;

        path = Path.Combine(ImageRoot, safeName);
        return File.Exists(path);
    }

    private static void DeleteImageLocked(ClipboardEntry entry)
    {
        if (entry.Kind != ClipboardEntryKind.Image) return;
        if (!TryResolveImage(entry.ImageFileName, out var path)) return;

        try { File.Delete(path); } catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;

            var loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(
                File.ReadAllText(HistoryPath));

            if (loaded is null) return;

            foreach (var entry in loaded)
            {
                if (entry.Kind == ClipboardEntryKind.Image &&
                    !TryResolveImage(entry.ImageFileName, out _))
                    continue;

                _entries.Add(entry);
            }

            TrimLocked();
        }
        catch
        {
            _entries.Clear();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                _entries,
                new JsonSerializerOptions { WriteIndented = true });

            var temp = HistoryPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, HistoryPath, true);
        }
        catch (Exception ex)
        {
            Status?.Invoke($"클립보드 기록 저장 실패: {ex.Message}");
        }
    }

    private static ClipboardEntry Clone(ClipboardEntry x) => new()
    {
        Id = x.Id,
        CapturedAt = x.CapturedAt,
        Kind = x.Kind,
        Preview = x.Preview,
        Text = x.Text,
        ImageFileName = x.ImageFileName,
        Hash = x.Hash
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _retryTimer.Stop();
        _retryTimer.Dispose();
        _sink.Dispose();
    }

    private sealed class ClipboardSink : NativeWindow, IDisposable
    {
        private readonly ClipboardHistoryService _owner;

        public ClipboardSink(ClipboardHistoryService owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "Con.Phone.ClipboardSink",
                Parent = new nint(-3)
            });

            if (!AddClipboardFormatListener(Handle))
                throw new InvalidOperationException("Windows 클립보드 변경 감시를 시작하지 못했습니다.");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x031D) // WM_CLIPBOARDUPDATE
                _owner.ScheduleCapture();

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle == 0) return;
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);
}
