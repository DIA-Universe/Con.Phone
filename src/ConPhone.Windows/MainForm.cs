using System.Diagnostics;
using System.Text.Json;
using ConPhone.Core;
using Microsoft.Win32;
namespace ConPhone.Windows;

public sealed class MainForm : Form
{
    private readonly bool _test;
    private readonly float _scale;
    private readonly InputCapture _input;
    private readonly BleHidServer _ble=new();
    private readonly Outbox _outbox=new();
    private readonly PointerScaler _scaler=new();
    private readonly CancellationTokenSource _lifetime=new();
    private readonly SemaphoreSlim _actions=new(1,1);
    private readonly ClipboardHistoryService _clipboard;
    private ConPhoneSettings _settings;
    private readonly Dictionary<string,Panel> _pages=[];
    private readonly Dictionary<string,Button> _nav=[];
    private readonly Label _state=new(),_detail=new(),_notice=new(),_clipStatus=new();
    private readonly ListBox _peers=new(),_history=new();
    private readonly TextBox _log=new(),_diagnosis=new(),_preview=new();
    private readonly PictureBox _image=new();
    private readonly NumericUpDown _mouse=new(),_scroll=new(),_interval=new(),_limit=new();
    private readonly CheckBox _invert=new(),_lockEnabled=new(),_textCapture=new(),_imageCapture=new(),_persist=new();
    private readonly HotkeyCaptureBox _toggle=new(),_emergency=new(),_lock=new();
    private readonly NotifyIcon _tray=new() { Icon=SystemIcons.Application,Text="Con.Phone" };
    private readonly System.Windows.Forms.Timer _timer=new() { Interval=500 };
    private Task? _pump;
    private bool _refreshing,_closing,_closed,_exitRequested,_serviceWanted=true;
    private long _epoch,_remoteEpoch;
    private int _entering,_ticks;
    private string _lastError="None",_currentPage="connection";
    internal InputCapture TestInput=>_input;
    internal HotkeyCaptureBox TestEditor=>_toggle;
    internal ClipboardHistoryService TestClipboard=>_clipboard;
    public MainForm(bool testMode=false,float layoutScale=1f)
    {
        _test=testMode; _scale=layoutScale;
        _settings=SettingsStore.Load();
        _input=new InputCapture(!testMode); _input.Configure(_settings);
        _scaler.Configure(_settings.MouseSpeedPercent,_settings.ScrollSpeedPercent,_settings.InvertScroll);
        _clipboard=new ClipboardHistoryService(_settings);
        Text="Con.Phone 0.2.1 RC · 안정화 검증판";
        AutoScaleMode=AutoScaleMode.Dpi; Font=new Font("Segoe UI",10f*_scale);
        BackColor=ConPhoneTheme.Basement; StartPosition=FormStartPosition.CenterScreen;
        ClientSize=new Size(S(1060),S(760)); MinimumSize=new Size(S(860),S(660));
        BuildUi(); LoadControls();
        _clipboard.Changed+=()=>Post(RefreshHistory);
        _clipboard.StatusChanged+=message=>Post(()=>_clipStatus.Text=message);
        foreach(var editor in new[]{_toggle,_emergency,_lock})
        {
            editor.Attach(_input);
            editor.RecordingChanged+=active=> { if(active) { long epoch=ForceLocal(); _=ReleaseAsync(epoch); Notice("단축키 등록 중 · 전역 동작은 잠시 중지됩니다."); } };
        }
        Deactivate+=(_,_)=>FinishRecorders();
        _input.Packet+=packet=>
        {
            if(!_input.IsRemote) return;
            long ticket=Volatile.Read(ref _remoteEpoch);
            if(ticket!=Volatile.Read(ref _epoch)) return;
            var scaled=packet is PointerPacket p ? _scaler.Apply(p) : packet;
            if(!_outbox.TryAdd(new(ticket,scaled))) { long e=ForceLocal(); Post(async()=> { await ReleaseAsync(e); Notice("전송 대기열이 찼습니다. 노트북 입력으로 복귀했습니다."); }); }
        };
        _input.Command+=command=>
        {
            if(command is InputCommand.ReturnLocal or InputCommand.LockAndReturn)
            {
                long ticket=ForceLocal();
                Post(async()=> { if(command==InputCommand.LockAndReturn) await LockReturnAsync(ticket); else await ReleaseAsync(ticket); });
            }
            else if(command==InputCommand.EnterPhone) Post(async()=>await EnterPhoneAsync());
        };
        _input.Fault+=error=> { long e=ForceLocal(); Post(async()=> { await ReleaseAsync(e); Notice(error); }); };
        _ble.Status+=message=>Post(()=>Log(message));
        _ble.Changed+=()=>
        {
            if(_input.IsRemote && !_ble.Ready) { ForceLocal(); Post(()=>Notice("입력 연결이 준비되지 않아 노트북으로 복귀했습니다. 자동 전송 재개는 하지 않습니다.")); }
        };
        _tray.Visible=!_test;
        var menu=new ContextMenuStrip();
        menu.Items.Add("창 열기",null,(_,_)=> { Show(); WindowState=FormWindowState.Normal; Activate(); });
        menu.Items.Add("노트북 복귀",null,(_,_)=> { long e=ForceLocal(); _=ReleaseAsync(e); });
        menu.Items.Add("완전 종료",null,(_,_)=> { _exitRequested=true; Close(); });
        _tray.ContextMenuStrip=menu;
        _tray.DoubleClick+=(_,_)=> { Show(); WindowState=FormWindowState.Normal; Activate(); };
        _timer.Tick+=async(_,_)=>
        {
            UpdateStatus();
            if(!_test && ++_ticks%4==0 && !_input.IsRemote && !_refreshing && _serviceWanted)
            { try { await RefreshPeersAsync(); } catch(Exception ex) { Log($"장치 갱신: {ex.GetType().Name} 0x{ex.HResult:X8}"); } }
        };
        _timer.Start();
        Shown+=async(_,_)=>
        {
            try
            {
                await _input.Started; await _clipboard.Started; RefreshHistory();
                if(SettingsStore.Notice.Length>0) Notice(SettingsStore.Notice);
                if(!_test) { _diagnosis.Text=await BleHidServer.DiagnoseAsync(); await _ble.StartAsync(); }
                else { _diagnosis.Text="UI/Windows 클립보드 테스트 모드 · Bluetooth 하드웨어 사용 안 함"; Notice("테스트 데이터만 표시 중 · 실제 휴대폰 연결 아님"); }
            }
            catch(Exception ex) { HandleError(ex); }
        };
        _pump=Task.Run(PumpAsync);
        if(!_test) { SystemEvents.PowerModeChanged+=PowerChanged; SystemEvents.SessionSwitch+=SessionChanged; }
        FormClosing+=ClosingAsync;
    }
    private int S(int value)=>(int)Math.Round(value*_scale);
    private Label Label(string text,bool bold=false)
    {
        var label=new Label { Text=text,AutoSize=true,Dock=DockStyle.Top,ForeColor=bold?ConPhoneTheme.Text:ConPhoneTheme.TextSecondary,Margin=new Padding(0,S(4),0,S(8)),Font=new Font(Font,bold?FontStyle.Bold:FontStyle.Regular),Tag="wrap" };
        return label;
    }
    private Button Button(string text,Func<Task> action,bool primary=false)
    {
        var button=new Button { Text=text,AutoSize=true,MinimumSize=new Size(S(96),S(38)),Padding=new Padding(S(10),S(4),S(10),S(4)),Margin=new Padding(0,0,S(8),S(6)),FlatStyle=FlatStyle.Flat,BackColor=primary?ConPhoneTheme.BrandWeak:Color.White,ForeColor=primary?Color.FromArgb(185,57,1):ConPhoneTheme.Text };
        button.FlatAppearance.BorderColor=primary?ConPhoneTheme.Brand:ConPhoneTheme.Stroke;
        button.Click+=async(_,_)=>await ActionAsync(action); return button;
    }
    private FlowLayoutPanel Buttons(params Control[] controls)
    {
        var flow=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Margin=new Padding(0),Padding=new Padding(0),Tag="wrapflow" };
        flow.Controls.AddRange(controls); return flow;
    }
    private TableLayoutPanel Stack()
    {
        var panel=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,Margin=new Padding(0),Padding=new Padding(0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        panel.SizeChanged+=(_,_)=>FitChildren(panel); return panel;
    }
    private void FitChildren(Control parent)
    {
        int width=Math.Max(S(80),parent.ClientSize.Width-parent.Padding.Horizontal-S(4));
        foreach(Control child in parent.Controls)
        {
            if(child.Tag as string is "wrap" or "wrapflow") child.MaximumSize=new Size(Math.Max(S(60),width-child.Margin.Horizontal),0);
        }
    }
    private TableLayoutPanel Card(string title,params Control[] content)
    {
        var card=Stack(); card.BackColor=Color.White; card.Padding=new Padding(S(18)); card.Margin=new Padding(0,0,0,S(16));
        card.Controls.Add(Label(title,true));
        foreach(var item in content) card.Controls.Add(item);
        return card;
    }
    private Panel Page(string name,params Control[] cards)
    {
        var page=new Panel { Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(S(2),S(2),S(12),S(2)) };
        var stack=Stack(); foreach(var card in cards) stack.Controls.Add(card); page.Controls.Add(stack);
        _pages[name]=page; return page;
    }
    private Control Field(string title,Control editor)
    {
        var row=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Margin=new Padding(0,S(3),0,S(6)) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,52)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,48));
        var label=Label(title); row.Controls.Add(label,0,0);
        editor.Dock=DockStyle.Fill; editor.Margin=new Padding(S(6),0,0,S(4));
        if(editor is HotkeyCaptureBox) { editor.Height=S(42); editor.MinimumSize=new Size(S(150),S(42)); }
        row.Controls.Add(editor,1,0);
        row.SizeChanged+=(_,_)=>label.MaximumSize=new Size(Math.Max(S(50),(int)(row.ClientSize.Width*.5)),0);
        return row;
    }
    private void Numeric(NumericUpDown n,int min,int max,int increment=1) { n.Minimum=min; n.Maximum=max; n.Increment=increment; n.ThousandsSeparator=true; }
    private void Check(CheckBox c,string text) { c.Text=text; c.AutoSize=true; c.Dock=DockStyle.Top; c.Margin=new Padding(0,S(5),0,S(6)); }
    private void BuildUi()
    {
        var root=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0),Padding=new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,S(180))); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); Controls.Add(root);
        var side=Stack(); side.Dock=DockStyle.Fill; side.AutoSize=false; side.Padding=new Padding(S(14),S(20),S(12),S(10)); side.BackColor=Color.White;
        var brand=Label("Con.Phone",true); brand.Font=new Font(Font.FontFamily,18*_scale,FontStyle.Bold); side.Controls.Add(brand);
        side.Controls.Add(Label("0.2.1 RC\nBluetooth 입력 공유"));
        foreach(var item in new[]{("connection","연결"),("input","입력 설정"),("clipboard","클립보드"),("diagnostics","진단")})
        {
            var b=new Button { Text=item.Item2,Dock=DockStyle.Top,Height=S(44),Margin=new Padding(0,0,0,S(6)),FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(S(10),0,0,0) };
            b.FlatAppearance.BorderSize=0; b.Click+=(_,_)=>ShowPage(item.Item1); side.Controls.Add(b); _nav[item.Item1]=b;
        }
        root.Controls.Add(side,0,0);
        var right=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(S(22),S(16),S(14),S(12)) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Percent,100)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.Controls.Add(right,1,0);
        var header=Stack();
        foreach(var l in new[]{_state,_detail}) { l.AutoSize=true; l.Dock=DockStyle.Top; l.Tag="wrap"; l.Margin=new Padding(0,0,0,S(6)); header.Controls.Add(l); }
        _state.Font=new Font(Font,FontStyle.Bold); _state.ForeColor=ConPhoneTheme.Text;
        _detail.ForeColor=ConPhoneTheme.TextSecondary; right.Controls.Add(header,0,0);
        var host=new Panel { Dock=DockStyle.Fill }; right.Controls.Add(host,0,1);
        _notice.AutoSize=true; _notice.Dock=DockStyle.Top; _notice.ForeColor=Color.FromArgb(185,57,1); _notice.Margin=new Padding(0,S(10),0,0); _notice.Tag="wrap";
        right.SizeChanged+=(_,_)=>FitChildren(right); right.Controls.Add(_notice,0,2);
        _peers.Dock=DockStyle.Top; _peers.Height=S(114); _peers.HorizontalScrollbar=false;
        host.Controls.Add(Page("connection",
            Card("Bluetooth 연결",Label("휴대폰에서 이 PC를 페어링한 뒤 아래에서 본인 기기를 승인하세요. 광고 상태와 실제 입력 연결 상태를 따로 표시합니다."),
                Buttons(Button("광고 시작",async()=> { _serviceWanted=true; await _ble.StartAsync(); }),Button("서비스 중지",async()=> { _serviceWanted=false; long e=ForceLocal(); await ReleaseAsync(e); _ble.Stop(); }))),
            Card("입력 대상",_peers,Buttons(Button("목록 갱신",RefreshPeersAsync),Button("선택한 기기 승인",ApproveAsync,true),Button("승인 해제",async()=> { long e=ForceLocal(); await ReleaseAsync(e); _ble.Revoke(); }))),
            Card("입력 전환",Label("키를 놓으면 휴대폰 입력을 시작합니다. 복귀는 Bluetooth 응답을 기다리지 않고 즉시 처리합니다."),
                Buttons(Button("휴대폰으로 전환",EnterPhoneAsync,true),Button("노트북으로 복귀",async()=>await ReleaseAsync(ForceLocal()))),
                Label("창을 닫으면 트레이에 남습니다. 완전 종료는 트레이 메뉴를 사용하세요."))));
        Numeric(_mouse,25,300,5); Numeric(_scroll,25,300,5); Numeric(_interval,8,50); Numeric(_limit,10,200,10);
        Check(_invert,"스크롤 방향 반전"); Check(_lockEnabled,"Android Meta+L 잠금 요청 사용 · 호환성 시험");
        host.Controls.Add(Page("input",
            Card("포인터 설정",Field("마우스 감도 (%)",_mouse),Field("스크롤 감도 (%)",_scroll),Field("마우스 전송 간격 (ms)",_interval),_invert,
                Label("기본 16ms는 전송 요청 간격이며 실제 Bluetooth 지연 시간을 보장하는 수치가 아닙니다. Windows 마우스 설정은 변경하지 않습니다.")),
            Card("단축키 등록",Label("입력칸 클릭 → 실제 키 조합 입력 → 다른 곳 클릭 → 설정 저장. 등록 중에는 전역 단축키 동작이 중지됩니다. Esc는 등록 취소입니다."),
                Field("PC ↔ 휴대폰 전환",_toggle),Field("비상 노트북 복귀",_emergency),Field("잠금 요청 후 복귀",_lock),_lockEnabled,
                Label("잠금 요청은 Power 키 대신 휴대폰에 Meta+L을 전송합니다. 기기에서 무시할 수 있으며 앱은 잠금 완료를 확인할 수 없습니다. iPhone 잠금은 지원하지 않습니다."),
                Buttons(Button("설정 저장",SaveSettingsAsync,true),Button("잠금 요청 시험",async()=>await LockReturnAsync(ForceLocal(),true)),Button("입력 기본값",ResetInputAsync)))));
        Check(_textCapture,"Windows 텍스트 기록"); Check(_imageCapture,"Windows 이미지 / 캡처 기록"); Check(_persist,"앱 종료 후에도 보관 · 현재 Windows 사용자 암호화");
        _history.Dock=DockStyle.Top; _history.Height=S(154); _history.HorizontalScrollbar=false;
        _history.SelectedIndexChanged+=(_,_)=>ShowPreview();
        _preview.Multiline=true; _preview.ReadOnly=true; _preview.Dock=DockStyle.Top; _preview.Height=S(74); _preview.ScrollBars=ScrollBars.Vertical;
        _image.Dock=DockStyle.Top; _image.Height=S(120); _image.SizeMode=PictureBoxSizeMode.Zoom; _image.BackColor=ConPhoneTheme.Fill;
        _clipStatus.AutoSize=true; _clipStatus.Dock=DockStyle.Top; _clipStatus.Tag="wrap";
        host.Controls.Add(Page("clipboard",
            Card("Windows 클립보드 보관함",Label("이 PC에서 복사한 텍스트와 이미지를 기록합니다. 휴대폰 클립보드의 송수신은 지원하지 않습니다. 기본값은 현재 실행 세션만 보관합니다."),_clipStatus,
                Buttons(Button("현재 항목 가져오기",()=>_clipboard.CaptureCurrentAsync()),Button("목록 새로고침",()=> { RefreshHistory(); return Task.CompletedTask; })),
                _history,_preview,_image,
                Buttons(Button("선택 항목 복원",RestoreClipboardAsync,true),Button("선택 삭제",DeleteClipboardAsync),Button("기록 비우기",ClearClipboardAsync))),
            Card("기록 범위",_textCapture,_imageCapture,_persist,Field("최대 보관 개수",_limit),Label("민감 정보 제외 표시를 존중합니다. 항목당 최대 8MB, 전체 32MB. 0.2.0의 기존 평문 기록 파일은 자동으로 읽거나 삭제하지 않습니다."),
                Button("클립보드 설정 저장",SaveSettingsAsync,true))));
        foreach(var box in new[]{_log,_diagnosis}) { box.Multiline=true; box.ReadOnly=true; box.ScrollBars=ScrollBars.Vertical; box.Dock=DockStyle.Top; box.BackColor=Color.White; }
        _log.Height=S(230); _diagnosis.Height=S(110);
        host.Controls.Add(Page("diagnostics",Card("진단 · 입력 내용 제외",Label("광고 전환, 실제 링크 해제, HID 일시 중지, 전송 오류를 구분해서 기록합니다. 클립보드 본문이나 입력한 키는 진단에 포함하지 않습니다."),_diagnosis,_log,
            Buttons(Button("Bluetooth 검사",async()=>_diagnosis.Text=await BleHidServer.DiagnoseAsync()),Button("진단 저장",SaveDiagnosticsAsync,true),Button("로그 지우기",()=> { _log.Clear(); return Task.CompletedTask; })))));
        ShowPage("connection"); UpdateStatus();
    }
    internal void ShowPage(string key)
    {
        FinishRecorders(); _currentPage=key;
        foreach(var p in _pages) p.Value.Visible=p.Key==key;
        foreach(var n in _nav) { n.Value.BackColor=n.Key==key?ConPhoneTheme.BrandWeak:Color.White; n.Value.ForeColor=n.Key==key?Color.FromArgb(185,57,1):ConPhoneTheme.TextSecondary; }
        if(_pages.TryGetValue(key,out var page)) { page.BringToFront(); page.PerformLayout(); }
    }
    private void FinishRecorders() { _toggle.FinishRecording(); _emergency.FinishRecording(); _lock.FinishRecording(); }
    private void LoadControls()
    {
        _mouse.Value=_settings.MouseSpeedPercent; _scroll.Value=_settings.ScrollSpeedPercent; _interval.Value=_settings.MouseReportIntervalMs; _invert.Checked=_settings.InvertScroll;
        _toggle.Binding=_settings.ToggleHotkey; _emergency.Binding=_settings.EmergencyHotkey; _lock.Binding=_settings.LockAndReturnHotkey; _lockEnabled.Checked=_settings.EnablePhoneLock;
        _limit.Value=_settings.ClipboardHistoryLimit; _textCapture.Checked=_settings.CaptureClipboardText; _imageCapture.Checked=_settings.CaptureClipboardImages; _persist.Checked=_settings.PersistClipboardHistory;
    }
    private async Task SaveSettingsAsync()
    {
        FinishRecorders(); var candidate=_settings.Clone();
        candidate.MouseSpeedPercent=(int)_mouse.Value; candidate.ScrollSpeedPercent=(int)_scroll.Value; candidate.MouseReportIntervalMs=(int)_interval.Value; candidate.InvertScroll=_invert.Checked;
        candidate.ToggleHotkey=_toggle.Binding; candidate.EmergencyHotkey=_emergency.Binding; candidate.LockAndReturnHotkey=_lock.Binding; candidate.EnablePhoneLock=_lockEnabled.Checked;
        candidate.ClipboardHistoryLimit=(int)_limit.Value; candidate.CaptureClipboardText=_textCapture.Checked; candidate.CaptureClipboardImages=_imageCapture.Checked; candidate.PersistClipboardHistory=_persist.Checked;
        candidate.Validate(); SettingsStore.Save(candidate); // Commit only after validation and successful atomic save.
        _settings=candidate; _input.Configure(candidate); _scaler.Configure(candidate.MouseSpeedPercent,candidate.ScrollSpeedPercent,candidate.InvertScroll);
        await _clipboard.ConfigureAsync(candidate); Notice("설정을 저장하고 적용했습니다."); UpdateStatus();
    }
    internal Task TestSaveSettingsAsync()=>SaveSettingsAsync();
    private async Task ResetInputAsync()
    {
        _mouse.Value=100; _scroll.Value=100; _interval.Value=16; _invert.Checked=false;
        _toggle.Binding=new() { Key=0x77 }; _emergency.Binding=new() { Key=0x78 }; _lock.Binding=new() { Key=0x4C, Modifiers=HotkeyModifiers.Control|HotkeyModifiers.Alt }; _lockEnabled.Checked=false;
        await SaveSettingsAsync();
    }
    private void UpdateStatus()
    {
        _state.Text=_input.IsRemote?"휴대폰 조작 중":_ble.Ready?"노트북 조작 중 · 휴대폰 입력 준비됨":"노트북 조작 중 · 휴대폰 입력 대기";
        _detail.Text=$"전환 {HotkeyCatalog.Display(_settings.ToggleHotkey)} · 복귀 {HotkeyCatalog.Display(_settings.EmergencyHotkey)}\n광고 {_ble.Advertisement} · HID K:{_ble.KeyboardSubscribers} M:{_ble.MouseSubscribers} · 전송 {_ble.LastSendMs}ms · 대기 {_outbox.Count}";
        _tray.Text=_input.IsRemote?"Con.Phone · 휴대폰 입력":"Con.Phone · 노트북 입력";
        _clipStatus.Text=_clipboard.Status;
    }
    private async Task ActionAsync(Func<Task> action)
    {
        try { await _actions.WaitAsync(_lifetime.Token); }
        catch(OperationCanceledException) { return; }
        try { if(!_closing) await action(); } catch(Exception ex) { HandleError(ex); } finally { _actions.Release(); }
    }
    private void Post(Action action)
    {
        if(_closed || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch(InvalidOperationException) { }
    }
    private void Log(string text)
    {
        if(InvokeRequired) { Post(()=>Log(text)); return; }
        if(_log.TextLength>18000) _log.Text=_log.Text[^9000..];
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\r\n");
    }
    private void Notice(string text) { if(InvokeRequired) { Post(()=>Notice(text)); return; } _notice.Text=text; Log(text); }
    internal void HandleError(Exception ex) { ForceLocal(); _lastError=$"{ex.GetType().Name}:0x{ex.HResult:X8}"; Notice($"{ex.Message} ({_lastError})"); }
    private long ForceLocal()
    {
        _input.Deactivate(); long epoch=Interlocked.Increment(ref _epoch); _outbox.Clear(); _scaler.Reset(); return epoch;
    }
    private async Task<bool> ReleaseAsync(long epoch)
    {
        if(!_ble.Ready) return false;
        using var cts=new CancellationTokenSource(1000);
        try
        {
            bool k=await _ble.SendAsync(new(true,new byte[8]),()=>epoch==Volatile.Read(ref _epoch),cts.Token);
            bool m=await _ble.SendAsync(new(false,new byte[6]),()=>epoch==Volatile.Read(ref _epoch),cts.Token);
            return k&&m;
        }
        catch(Exception ex) { _lastError=$"Release:{ex.GetType().Name}:0x{ex.HResult:X8}"; Post(()=>Notice("PC 입력은 복구됐지만 휴대폰 키 해제를 확인하지 못했습니다. 휴대폰에 입력이 남으면 Bluetooth 연결을 해제하세요.")); return false; }
    }
    private async Task EnterPhoneAsync()
    {
        if(_input.IsRemote) { await ReleaseAsync(ForceLocal()); return; }
        if(Interlocked.Exchange(ref _entering,1)!=0) { ForceLocal(); return; }
        try
        {
            FinishRecorders();
            if(!_ble.Ready) { Notice("본인 기기 승인과 키보드·마우스 구독을 먼저 확인하세요."); return; }
            long ticket=Volatile.Read(ref _epoch);
            if(!await ReleaseAsync(ticket)) return;
            var deadline=Stopwatch.StartNew();
            while(deadline.ElapsedMilliseconds<2000 && ticket==Volatile.Read(ref _epoch) && !_closing)
            {
                Volatile.Write(ref _remoteEpoch,ticket);
                if(_input.Activate())
                {
                    if(ticket!=Volatile.Read(ref _epoch)) _input.Deactivate();
                    else Notice("휴대폰 입력을 시작했습니다.");
                    return;
                }
                await Task.Delay(20);
            }
            Notice("전환이 취소됐거나 키가 눌려 있습니다. 모든 키를 놓고 다시 시도하세요.");
        }
        catch(Exception ex) { HandleError(ex); }
        finally { Interlocked.Exchange(ref _entering,0); }
    }
    private async Task LockReturnAsync(long ticket,bool explicitTest=false)
    {
        if(!explicitTest && !_settings.EnablePhoneLock) { Notice("잠금 요청은 비활성화되어 있습니다. PC 입력으로 복귀했습니다."); return; }
        if(!_ble.Ready) { Notice("잠금 요청을 보낼 연결이 없습니다. PC 입력으로 복귀했습니다."); return; }
        using var timeout=new CancellationTokenSource(1500);
        bool sent=false;
        try
        {
            if(!await ReleaseAsync(ticket)) return;
            // Android framework's Meta+L handler; HID Keyboard report stays the same as 0.1.6.
            sent=await _ble.SendAsync(new(true,new byte[]{8,0,0x0F,0,0,0,0,0}),()=>ticket==Volatile.Read(ref _epoch),timeout.Token);
            await Task.Delay(45,timeout.Token);
        }
        catch(Exception ex) { _lastError=$"Lock:{ex.GetType().Name}:0x{ex.HResult:X8}"; }
        finally { await ReleaseAsync(ticket); }
        Notice(sent?"Meta+L 잠금 요청을 전송했습니다. PC로 복귀했으며 휴대폰의 실제 잠금 완료는 확인할 수 없습니다.":"잠금 요청 전송을 확인하지 못했습니다. PC 입력으로는 복귀했습니다.");
    }
    private async Task ApproveAsync()
    {
        long ticket=ForceLocal(); await ReleaseAsync(ticket);
        if(_peers.SelectedItem is not Peer peer) { Notice("연결 목록에서 본인 휴대폰을 선택하세요."); return; }
        if(MessageBox.Show(this,$"입력을 보낼 본인 휴대폰입니까?\n{peer.Name}","기기 승인",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes) return;
        await _ble.ApproveAsync(peer.Id); Notice("기기를 승인했습니다. 전환 단축키를 누르세요.");
    }
    private async Task RefreshPeersAsync()
    {
        if(_refreshing || _test) return; _refreshing=true;
        try
        {
            var selected=(_peers.SelectedItem as Peer)?.Id;
            var peers=await _ble.GetPeersAsync();
            _peers.BeginUpdate();
            try { _peers.Items.Clear(); foreach(var p in peers) _peers.Items.Add(p); for(int i=0;i<_peers.Items.Count;i++) if((_peers.Items[i] as Peer)?.Id==selected) _peers.SelectedIndex=i; if(_peers.SelectedIndex<0 && _peers.Items.Count==1) _peers.SelectedIndex=0; }
            finally { _peers.EndUpdate(); }
        }
        finally { _refreshing=false; }
    }
    private async Task PumpAsync()
    {
        long lastMouse=0;
        while(!_lifetime.IsCancellationRequested)
        {
            Envelope? envelope=null;
            try
            {
                envelope=await _outbox.TakeAsync(_lifetime.Token);
                if(envelope.Epoch!=Volatile.Read(ref _epoch) || !_input.IsRemote) continue;
                if(envelope.Packet is PointerPacket)
                {
                    double elapsed=Stopwatch.GetElapsedTime(lastMouse).TotalMilliseconds;
                    int delay=_settings.MouseReportIntervalMs-(int)Math.Min(int.MaxValue,elapsed);
                    if(delay>0) await Task.Delay(delay,_lifetime.Token);
                }
                foreach(var report in Hid.Encode(envelope.Packet))
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(1000);
                    bool sent=await _ble.SendAsync(report,()=>envelope.Epoch==Volatile.Read(ref _epoch) && _input.IsRemote,timeout.Token);
                    if(!sent && envelope.Epoch==Volatile.Read(ref _epoch) && _input.IsRemote) throw new InvalidOperationException("입력 연결이 준비되지 않았습니다.");
                }
                if(envelope.Packet is PointerPacket) lastMouse=Stopwatch.GetTimestamp();
            }
            catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { break; }
            catch(Exception ex)
            {
                // Late failure from an older epoch must not cancel a new input session.
                if(envelope?.Epoch!=Volatile.Read(ref _epoch)) continue;
                long e=ForceLocal(); _lastError=$"Send:{ex.GetType().Name}:0x{ex.HResult:X8}";
                Post(async()=> { await ReleaseAsync(e); Notice("전송 오류로 PC에 복귀했습니다. GATT 서비스는 유지합니다. 진단 로그를 확인하세요."); });
            }
        }
    }
    private void RefreshHistory()
    {
        string? id=(_history.SelectedItem as ClipboardEntry)?.Id;
        _history.BeginUpdate();
        try { _history.Items.Clear(); foreach(var e in _clipboard.Entries) _history.Items.Add(e); for(int i=0;i<_history.Items.Count;i++) if((_history.Items[i] as ClipboardEntry)?.Id==id) _history.SelectedIndex=i; if(_history.SelectedIndex<0 && _history.Items.Count>0) _history.SelectedIndex=0; }
        finally { _history.EndUpdate(); }
        ShowPreview();
    }
    private void ShowPreview()
    {
        var old=_image.Image; _image.Image=null; old?.Dispose();
        var entry=_history.SelectedItem as ClipboardEntry;
        _preview.Text=entry?.Kind==ClipboardEntryKind.Text ? entry.Text : entry?.Preview ?? "기록된 항목이 없습니다.";
        _image.Visible=entry?.Png is not null;
        if(entry?.Png is { } png) { using var ms=new MemoryStream(png); using var bitmap=Image.FromStream(ms); _image.Image=new Bitmap(bitmap); }
    }
    private async Task RestoreClipboardAsync()
    {
        if(_history.SelectedItem is not ClipboardEntry entry) { Notice("복원할 항목을 선택하세요."); return; }
        bool restored=await _clipboard.RestoreAsync(entry.Id); Notice(restored?"Windows 클립보드로 복원했습니다. 대상 프로그램에서 Ctrl+V를 누르세요.":"클립보드 복원에 실패했습니다.");
    }
    private async Task DeleteClipboardAsync() { if(_history.SelectedItem is ClipboardEntry e) await _clipboard.DeleteAsync(e.Id); }
    private async Task ClearClipboardAsync() { if(MessageBox.Show(this,"현재 보관함의 기록을 모두 지울까요?","기록 비우기",MessageBoxButtons.YesNo)==DialogResult.Yes) await _clipboard.ClearAsync(); }
    private async Task SaveDiagnosticsAsync()
    {
        using var dialog=new SaveFileDialog { Filter="JSON (*.json)|*.json",FileName="ConPhone-diagnostics-0.2.1.json" };
        if(dialog.ShowDialog(this)!=DialogResult.OK) return;
        var result=new { Version="0.2.1-rc",OS=Environment.OSVersion.ToString(),Advertisement=_ble.Advertisement,Ready=_ble.Ready,K=_ble.KeyboardSubscribers,M=_ble.MouseSubscribers,Sent=_ble.Sent,Failures=_ble.Failures,LatencyMs=_ble.LastSendMs,NativePending=_ble.NativeWritePending,Queue=_outbox.Count,LastError=_lastError,ClipboardStatus=_clipboard.Status,ClipboardCount=_clipboard.Entries.Count,Events=_log.Text,HardwareValidated=false };
        await File.WriteAllTextAsync(dialog.FileName,JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true })); Notice("진단을 저장했습니다. 클립보드 본문과 입력 키는 제외했습니다.");
    }
    private void PowerChanged(object? sender,PowerModeChangedEventArgs e)
    {
        if(e.Mode==PowerModes.Suspend) { ForceLocal(); _ble.Revoke(); }
        if(e.Mode==PowerModes.Resume && _serviceWanted) Post(async()=> { try { await _ble.StartAsync(); } catch(Exception ex) { HandleError(ex); } });
    }
    private void SessionChanged(object? sender,SessionSwitchEventArgs e)
    {
        if(e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff) { ForceLocal(); _ble.Revoke(); }
    }
    internal void TestExit() { _exitRequested=true; Close(); }
    private async void ClosingAsync(object? sender,FormClosingEventArgs e)
    {
        if(_closed) return;
        if(!_test && !_exitRequested && e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; FinishRecorders(); Hide(); return; }
        e.Cancel=true; if(_closing) return; _closing=true; _timer.Stop(); FinishRecorders();
        await ReleaseAsync(ForceLocal()); _lifetime.Cancel();
        if(_pump is not null) { try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)); } catch(Exception) { } }
        _input.Dispose(); _clipboard.Dispose(); _ble.Dispose();
        if(!_test) { SystemEvents.PowerModeChanged-=PowerChanged; SystemEvents.SessionSwitch-=SessionChanged; }
        _image.Image?.Dispose(); _tray.Visible=false; _tray.Dispose(); _timer.Dispose(); _closed=true; Close();
    }
}
