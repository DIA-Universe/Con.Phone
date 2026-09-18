using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConPhone.Windows;
public enum ClipboardEntryKind { Text, Image }
public sealed record ClipboardEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public ClipboardEntryKind Kind { get; init; }
    public string Preview { get; init; } = "";
    public string? Text { get; init; }
    public byte[]? Png { get; init; }
    public string Hash { get; init; } = "";
    public int Bytes => Png?.Length ?? (Text?.Length ?? 0)*2;
    public override string ToString() => $"{CapturedAt:HH:mm:ss}  {(Kind==ClipboardEntryKind.Text ? "텍스트" : "이미지")}  {Preview}";
}

// All OLE clipboard reads/writes and persistence run on their own STA message-loop, never on a hook/UI thread.
public sealed class ClipboardHistoryService : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<ClipboardEntry> _entries = [];
    private Control? _dispatcher;
    private ClipboardSink? _sink;
    private System.Windows.Forms.Timer? _timer;
    private ConPhoneSettings _settings;
    private uint _seen, _selfSequence;
    private bool _requested;
    private int _attempt;
    private volatile bool _disposed;
    private volatile string _status = "클립보드 준비 중";
    public event Action? Changed;
    public event Action<string>? StatusChanged;
    public Task Started => _started.Task;
    public string Status => _status;
    private string HistoryPath => Path.Combine(SettingsStore.Root,"clipboard-history.dpapi");
    private const int MaxItemBytes = 8*1024*1024, MaxTotalBytes = 32*1024*1024;
    public IReadOnlyList<ClipboardEntry> Entries { get { lock(_gate) return _entries.ToArray(); } }
    public ClipboardHistoryService(ConPhoneSettings settings)
    {
        _settings=settings.Clone();
        _thread=new Thread(Run) { IsBackground=true, Name="ConPhone.Clipboard.STA" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
    }
    private void Run()
    {
        try
        {
            Application.OleRequired();
            _dispatcher=new Control(); _ = _dispatcher.Handle;
            _sink=new ClipboardSink(this);
            _seen=GetClipboardSequenceNumber();
            if (_settings.PersistClipboardHistory) Load();
            _timer=new System.Windows.Forms.Timer { Interval=120 };
            _timer.Tick += (_,_) => Poll(); _timer.Start();
            SetStatus("감시 중 · 텍스트/이미지 · 이 PC만"); _started.TrySetResult();
            Application.Run();
        }
        catch(Exception ex) { SetStatus($"클립보드 초기화 실패: {ex.GetType().Name} 0x{ex.HResult:X8}"); _started.TrySetException(ex); }
        finally { _timer?.Dispose(); _sink?.Dispose(); _dispatcher?.Dispose(); }
    }
    private void SetStatus(string text) { if (_status==text) return; _status=text; StatusChanged?.Invoke(text); }
    private async Task<T> OnThread<T>(Func<T> action)
    {
        await Started.ConfigureAwait(false);
        if(_disposed) throw new ObjectDisposedException(nameof(ClipboardHistoryService));
        var result=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher!.BeginInvoke(() => { try { result.TrySetResult(action()); } catch(Exception ex) { result.TrySetException(ex); } });
        return await result.Task.ConfigureAwait(false);
    }
    public Task ConfigureAsync(ConPhoneSettings settings) => OnThread(() =>
    {
        _settings=settings.Clone(); lock(_gate) Trim(); Save();
        SetStatus(_settings.CaptureClipboardText || _settings.CaptureClipboardImages ? "감시 중 · 이 PC만" : "자동 기록 일시 중지");
        Changed?.Invoke(); return true;
    });
    public Task CaptureCurrentAsync() => OnThread(() => { _requested=true; _attempt=0; _selfSequence=0; Poll(); return true; });
    private void Poll()
    {
        if(_disposed) return;
        uint sequence=GetClipboardSequenceNumber();
        if(!_requested && (sequence==_seen || sequence==_selfSequence)) return;
        if(!_settings.CaptureClipboardText && !_settings.CaptureClipboardImages) { _seen=sequence; _requested=false; return; }
        try
        {
            var data=Clipboard.GetDataObject();
            if(data is null) return;
            if(data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore") || HistoryExcluded(data))
            { _seen=sequence; _requested=false; SetStatus("민감 정보 표시가 있는 항목은 기록하지 않았습니다."); return; }
            ClipboardEntry? entry=null;
            // Prefer pixels when an application publishes both a screenshot and explanatory text.
            if(_settings.CaptureClipboardImages && (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG")))
            {
                Image? source=null;
                try
                {
                    source=data.GetData(DataFormats.Bitmap) as Image;
                    if(source is null && data.GetData("PNG") is Stream pngStream) source=Image.FromStream(pngStream);
                    if(source is not null)
                    {
                        if((long)source.Width*source.Height > 20000000) throw new InvalidDataException("ImageSizeLimit");
                        using var bitmap=new Bitmap(source); using var ms=new MemoryStream();
                        bitmap.Save(ms,System.Drawing.Imaging.ImageFormat.Png); var bytes=ms.ToArray();
                        if(bytes.Length>MaxItemBytes) throw new InvalidDataException("ImageByteLimit");
                        entry=new() { Kind=ClipboardEntryKind.Image, Png=bytes, Preview=$"{bitmap.Width} × {bitmap.Height}", Hash=Convert.ToHexString(SHA256.HashData(bytes)) };
                    }
                }
                finally { source?.Dispose(); }
            }
            if(entry is null && _settings.CaptureClipboardText && data.GetData(DataFormats.UnicodeText) is string text && text.Length>0)
            {
                if(text.Length>MaxItemBytes/2) throw new InvalidDataException("TextSizeLimit");
                string preview=text.Replace('\r',' ').Replace('\n',' ');
                if(preview.Length>90) preview=preview[..90]+"…";
                entry=new() { Kind=ClipboardEntryKind.Text, Text=text, Preview=preview, Hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("text\0"+text))) };
            }
            if(sequence!=GetClipboardSequenceNumber()) { _requested=true; return; }
            _seen=sequence; _requested=false; _attempt=0;
            if(entry is not null)
            {
                lock(_gate)
                {
                    _entries.RemoveAll(x=>x.Hash==entry.Hash && x.Kind==entry.Kind);
                    _entries.Insert(0,entry); Trim();
                }
                Save(); SetStatus($"저장됨 · {DateTime.Now:HH:mm:ss} · {entry.Kind}"); Changed?.Invoke();
            }
            else SetStatus("이번 형식은 기록 대상이 아닙니다 (텍스트/이미지만 지원).");
        }
        catch(ExternalException ex)
        {
            _requested=true;
            if(++_attempt>=10) { _seen=sequence; _requested=false; _attempt=0; SetStatus($"클립보드 점유로 읽기 실패 (0x{ex.HResult:X8}). '현재 항목 가져오기'로 재시도하세요."); }
        }
        catch(Exception ex) { _seen=sequence; _requested=false; _attempt=0; SetStatus($"클립보드 기록 실패: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
    }
    private static bool HistoryExcluded(IDataObject data)
    {
        object? value=data.GetData("CanIncludeInClipboardHistory");
        if(value is int i) return i==0;
        if(value is Stream stream && stream.Length>=4) { long pos=stream.Position; using var r=new BinaryReader(stream,Encoding.UTF8,true); bool excluded=r.ReadInt32()==0; stream.Position=pos; return excluded; }
        return false;
    }
    public Task<bool> RestoreAsync(string id) => OnThread(() =>
    {
        ClipboardEntry? entry; lock(_gate) entry=_entries.FirstOrDefault(x=>x.Id==id);
        if(entry is null) return false;
        var data=new DataObject();
        using var bytes=entry.Png is null ? null : new MemoryStream(entry.Png,false);
        using var image=bytes is null ? null : Image.FromStream(bytes);
        using var copy=image is null ? null : new Bitmap(image);
        if(entry.Kind==ClipboardEntryKind.Text) data.SetData(DataFormats.UnicodeText,entry.Text ?? "");
        else if(copy is not null) { data.SetData(DataFormats.Bitmap,copy); data.SetData("PNG",new MemoryStream(entry.Png!,false)); }
        else return false;
        Clipboard.SetDataObject(data,true,8,70); // Persistent native copy before disposing Image/stream.
        _selfSequence=GetClipboardSequenceNumber(); _seen=_selfSequence; _requested=false;
        SetStatus("클립보드 복원 완료 · 대상 Windows 프로그램에서 Ctrl+V"); return true;
    });
    public Task DeleteAsync(string id) => OnThread(() => { lock(_gate) _entries.RemoveAll(x=>x.Id==id); Save(); Changed?.Invoke(); return true; });
    public Task ClearAsync() => OnThread(() => { lock(_gate) _entries.Clear(); Save(); Changed?.Invoke(); return true; });
    private void Trim()
    {
        long bytes=_entries.Sum(x=>(long)x.Bytes);
        while(_entries.Count>_settings.ClipboardHistoryLimit || bytes>MaxTotalBytes)
        { bytes-=_entries[^1].Bytes; _entries.RemoveAt(_entries.Count-1); }
    }
    private void Load()
    {
        if(!File.Exists(HistoryPath)) return;
        try
        {
            if(new FileInfo(HistoryPath).Length>64*1024*1024) throw new InvalidDataException();
            var entries=JsonSerializer.Deserialize<List<ClipboardEntry>>(LocalVault.Unseal(File.ReadAllBytes(HistoryPath))) ?? [];
            lock(_gate) { _entries.AddRange(entries.Where(x=>x.Bytes<=MaxItemBytes)); Trim(); }
        }
        catch(Exception ex) { SetStatus($"저장 기록 읽기 실패: {ex.GetType().Name}. 원본을 유지합니다."); }
    }
    private void Save()
    {
        try
        {
            if(!_settings.PersistClipboardHistory) { if(File.Exists(HistoryPath)) File.Delete(HistoryPath); return; }
            byte[] json; lock(_gate) json=JsonSerializer.SerializeToUtf8Bytes(_entries);
            Directory.CreateDirectory(SettingsStore.Root);
            File.WriteAllBytes(HistoryPath+".tmp",LocalVault.Seal(json)); File.Move(HistoryPath+".tmp",HistoryPath,true);
        }
        catch(Exception ex) { SetStatus($"디스크 보관 실패: {ex.GetType().Name}. 현재 세션 기록은 유지됩니다."); }
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        try { _dispatcher?.BeginInvoke(() => Application.ExitThread()); } catch(InvalidOperationException) { }
        if(Thread.CurrentThread!=_thread) _thread.Join(1000);
    }
    private sealed class ClipboardSink : NativeWindow, IDisposable
    {
        private readonly ClipboardHistoryService _owner;
        public ClipboardSink(ClipboardHistoryService owner)
        {
            _owner=owner; CreateHandle(new CreateParams { Caption="ConPhone.Clipboard", Parent=new nint(-3) });
            if(!AddClipboardFormatListener(Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        protected override void WndProc(ref Message m)
        {
            // Reading delayed-rendered formats here can reenter the source application. Defer to the STA timer.
            if(m.Msg==0x031D) { _owner._requested=GetClipboardSequenceNumber()!=_owner._selfSequence; _owner._attempt=0; }
            base.WndProc(ref m);
        }
        public void Dispose() { if(Handle!=0) { RemoveClipboardFormatListener(Handle); DestroyHandle(); } }
    }
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AddClipboardFormatListener(nint window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveClipboardFormatListener(nint window);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
