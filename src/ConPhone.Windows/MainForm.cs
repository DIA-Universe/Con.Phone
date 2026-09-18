using System.Text.Json;
using ConPhone.Core;
using Microsoft.Win32;

namespace ConPhone.Windows;

public sealed class MainForm : Form
{
    private readonly BleHidServer _ble = new();
    private readonly InputCapture _input = new();
    private readonly Outbox _outbox = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly Label _state = new() { AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = Color.FromArgb(25, 75, 125) };
    private readonly TextBox _diagnosis = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 810, Height = 110 };
    private readonly ListBox _peers = new() { Width = 810, Height = 86 };
    private readonly TextBox _events = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 810, Height = 100 };
    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly NotifyIcon _tray = new() { Text = "Con.Phone · 로컬 입력", Icon = SystemIcons.Application, Visible = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    private Task? _pump;
    private long _epoch, _sent;
    private bool _closing, _closed;
    private string _lastError = "None";

    public MainForm()
    {
        Text = "Con.Phone 0.1.1 · 동글 없는 무선 입력";
        Width = 880; Height = 820; MinimumSize = new Size(780, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 248, 252);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(20) };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Con.Phone", Font = new Font("Segoe UI", 25, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(new Label { Text = "내장 Bluetooth → 갤럭시 / iPhone · 추가 동글 / 서버 / 계정 없음", AutoSize = true });
        layout.Controls.Add(new Label { Text = "실행하면 Bluetooth 검색을 자동 시작합니다. Android·iPhone 모두 실기기 검증 전이며 연결 성공과 입력 성공은 별개입니다.", AutoSize = true, ForeColor = Color.DarkRed, Margin = new Padding(3, 10, 3, 10) });
        _profile.Items.AddRange(["갤럭시 / Android", "iPhone (실기기 검증 전)"]); _profile.SelectedIndex = 0;
        layout.Controls.Add(_profile);
        layout.Controls.Add(_state);
        layout.Controls.Add(Row(
            Button("Bluetooth 다시 검사", async () => _diagnosis.Text = await BleHidServer.DiagnoseAsync()),
            Button("Bluetooth 검색 다시 시작", async () => { await _ble.StartAsync(); Log("휴대폰 Bluetooth 설정에서 이 PC를 검색하고 양쪽 페어링을 확인하세요."); }),
            Button("서비스 중지", async () => { await ReturnLocalAsync(); _ble.Stop(); })));
        layout.Controls.Add(_diagnosis);
        layout.Controls.Add(new Label { Text = "휴대폰에서 페어링 → 대상 확인 → 승인", AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
        layout.Controls.Add(_peers);
        layout.Controls.Add(Row(
            Button("연결 목록 갱신", RefreshPeersAsync),
            Button("선택한 기기 승인", async () =>
            {
                await ReturnLocalAsync();
                if (_peers.SelectedItem is not Peer peer) throw new InvalidOperationException("기기를 먼저 선택해 주세요.");
                if (MessageBox.Show(this, $"본인 휴대폰이 맞습니까?\r\n{peer}\r\nWindows와 휴대폰의 페어링 확인도 직접 확인하세요.", "입력 대상 승인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                await _ble.ApproveAsync(peer.Id); Log("기기 승인 완료. 누른 키를 모두 놓고 F8로 입력을 전환하세요.");
            }),
            Button("승인 해제", async () => { await ReturnLocalAsync(); _ble.Revoke(); })));
        layout.Controls.Add(Row(Button("입력 전환 · F8", ToggleAsync), Button("노트북 복귀 · F9", ReturnLocalAsync), Button("진단 저장", SaveDiagnosticsAsync)));
        layout.Controls.Add(new Label { Text = "F8: 전환 / F9: 비상 복귀 (앱 실행 중 예약 키)\r\n휴대폰의 물리 키보드 설정에서 한국어 배열을 선택하세요. 한영 상태는 추측하지 않습니다.\r\niPhone: 설정 → 손쉬운 사용 → 터치 → AssistiveTouch. 앱/OS별 동작 차이는 실기기 시험이 필요합니다.", AutoSize = true, MaximumSize = new Size(810, 0), Margin = new Padding(3, 10, 3, 10) });
        layout.Controls.Add(_events);
        var menu = new ContextMenuStrip();
        menu.Items.Add("창 열기", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("노트북 복귀", null, (_, _) => Dispatch(ReturnLocalAsync));
        menu.Items.Add("종료", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        _input.Packet += packet =>
        {
            if (_input.IsRemote && !_outbox.TryAdd(new(Volatile.Read(ref _epoch), packet)))
            { ImmediateLocal(); Dispatch(async () => { await ReleaseAsync(); Log("입력 대기열 초과 — 로컬 복귀"); }); }
        };
        _input.Toggle += () => Dispatch(ToggleAsync);
        _input.Emergency += () => { ImmediateLocal(); Dispatch(ReturnLocalAsync); };
        _input.Fault += text => { ImmediateLocal(); Dispatch(async () => { await ReleaseAsync(); Log(text); }); };
        _ble.Status += text => Post(() => Log(text));
        _ble.Changed += () =>
        {
            if (_input.IsRemote && !_ble.Ready) { ImmediateLocal(); Post(() => Log("연결/구독 상태 변경 — 로컬 복귀. 자동 재개하지 않습니다.")); }
        };
        _timer.Tick += (_, _) =>
        {
            if (_input.IsRemote && !_ble.Ready) ImmediateLocal();
            _state.Text = _input.IsRemote ? "휴대폰 조작 중 · F9로 즉시 복귀" : $"노트북 조작 중 · 광고:{_ble.Advertisement} · 승인:{(_ble.Ready ? "준비됨" : "대기")}";
            _tray.Text = _input.IsRemote ? "Con.Phone · 휴대폰 입력 / F9 복귀" : "Con.Phone · 노트북 입력";
        };
        _timer.Start();
        Shown += (_, _) => Dispatch(async () =>
        {
            await _input.Started;
            _diagnosis.Text = await BleHidServer.DiagnoseAsync();
            await _ble.StartAsync();
            Log("Bluetooth 검색을 자동 시작했습니다. 휴대폰 Bluetooth 설정에서 이 PC를 찾아 페어링하세요.");
        });
        _pump = Task.Run(PumpAsync);
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        FormClosing += ClosingAsync;
    }
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(820, 0) };
        row.Controls.AddRange(controls); return row;
    }
    private Button Button(string text, Func<Task> action)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(9, 5, 9, 5), FlatStyle = FlatStyle.Flat, BackColor = Color.White };
        b.Click += (_, _) => Dispatch(action); return b;
    }
    private void Post(Action action)
    {
        if (_closed || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private void Dispatch(Func<Task> action) => Post(async () =>
    {
        if (!await _actions.WaitAsync(0)) return;
        try { if (!_closing) await action(); }
        catch (Exception ex) { ImmediateLocal(); _lastError = $"{ex.GetType().Name}:0x{ex.HResult:X8}"; Log($"{ex.Message} ({_lastError})"); }
        finally { _actions.Release(); }
    });
    private void Log(string text)
    {
        if (_events.TextLength > 10000) _events.Clear();
        _events.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\r\n");
    }
    private void ImmediateLocal()
    {
        _input.Deactivate();
        Interlocked.Increment(ref _epoch);
        _outbox.Clear();
    }
    private async Task ReleaseAsync()
    {
        if (!_ble.Ready) return;
        long epoch = Volatile.Read(ref _epoch);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        try
        {
            await _ble.SendAsync(new(true, new byte[8]), () => epoch == Volatile.Read(ref _epoch), timeout.Token);
            await _ble.SendAsync(new(false, new byte[5]), () => epoch == Volatile.Read(ref _epoch), timeout.Token);
        }
        catch (Exception ex)
        {
            _ble.Stop(); _lastError = $"Release:{ex.GetType().Name}";
            Log("해제 보고서 전달을 확인하지 못했습니다. 로컬은 복구됐으며, 휴대폰 입력이 남으면 Bluetooth 연결을 끊어 주세요.");
        }
    }
    private async Task ReturnLocalAsync() { ImmediateLocal(); await ReleaseAsync(); }
    private async Task ToggleAsync()
    {
        if (_input.IsRemote) { await ReturnLocalAsync(); return; }
        if (!_ble.Ready) throw new InvalidOperationException("키보드·마우스 구독과 기기 승인을 먼저 완료하세요.");
        ImmediateLocal();
        long epoch = Volatile.Read(ref _epoch);
        await ReleaseAsync();
        if (epoch != Volatile.Read(ref _epoch) || !_ble.Ready) return;
        _input.Activate();
        Log("휴대폰 입력 시작. F9로 비상 복귀할 수 있습니다.");
    }
    private async Task RefreshPeersAsync()
    {
        var peers = await _ble.GetPeersAsync();
        _peers.Items.Clear(); foreach (var peer in peers) _peers.Items.Add(peer);
        if (peers.Count == 1 && _peers.SelectedIndex < 0) _peers.SelectedIndex = 0;
        if (peers.Count == 0) Log("HID 구독 기기가 없습니다. Bluetooth 페어링만 됐거나 휴대폰이 HID로 인식하지 못했을 수 있습니다.");
    }
    private async Task PumpAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var envelope = await _outbox.TakeAsync(_lifetime.Token);
                foreach (var report in Hid.Encode(envelope.Packet))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(750);
                    await _ble.SendAsync(report, () => envelope.Epoch == Volatile.Read(ref _epoch) && _input.IsRemote, timeout.Token);
                    Interlocked.Increment(ref _sent);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                ImmediateLocal(); _ble.Stop();
                _lastError = $"Send:{ex.GetType().Name}:0x{ex.HResult:X8}";
                Post(() => Log("전송 실패/지연으로 로컬 복귀했습니다. 휴대폰 연결을 해제한 뒤 서비스를 다시 시작하세요."));
            }
        }
    }
    private async Task SaveDiagnosticsAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "ConPhone-diagnostics.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var report = new
        {
            Version = "0.1.1", OS = Environment.OSVersion.Version.ToString(), Runtime = Environment.Version.ToString(),
            TargetProfile = _profile.SelectedIndex == 0 ? "Android" : "iPhone", Bluetooth = await BleHidServer.DiagnoseAsync(),
            Advertisement = _ble.Advertisement, ApprovedAndSubscribed = _ble.Ready,
            SendOperations = Interlocked.Read(ref _sent), Pending = _outbox.Count, LastError = _lastError,
            AndroidHardwareTest = "Not performed by build environment", IPhoneHardwareTest = "Not performed",
            Privacy = "No text, keys, pairing codes, device names or Bluetooth addresses are exported."
        };
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Log("진단 저장 완료. 입력 문자/키코드/기기 주소는 저장하지 않았습니다.");
    }
    private void PowerChanged(object? sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Suspend) { ImmediateLocal(); _ble.Stop(); } }
    private void SessionChanged(object? sender, SessionSwitchEventArgs e)
    { if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff) { ImmediateLocal(); _ble.Stop(); } }
    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        await ReturnLocalAsync();
        _lifetime.Cancel(); _ble.Stop(); _input.Dispose();
        if (_pump is not null) { try { await _pump; } catch (OperationCanceledException) { } }
        SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged;
        _timer.Stop(); _timer.Dispose(); _tray.Visible = false; _tray.Dispose();
        _closed = true; Close();
    }
}
