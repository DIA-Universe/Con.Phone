using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConPhone.Windows;

public enum ClipboardEntryKind { Text, Image }

public sealed class ClipboardEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public ClipboardEntryKind Kind { get; set; }
    public string Preview { get; set; } = "";
    public string? Text { get; set; }
    public string? ImageFile { get; set; }
    public string Hash { get; set; } = "";

    public override string ToString() =>
        $"{CapturedAt:HH:mm:ss}  {(Kind == ClipboardEntryKind.Text ? "텍스트" : "이미지")}  {Preview}";
}

public sealed class ClipboardHistoryService : IDisposable
{
    private readonly ClipboardSink _sink;
    private readonly List<ClipboardEntry> _entries = [];
    private readonly object _gate = new();
    private int _limit;
    private bool _captureText;
    private bool _captureImages;
    private bool _disposed;
    private bool _selfWrite;

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConPhone");
    private static readonly string ImageRoot = Path.Combine(Root, "ClipboardImages");
    private static readonly string HistoryPath = Path.Combine(Root, "clipboard-history.json");

    public event Action? Changed;

    public ClipboardHistoryService(int limit, bool captureText, bool captureImages)
    {
        _limit = Math.Clamp(limit, 10, 200);
        _captureText = captureText;
        _captureImages = captureImages;
        Load();
        _sink = new ClipboardSink(this);
    }

    public IReadOnlyList<ClipboardEntry> Entries
    {
        get { lock (_gate) return _entries.Select(Clone).ToArray(); }
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

    private void OnClipboardUpdate()
    {
        if (_disposed || _selfWrite) return;
        try
        {
            if (_captureText && Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!string.IsNullOrEmpty(text)) AddText(text);
                return;
            }

            if (_captureImages && Clipboard.ContainsImage())
            {
                using var image = Clipboard.GetImage();
                if (image is not null) AddImage(image);
            }
        }
        catch (ExternalException)
        {
            // Another app can hold the clipboard for a short time. The next clipboard update is still monitored.
        }
        catch
        {
            // Clipboard history must never break input switching.
        }
    }

    private void AddText(string text)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("text\0" + text)));
        var preview = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (preview.Length > 90) preview = preview[..90] + "…";

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
        Changed?.Invoke();
    }

    private void AddImage(Image image)
    {
        Directory.CreateDirectory(ImageRoot);
        using var ms = new MemoryStream();
        image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        var bytes = ms.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        lock (_gate)
        {
            if (_entries.FirstOrDefault()?.Hash == hash) return;
            var duplicates = _entries.Where(x => x.Hash == hash).ToArray();
            foreach (var duplicate in duplicates) DeleteImageFile(duplicate);
            _entries.RemoveAll(x => x.Hash == hash);

            var id = Guid.NewGuid().ToString("N");
            var file = Path.Combine(ImageRoot, id + ".png");
            File.WriteAllBytes(file, bytes);
            _entries.Insert(0, new ClipboardEntry
            {
                Id = id,
                Kind = ClipboardEntryKind.Image,
                ImageFile = file,
                Preview = $"{image.Width} × {image.Height} 이미지",
                Hash = hash,
                CapturedAt = DateTime.Now
            });
            TrimLocked();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public bool Restore(string id)
    {
        ClipboardEntry? entry;
        lock (_gate) entry = _entries.FirstOrDefault(x => x.Id == id) is { } found ? Clone(found) : null;
        if (entry is null) return false;

        try
        {
            _selfWrite = true;
            if (entry.Kind == ClipboardEntryKind.Text && entry.Text is not null)
            {
                Clipboard.SetText(entry.Text, TextDataFormat.UnicodeText);
                return true;
            }
            if (entry.Kind == ClipboardEntryKind.Image && entry.ImageFile is { } path && File.Exists(path))
            {
                using var source = Image.FromFile(path);
                using var copy = new Bitmap(source);
                Clipboard.SetImage(copy);
                return true;
            }
        }
        catch (ExternalException) { }
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
            DeleteImageFile(entry);
            _entries.Remove(entry);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _entries) DeleteImageFile(entry);
            _entries.Clear();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    private void TrimLocked()
    {
        while (_entries.Count > _limit)
        {
            var last = _entries[^1];
            DeleteImageFile(last);
            _entries.RemoveAt(_entries.Count - 1);
        }
    }

    private static void DeleteImageFile(ClipboardEntry entry)
    {
        if (entry.Kind != ClipboardEntryKind.Image || string.IsNullOrWhiteSpace(entry.ImageFile)) return;
        try { if (File.Exists(entry.ImageFile)) File.Delete(entry.ImageFile); } catch { }
    }

    private void Load()
    {
        try
        {
            Directory.CreateDirectory(Root);
            if (!File.Exists(HistoryPath)) return;
            var loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(HistoryPath));
            if (loaded is null) return;
            _entries.AddRange(loaded.Where(x => x.Kind != ClipboardEntryKind.Image ||
                                                (!string.IsNullOrWhiteSpace(x.ImageFile) && File.Exists(x.ImageFile))));
            TrimLocked();
        }
        catch { }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Root);
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(HistoryPath, json);
        }
        catch { }
    }

    private static ClipboardEntry Clone(ClipboardEntry x) => new()
    {
        Id = x.Id, CapturedAt = x.CapturedAt, Kind = x.Kind, Preview = x.Preview,
        Text = x.Text, ImageFile = x.ImageFile, Hash = x.Hash
    };

    public void Dispose()
    {
        _disposed = true;
        _sink.Dispose();
    }

    private sealed class ClipboardSink : NativeWindow, IDisposable
    {
        private readonly ClipboardHistoryService _owner;
        public ClipboardSink(ClipboardHistoryService owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Caption = "Con.Phone.ClipboardSink", Parent = new nint(-3) });
            AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x031D) _owner.OnClipboardUpdate(); // WM_CLIPBOARDUPDATE
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != 0)
            {
                RemoveClipboardFormatListener(Handle);
                DestroyHandle();
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);
}
