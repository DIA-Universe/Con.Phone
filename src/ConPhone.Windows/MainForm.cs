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
    private readonly TrackBar _mouseSpeed = new() { Minimum = 25, Maximum = 300, TickFrequency = 25, SmallChange = 5, LargeChange = 25, Width = 420 };
    private readonly TrackBar _scrollSpeed = new() { Minimum = 25, Maximum = 300, TickFrequency = 25, SmallChange = 5, LargeChange = 25, Width = 420 };
    private readonly Label _mouseSpeedValue = new() { AutoSize = true, Width = 70 };
    private readonly Label _scrollSpeedValue = new() { AutoSize = true, Width = 70 };
    private readonly CheckBox _invertScroll = new() { Text = "스크롤 방향 반전", AutoSize = true };
    private readonly HotkeyCaptureBox _toggleKey = new();
    private readonly HotkeyCaptureBox _emergencyKey = new();
    private readonly HotkeyCaptureBox _lockAndReturnKey = new();
    private readonly Label _shortcutSummary = new() { AutoSize = true, ForeColor = ConPhoneTheme.TextSecondary };
    private readonly ListBox _clipboardList = new() { Width = 650, Height = 250, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _clipboardDetail = new() { AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = ConPhoneTheme.TextSecondary };
    private readonly NumericUpDown _clipboardLimit = new() { Minimum = 10, Maximum = 200, Increment = 10, Width = 90 };
    private readonly CheckBox _captureClipboardText = new() { Text = "텍스트 저장", AutoSize = true };
    private readonly CheckBox _captureClipboardImages = new() { Text = "이미지/캡처 저장", AutoSize = true };
    private ClipboardHistoryService? _clipboard;
    private readonly Dictionary<string, Panel> _pages = new();
    private readonly Dictionary<string, ConPhoneNavButton> _navButtons = new();
    private Panel? _contentHost;
    private readonly NotifyIcon _tray = new() { Text = "Con.Phone · 로컬 입력", Icon = SystemIcons.Application, Visible = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer _peerTimer = new() { Interval = 1500 };
    private Task? _pump;
    private long _epoch, _sent;
    private bool _closing, _closed, _exitRequested;
    private string _lastError = "None";

    public MainForm()
    {
        Text = "Con.Phone 0.1.6 · Bluetooth 키보드/마우스 공유";
        Width = 920; Height = 760; MinimumSize = new Size(820, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 248, 252);

        _profile.Items.AddRange(["갤럭시 / Android", "iPhone (실기기 검증 전)"]); _profile.SelectedIndex = 0;
        foreach (var option in HotkeyCatalog.Options)
        {
            _toggleKey.Items.Add(option);
            _emergencyKey.Items.Add(option);
        }
        LoadSettingsToUi();
        ApplyInputSettings(save: false);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        header.Controls.Add(new Label { Text = "Con.Phone", Font = new Font("Segoe UI", 25, FontStyle.Bold), AutoSize = true });
        header.Controls.Add(new Label { Text = "노트북의 키보드·마우스를 Bluetooth HID로 휴대폰에 전달", AutoSize = true, ForeColor = Color.DimGray });
        root.Controls.Add(header, 0, 0);

        _state.Margin = new Padding(0, 8, 0, 8);
        root.Controls.Add(_state, 0, 1);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        root.Controls.Add(tabs, 0, 2);

        var connectionTab = new TabPage("연결");
        var connection = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(14)
        };
        connectionTab.Controls.Add(connection);
        connection.Controls.Add(new Label { Text = "대상 프로필", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        connection.Controls.Add(_profile);
        connection.Controls.Add(Row(
            Button("Bluetooth 다시 검사", async () => _diagnosis.Text = await BleHidServer.DiagnoseAsync()),
            Button("Bluetooth 광고 확인/시작", async () => { await _ble.StartAsync(); Log("Bluetooth HID 광고 상태를 확인했습니다."); }),
            Button("서비스 중지", async () => { await ReturnLocalAsync(); _ble.Stop(); })));
        connection.Controls.Add(new Label
        {
            Text = "휴대폰에서 이 PC를 페어링한 뒤 아래 목록에서 본인 기기를 승인하세요. 기존 등록이 연결되지 않으면 휴대폰에서 삭제 후 새로 페어링하세요.",
            AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 12, 3, 8)
        });
        connection.Controls.Add(_peers);
        connection.Controls.Add(Row(
            Button("연결 목록 갱신", () => RefreshPeersAsync(false)),
            Button("선택한 기기 승인", async () =>
            {
                await ReturnLocalAsync();
                if (_peers.SelectedItem is not Peer peer) throw new InvalidOperationException("기기를 먼저 선택해 주세요.");
                if (MessageBox.Show(this, $"본인 휴대폰이 맞습니까?\r\n{peer}", "입력 대상 승인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                await _ble.ApproveAsync(peer.Id);
                Log($"기기 승인 완료. {HotkeyCatalog.Display(_settings.ToggleHotkey)} 키로 휴대폰 입력을 시작하세요.");
            }),
            Button("승인 해제", async () => { await ReturnLocalAsync(); _ble.Revoke(); })));
        connection.Controls.Add(Row(
            Button("입력 전환", ToggleAsync),
            Button("노트북 즉시 복귀", ReturnLocalAsync)));
        connection.Controls.Add(_shortcutSummary);
        connection.Controls.Add(new Label
        {
            Text = "창의 X를 눌러도 Bluetooth 연결 유지를 위해 트레이에서 계속 실행됩니다.",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 12, 3, 3)
        });
        tabs.TabPages.Add(connectionTab);

        var inputTab = new TabPage("입력 설정");
        var input = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(14)
        };
        inputTab.Controls.Add(input);

        input.Controls.Add(new Label { Text = "마우스 감도", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        input.Controls.Add(new Label
        {
            Text = "휴대폰에서 PC와 체감 속도가 다를 때 조절합니다. 100%가 원본 Raw Input 속도입니다.",
            AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Color.DimGray
        });
        input.Controls.Add(Row(_mouseSpeed, _mouseSpeedValue));

        input.Controls.Add(new Label { Text = "스크롤 감도", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 16, 3, 3) });
        input.Controls.Add(Row(_scrollSpeed, _scrollSpeedValue, _invertScroll));

        input.Controls.Add(new Label { Text = "전환 단축키", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 18, 3, 3) });
        input.Controls.Add(LabeledRow("PC ↔ 휴대폰 전환", _toggleKey));
        input.Controls.Add(LabeledRow("비상 노트북 복귀", _emergencyKey));
        input.Controls.Add(new Label
        {
            Text = "0.1.6에서는 단일 키 단축키를 사용합니다. 두 키는 서로 다르게 지정해야 합니다.",
            AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Color.DimGray
        });
        input.Controls.Add(Row(
            Button("설정 적용/저장", () => { ApplyInputSettings(save: true); return Task.CompletedTask; }),
            Button("기본값 복원", () => { ResetInputSettings(); return Task.CompletedTask; })));
        tabs.TabPages.Add(inputTab);

        var diagnosticsTab = new TabPage("진단");
        var diagnostics = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(14)
        };
        diagnosticsTab.Controls.Add(diagnostics);
        diagnostics.Controls.Add(new Label { Text = "Bluetooth 상태", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        diagnostics.Controls.Add(_diagnosis);
        diagnostics.Controls.Add(new Label { Text = "실행 로그", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
        _events.Height = 220;
        diagnostics.Controls.Add(_events);
        diagnostics.Controls.Add(Row(Button("진단 저장", SaveDiagnosticsAsync), Button("로그 지우기", () => { _events.Clear(); return Task.CompletedTask; })));
        diagnostics.Controls.Add(new Label { Text = $"설정 파일: {SettingsStore.FilePath}", AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(820, 0) });
        tabs.TabPages.Add(diagnosticsTab);

        _mouseSpeed.ValueChanged += (_, _) => _mouseSpeedValue.Text = $"{_mouseSpeed.Value}%";
        _scrollSpeed.ValueChanged += (_, _) => _scrollSpeedValue.Text = $"{_scrollSpeed.Value}%";
        var menu = new ContextMenuStrip();
        menu.Items.Add("창 열기", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("노트북 복귀", null, (_, _) => Dispatch(ReturnLocalAsync));
        menu.Items.Add("종료", null, (_, _) => { _exitRequested = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        _input.Packet += packet =>
        {
            var outgoing = packet is PointerPacket pointer ? _pointerScaler.Apply(pointer) : packet;
            if (_input.IsRemote && !_outbox.TryAdd(new(Volatile.Read(ref _epoch), outgoing)))
            { ImmediateLocal(); DispatchCritical(async () => { await ReleaseAsync(); Log("입력 대기열 초과 — 로컬 복귀"); }); }
        };
        _input.Toggle += () => DispatchCritical(ToggleAsync);
        _input.Emergency += () => { ImmediateLocal(); DispatchCritical(ReturnLocalAsync); };
        _input.Fault += text => { ImmediateLocal(); DispatchCritical(async () => { await ReleaseAsync(); Log(text); }); };
        _ble.Status += text => Post(() => Log(text));
        _ble.Changed += () =>
        {
            if (_input.IsRemote && !_ble.Ready) { ImmediateLocal(); Post(() => Log("연결/구독 상태 변경 — 로컬 복귀. 자동 재개하지 않습니다.")); }
        };
        _timer.Tick += (_, _) =>
        {
            if (_input.IsRemote && !_ble.Ready) ImmediateLocal();
            var emergency = HotkeyCatalog.Display(_settings.EmergencyHotkey);
            _state.Text = _input.IsRemote
                ? $"휴대폰 조작 중 · {emergency}로 즉시 복귀"
                : $"노트북 조작 중 · 광고:{_ble.Advertisement} · HID구독 K:{_ble.KeyboardSubscribers} M:{_ble.MouseSubscribers} · 승인:{(_ble.Ready ? "준비됨" : "대기")}";
            _tray.Text = _input.IsRemote ? $"Con.Phone · 휴대폰 입력 / {emergency} 복귀" : "Con.Phone · 노트북 입력";
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
    private void LoadSettingsToUi()
    {
        _mouseSpeed.Value = _settings.MouseSpeedPercent;
        _scrollSpeed.Value = _settings.ScrollSpeedPercent;
        _invertScroll.Checked = _settings.InvertScroll;
        SelectHotkey(_toggleKey, _settings.ToggleHotkey.Key);
        SelectHotkey(_emergencyKey, _settings.EmergencyHotkey.Key);
        UpdateSettingLabels();
    }

    private static void SelectHotkey(ComboBox box, int key)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is HotkeyOption option && option.Key == key)
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static int SelectedHotkey(ComboBox box) =>
        box.SelectedItem is HotkeyOption option ? option.Key : throw new InvalidOperationException("단축키를 선택해 주세요.");

    private void UpdateSettingLabels()
    {
        _mouseSpeedValue.Text = $"{_mouseSpeed.Value}%";
        _scrollSpeedValue.Text = $"{_scrollSpeed.Value}%";
        _shortcutSummary.Text = $"전환: {HotkeyCatalog.Display(_settings.ToggleHotkey)}   ·   비상 복귀: {HotkeyCatalog.Display(_settings.EmergencyHotkey)}";
    }

    private void ApplyInputSettings(bool save)
    {
        var toggle = SelectedHotkey(_toggleKey);
        var emergency = SelectedHotkey(_emergencyKey);
        if (toggle == emergency) throw new InvalidOperationException("입력 전환 키와 비상 복귀 키는 서로 다르게 지정해 주세요.");

        _settings.MouseSpeedPercent = _mouseSpeed.Value;
        _settings.ScrollSpeedPercent = _scrollSpeed.Value;
        _settings.InvertScroll = _invertScroll.Checked;
        _settings.ToggleHotkey = new HotkeyBinding { Key = toggle, Modifiers = HotkeyModifiers.None };
        _settings.EmergencyHotkey = new HotkeyBinding { Key = emergency, Modifiers = HotkeyModifiers.None };

        _pointerScaler.Configure(_settings.MouseSpeedPercent, _settings.ScrollSpeedPercent, _settings.InvertScroll);
        _input.ConfigureHotkeys(_settings.ToggleHotkey, _settings.EmergencyHotkey);
        UpdateSettingLabels();

        if (save)
        {
            SettingsStore.Save(_settings);
            Log($"입력 설정 저장 · 마우스 {_settings.MouseSpeedPercent}% · 스크롤 {_settings.ScrollSpeedPercent}% · 전환 {HotkeyCatalog.Display(_settings.ToggleHotkey)} · 복귀 {HotkeyCatalog.Display(_settings.EmergencyHotkey)}");
        }
    }

    private void ResetInputSettings()
    {
        _mouseSpeed.Value = 100;
        _scrollSpeed.Value = 100;
        _invertScroll.Checked = false;
        SelectHotkey(_toggleKey, 0x77);
        SelectHotkey(_emergencyKey, 0x78);
        ApplyInputSettings(save: true);
    }

    private static FlowLayoutPanel LabeledRow(string label, Control control)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, MaximumSize = new Size(820, 0) };
        row.Controls.Add(new Label { Text = label, AutoSize = false, Width = 180, Height = 30, TextAlign = ContentAlignment.MiddleLeft });
        row.Controls.Add(control);
        return row;
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
        catch (Exception ex) { HandleActionError(ex); }
        finally { _actions.Release(); }
    });

    private void DispatchCritical(Func<Task> action) => Post(async () =>
    {
        await _actions.WaitAsync();
        try { if (!_closing) await action(); }
        catch (Exception ex) { HandleActionError(ex); }
        finally { _actions.Release(); }
    });

    private void HandleActionError(Exception ex)
    {
        ImmediateLocal();
        _lastError = $"{ex.GetType().Name}:0x{ex.HResult:X8}";
        var ble = ex.HResult == unchecked((int)0x8000000E)
            ? $" · BLE:{_ble.LastOperation} · 광고:{_ble.Advertisement}"
            : "";
        Log($"{ex.Message} ({_lastError}{ble})");
    }
    private void Log(string text)
    {
        if (_events.TextLength > 10000) _events.Clear();
        _events.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\r\n");
    }
    private void ImmediateLocal()
    {
        _input.Deactivate();
        _pointerScaler.Reset();
        Interlocked.Increment(ref _epoch);
        _outbox.Clear();
    }
    private async Task ReleaseAsync()
    {
        if (!_ble.Ready) return;
        long epoch = Volatile.Read(ref _epoch);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await _ble.SendAsync(new(true, new byte[8]), () => epoch == Volatile.Read(ref _epoch), timeout.Token);
            await _ble.SendAsync(new(false, new byte[Hid.MouseReportLength]), () => epoch == Volatile.Read(ref _epoch), timeout.Token);
        }
        catch (Exception ex)
        {
            _lastError = $"Release:{ex.GetType().Name}:0x{ex.HResult:X8}";
            Log("휴대폰에 키/버튼 해제 보고서를 확인하지 못했지만 노트북 입력은 즉시 복구했습니다. Bluetooth 서비스는 유지합니다.");
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
        _pointerScaler.Reset();
        _input.Activate();
        Log($"휴대폰 입력 시작. {HotkeyCatalog.Display(_settings.EmergencyHotkey)}로 비상 복귀할 수 있습니다.");
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
                ImmediateLocal();
                _lastError = $"Send:{ex.GetType().Name}:0x{ex.HResult:X8}";
                Post(() => Log("Bluetooth 전송 실패/지연으로 노트북 입력으로 복귀했습니다. 서비스는 유지하므로 연결 상태를 확인한 뒤 다시 전환할 수 있습니다."));
            }
        }
    }
    private async Task SaveDiagnosticsAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "ConPhone-diagnostics.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var report = new
        {
            Version = "0.1.6", OS = Environment.OSVersion.Version.ToString(), Runtime = Environment.Version.ToString(),
            TargetProfile = _profile.SelectedIndex == 0 ? "Android" : "iPhone", Bluetooth = await BleHidServer.DiagnoseAsync(),
            Advertisement = _ble.Advertisement, BleLastOperation = _ble.LastOperation, KeyboardSubscribers = _ble.KeyboardSubscribers, MouseSubscribers = _ble.MouseSubscribers, ApprovedAndSubscribed = _ble.Ready,
            SendOperations = Interlocked.Read(ref _sent), Pending = _outbox.Count, LastError = _lastError,
            MouseSpeedPercent = _settings.MouseSpeedPercent, ScrollSpeedPercent = _settings.ScrollSpeedPercent,
            ToggleHotkey = HotkeyCatalog.Display(_settings.ToggleHotkey), EmergencyHotkey = HotkeyCatalog.Display(_settings.EmergencyHotkey),
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
