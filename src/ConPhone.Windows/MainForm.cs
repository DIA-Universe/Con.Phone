using System.Text.Json;
using ConPhone.Core;
using Microsoft.Win32;

namespace ConPhone.Windows;

public sealed class MainForm : Form
{
    private readonly BleHidServer _ble = new();
    private readonly InputCapture _input = new();
    private readonly Outbox _outbox = new();
    private readonly PointerScaler _pointerScaler = new();
    private readonly ConPhoneSettings _settings = SettingsStore.Load();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly Label _state = new() { AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = Color.FromArgb(25, 75, 125) };
    private readonly TextBox _diagnosis = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 810, Height = 110 };
    private readonly ListBox _peers = new() { Width = 810, Height = 86 };
    private readonly TextBox _events = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 810, Height = 100 };
    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly TrackBar _mouseSpeed = new() { Minimum = 25, Maximum = 300, TickFrequency = 25, SmallChange = 5, LargeChange = 25, Width = 430 };
    private readonly TrackBar _scrollSpeed = new() { Minimum = 25, Maximum = 300, TickFrequency = 25, SmallChange = 5, LargeChange = 25, Width = 430 };
    private readonly Label _mouseSpeedValue = new() { AutoSize = true, Width = 70 };
    private readonly Label _scrollSpeedValue = new() { AutoSize = true, Width = 70 };
    private readonly CheckBox _invertScroll = new() { Text = "스크롤 방향 반전", AutoSize = true };
    private readonly ComboBox _toggleKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _emergencyKey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Label _shortcutSummary = new() { AutoSize = true, ForeColor = Color.FromArgb(35, 90, 145) };
    private readonly NotifyIcon _tray = new() { Text = "Con.Phone · 로컬 입력", Icon = SystemIcons.Application, Visible = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer _peerTimer = new() { Interval = 1500 };
    private Task? _pump;
    private long _epoch, _sent;
    private bool _closing, _closed, _exitRequested;
    private string _lastError = "None";

    public MainForm()
    {
        Text = "Con.Phone 0.1.5 · 동글 없는 무선 입력";
        Width = 880; Height = 820; MinimumSize = new Size(780, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 248, 252);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(20) };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Con.Phone", Font = new Font("Segoe UI", 25, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(new Label { Text = "내장 Bluetooth → 갤럭시 / iPhone · 추가 동글 / 서버 / 계정 없음", AutoSize = true });
        layout.Controls.Add(new Label { Text = "실행하면 Bluetooth HID 광고를 자동 시작합니다. 휴대폰의 기존 PC 항목이 \'연결할 수 없음\'이면 그 등록을 삭제한 뒤, Con.Phone을 켜 둔 상태에서 새로 페어링하세요. 창의 X는 종료가 아니라 트레이로 숨깁니다.", AutoSize = true, ForeColor = Color.DarkRed, MaximumSize = new Size(810, 0), Margin = new Padding(3, 10, 3, 10) });
        _profile.Items.AddRange(["갤럭시 / Android", "iPhone (실기기 검증 전)"]); _profile.SelectedIndex = 0;
        layout.Controls.Add(_profile);
        layout.Controls.Add(_state);
        layout.Controls.Add(Row(
            Button("Bluetooth 다시 검사", async () => _diagnosis.Text = await BleHidServer.DiagnoseAsync()),
            Button("Bluetooth 광고 확인/시작", async () => { await _ble.StartAsync(); Log("광고 상태 확인 완료. 기존 등록 기기가 연결되지 않으면 휴대폰에서 삭제 후 새로 페어링하세요."); }),
            Button("서비스 중지", async () => { await ReturnLocalAsync(); _ble.Stop(); })));
        layout.Controls.Add(_diagnosis);
        layout.Controls.Add(new Label { Text = "휴대폰: 기존 PC 등록이 실패하면 삭제 → 새 기기 검색 → 이 PC 페어링 → 아래 대상 승인", AutoSize = true, MaximumSize = new Size(810, 0), Margin = new Padding(3, 12, 3, 3) });
        layout.Controls.Add(_peers);
        layout.Controls.Add(Row(
            Button("연결 목록 갱신", () => RefreshPeersAsync(false)),
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
        menu.Items.Add("종료", null, (_, _) => { _exitRequested = true; Close(); });
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
            _state.Text = _input.IsRemote ? "휴대폰 조작 중 · F9로 즉시 복귀" : $"노트북 조작 중 · 광고:{_ble.Advertisement} · HID구독 K:{_ble.KeyboardSubscribers} M:{_ble.MouseSubscribers} · 승인:{(_ble.Ready ? "준비됨" : "대기")}";
            _tray.Text = _input.IsRemote ? "Con.Phone · 휴대폰 입력 / F9 복귀" : "Con.Phone · 노트북 입력";
        };
        _timer.Start();
        _peerTimer.Tick += (_, _) =>
        {
            if (_ble.Advertisement is "Started" or "StartedWithoutAllAdvertisementData")
                Dispatch(() => RefreshPeersAsync(true));
        };
        _peerTimer.Start();
        Shown += (_, _) => Dispatch(async () =>
        {
            await _input.Started;
            _diagnosis.Text = await BleHidServer.DiagnoseAsync();
            await _ble.StartAsync();
            Log("Bluetooth HID 광고를 자동 시작했습니다. 이전 등록이 연결 실패하면 휴대폰에서 삭제 후 이 PC를 새로 검색해 페어링하세요.");
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
        catch (Exception ex)
        {
            ImmediateLocal();
            _lastError = $"{ex.GetType().Name}:0x{ex.HResult:X8}";
            var ble = ex.HResult == unchecked((int)0x8000000E)
                ? $" · BLE:{_ble.LastOperation} · 광고:{_ble.Advertisement}"
                : "";
            Log($"{ex.Message} ({_lastError}{ble})");
        }
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
            await _ble.SendAsync(new(false, new byte[Hid.MouseReportLength]), () => epoch == Volatile.Read(ref _epoch), timeout.Token);
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
    private async Task RefreshPeersAsync(bool silent = false)
    {
        var selectedId = (_peers.SelectedItem as Peer)?.Id;
        var peers = await _ble.GetPeersAsync();

        _peers.BeginUpdate();
        try
        {
            _peers.Items.Clear();
            foreach (var peer in peers) _peers.Items.Add(peer);

            if (selectedId is not null)
            {
                for (var i = 0; i < _peers.Items.Count; i++)
                {
                    if (_peers.Items[i] is Peer peer && peer.Id == selectedId)
                    {
                        _peers.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (_peers.SelectedIndex < 0 && peers.Count == 1)
                _peers.SelectedIndex = 0;
        }
        finally
        {
            _peers.EndUpdate();
        }

        if (!silent && peers.Count == 0)
            Log("HID 구독 기기가 없습니다. Bluetooth 페어링만 됐거나 휴대폰이 HID로 인식하지 못했을 수 있습니다.");
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
            Version = "0.1.5", OS = Environment.OSVersion.Version.ToString(), Runtime = Environment.Version.ToString(),
            TargetProfile = _profile.SelectedIndex == 0 ? "Android" : "iPhone", Bluetooth = await BleHidServer.DiagnoseAsync(),
            Advertisement = _ble.Advertisement, BleLastOperation = _ble.LastOperation, KeyboardSubscribers = _ble.KeyboardSubscribers, MouseSubscribers = _ble.MouseSubscribers, ApprovedAndSubscribed = _ble.Ready,
            SendOperations = Interlocked.Read(ref _sent), Pending = _outbox.Count, LastError = _lastError,
            AndroidHardwareTest = "Not performed by build environment", IPhoneHardwareTest = "Not performed",
            Privacy = "No text, keys, pairing codes, device names or Bluetooth addresses are exported."
        };
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Log("진단 저장 완료. 입력 문자/키코드/기기 주소는 저장하지 않았습니다.");
    }
    private void PowerChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            ImmediateLocal();
            _ble.Revoke();
            _ble.Stop();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Dispatch(async () =>
            {
                await _ble.StartAsync();
                Log("절전 모드에서 복귀해 Bluetooth HID 광고를 다시 확인했습니다.");
            });
        }
    }
    private void SessionChanged(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            ImmediateLocal();
            _ble.Revoke();
            Log("Windows 잠금 감지 — 입력 대상 승인을 해제했지만 Bluetooth HID 광고는 유지합니다.");
        }
        else if (e.Reason == SessionSwitchReason.SessionLogoff)
        {
            ImmediateLocal();
            _ble.Stop();
        }
    }
    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2500, "Con.Phone 계속 실행 중", "Bluetooth HID 연결을 유지하려고 트레이에서 계속 실행합니다. 완전 종료는 트레이 메뉴의 '종료'를 사용하세요.", ToolTipIcon.Info);
            return;
        }

        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        await ReturnLocalAsync();
        _lifetime.Cancel(); _ble.Stop(); _input.Dispose();
        if (_pump is not null) { try { await _pump; } catch (OperationCanceledException) { } }
        SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged;
        _timer.Stop(); _timer.Dispose(); _peerTimer.Stop(); _peerTimer.Dispose(); _tray.Visible = false; _tray.Dispose();
        _closed = true; Close();
    }
}
