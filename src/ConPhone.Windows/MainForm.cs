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
        Text = "Con.Phone 0.2.0 · Bluetooth 입력 허브";
        Width = 1080; Height = 760; MinimumSize = new Size(920, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = ConPhoneTheme.Font(10f);
        BackColor = ConPhoneTheme.Basement;

        _state.Font = ConPhoneTheme.Font(11f, FontStyle.Bold);
        _state.ForeColor = ConPhoneTheme.Text;
        _diagnosis.BackColor = ConPhoneTheme.Fill;
        _diagnosis.ForeColor = ConPhoneTheme.TextSecondary;
        _events.BackColor = ConPhoneTheme.Fill;
        _events.ForeColor = ConPhoneTheme.TextSecondary;
        _peers.BackColor = ConPhoneTheme.Surface;
        _peers.ForeColor = ConPhoneTheme.Text;
        _clipboardList.BackColor = ConPhoneTheme.Surface;
        _clipboardList.ForeColor = ConPhoneTheme.Text;

        _profile.Items.AddRange(["갤럭시 / Android", "iPhone (실기기 검증 전)"]);
        _profile.SelectedIndex = 0;

        _clipboard = new ClipboardHistoryService(
            _settings.ClipboardHistoryLimit,
            _settings.CaptureClipboardText,
            _settings.CaptureClipboardImages);
        _clipboard.Changed += () => Post(RefreshClipboardList);

        LoadSettingsToUi();
        ApplyInputSettings(save: false);
        BuildShell();
        RefreshClipboardList();

        _mouseSpeed.ValueChanged += (_, _) => _mouseSpeedValue.Text = $"{_mouseSpeed.Value}%";
        _scrollSpeed.ValueChanged += (_, _) => _scrollSpeedValue.Text = $"{_scrollSpeed.Value}%";
        _clipboardList.SelectedIndexChanged += (_, _) => UpdateClipboardDetail();

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
        _input.LockAndReturn += () => DispatchCritical(LockPhoneAndReturnAsync);
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
    private void BuildShell()
    {
        Controls.Clear();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ConPhoneTheme.Basement,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = ConPhoneTheme.Surface, Padding = new Padding(14, 22, 14, 18) };
        root.Controls.Add(sidebar, 0, 0);

        var brand = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = Color.Transparent };
        brand.Controls.Add(new Label
        {
            Text = "Con.Phone",
            AutoSize = true,
            Font = ConPhoneTheme.Font(20f, FontStyle.Bold),
            ForeColor = ConPhoneTheme.Text,
            Location = new Point(12, 4)
        });
        brand.Controls.Add(new Label
        {
            Text = "Bluetooth 입력 허브",
            AutoSize = true,
            Font = ConPhoneTheme.Font(9.5f),
            ForeColor = ConPhoneTheme.TextTertiary,
            Location = new Point(13, 42)
        });
        sidebar.Controls.Add(brand);

        var nav = new Panel { Dock = DockStyle.Top, Height = 220, BackColor = Color.Transparent };
        sidebar.Controls.Add(nav);
        nav.BringToFront();

        AddNavigation(nav, "connection", "연결", 0);
        AddNavigation(nav, "input", "입력 설정", 1);
        AddNavigation(nav, "clipboard", "클립보드", 2);
        AddNavigation(nav, "diagnostics", "진단", 3);

        var sideFooter = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            Text = "0.2.0\r\n설정은 이 PC에만 저장됩니다.",
            Font = ConPhoneTheme.Font(8.5f),
            ForeColor = ConPhoneTheme.TextTertiary,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(10, 0, 0, 0)
        };
        sidebar.Controls.Add(sideFooter);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ConPhoneTheme.Basement,
            Padding = new Padding(28, 20, 28, 24)
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(right, 1, 0);

        var top = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var title = ConPhoneTheme.Title("내 기기", 18f);
        title.Location = new Point(0, 2);
        top.Controls.Add(title);
        _state.Location = new Point(0, 34);
        top.Controls.Add(_state);
        right.Controls.Add(top, 0, 0);

        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, AutoScroll = true };
        right.Controls.Add(_contentHost, 0, 1);

        _pages["connection"] = BuildConnectionPage();
        _pages["input"] = BuildInputPage();
        _pages["clipboard"] = BuildClipboardPage();
        _pages["diagnostics"] = BuildDiagnosticsPage();
        foreach (var page in _pages.Values)
        {
            page.Visible = false;
            _contentHost.Controls.Add(page);
        }
        ShowPage("connection");
    }

    private void AddNavigation(Panel host, string key, string text, int order)
    {
        var button = new ConPhoneNavButton
        {
            Text = text, BackColor = Color.Transparent, Dock = DockStyle.None,
            Location = new Point(0, order * 48), Width = 188
        };
        button.Click += (_, _) => ShowPage(key);
        host.Controls.Add(button);
        _navButtons[key] = button;
    }

    private void ShowPage(string key)
    {
        foreach (var pair in _pages) pair.Value.Visible = pair.Key == key;
        foreach (var pair in _navButtons) pair.Value.Selected = pair.Key == key;
        if (_pages.TryGetValue(key, out var page))
        {
            page.BringToFront();
            page.Focus();
        }
    }

    private Panel BuildConnectionPage()
    {
        var page = PageFlow();

        var intro = CardFlow();
        intro.Controls.Add(ConPhoneTheme.Title("휴대폰 연결", 16f));
        intro.Controls.Add(ConPhoneTheme.Body("휴대폰은 Con.Phone을 Bluetooth 키보드·마우스로 인식합니다. 연결 후 본인 기기를 한 번 승인하면 단축키로 바로 전환할 수 있습니다."));
        intro.Controls.Add(Row(
            Button("Bluetooth 확인", async () => _diagnosis.Text = await BleHidServer.DiagnoseAsync()),
            Button("광고 확인/시작", async () => { await _ble.StartAsync(); Log("Bluetooth HID 광고 상태를 확인했습니다."); }, ConPhoneButtonKind.Primary),
            Button("서비스 중지", async () => { await ReturnLocalAsync(); _ble.Stop(); }, ConPhoneButtonKind.Ghost)));
        page.Controls.Add(intro);

        var devices = CardFlow();
        devices.Controls.Add(ConPhoneTheme.Section("연결된 기기"));
        devices.Controls.Add(ConPhoneTheme.Body("목록에서 본인 휴대폰을 선택한 뒤 승인하세요. 기존 등록이 연결되지 않으면 휴대폰 Bluetooth 설정에서 삭제 후 새로 페어링하세요."));
        _peers.Width = 720; _peers.Height = 118;
        devices.Controls.Add(_peers);
        devices.Controls.Add(Row(
            Button("목록 갱신", () => RefreshPeersAsync(false)),
            Button("선택한 기기 승인", async () =>
            {
                await ReturnLocalAsync();
                if (_peers.SelectedItem is not Peer peer) throw new InvalidOperationException("기기를 먼저 선택해 주세요.");
                if (MessageBox.Show(this, $"본인 휴대폰이 맞습니까?\r\n{peer}", "입력 대상 승인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                await _ble.ApproveAsync(peer.Id);
                Log($"기기 승인 완료. {HotkeyCatalog.Display(_settings.ToggleHotkey)}로 휴대폰 입력을 시작할 수 있습니다.");
            }, ConPhoneButtonKind.Primary),
            Button("승인 해제", async () => { await ReturnLocalAsync(); _ble.Revoke(); }, ConPhoneButtonKind.Ghost)));
        page.Controls.Add(devices);

        var actions = CardFlow();
        actions.Controls.Add(ConPhoneTheme.Section("빠른 동작"));
        actions.Controls.Add(ConPhoneTheme.Body($"전환 {_shortcutSummary.Text}"));
        actions.Controls.Add(Row(
            Button("휴대폰으로 전환", ToggleAsync, ConPhoneButtonKind.Primary),
            Button("노트북으로 복귀", ReturnLocalAsync),
            Button("휴대폰 잠금 + 복귀", LockPhoneAndReturnAsync)));
        page.Controls.Add(actions);
        return page;
    }

    private Panel BuildInputPage()
    {
        var page = PageFlow();

        var pointer = CardFlow();
        pointer.Controls.Add(ConPhoneTheme.Title("포인터", 16f));
        pointer.Controls.Add(ConPhoneTheme.Body("Raw Input을 휴대폰으로 보내기 전에 감도만 조절합니다. Windows 자체 마우스 속도는 바꾸지 않습니다."));
        pointer.Controls.Add(ConPhoneTheme.Section("마우스 감도"));
        pointer.Controls.Add(Row(_mouseSpeed, _mouseSpeedValue));
        pointer.Controls.Add(ConPhoneTheme.Section("스크롤 감도"));
        pointer.Controls.Add(Row(_scrollSpeed, _scrollSpeedValue, _invertScroll));
        page.Controls.Add(pointer);

        var shortcuts = CardFlow();
        shortcuts.Controls.Add(ConPhoneTheme.Title("단축키", 16f));
        shortcuts.Controls.Add(ConPhoneTheme.Body("입력칸을 클릭한 뒤 원하는 키 조합을 그대로 누르세요. Ctrl·Alt·Shift·Win 조합을 함께 인식합니다."));
        shortcuts.Controls.Add(HotkeyRow("PC ↔ 휴대폰 전환", _toggleKey, "어느 화면에서든 입력 대상을 전환합니다."));
        shortcuts.Controls.Add(HotkeyRow("비상 노트북 복귀", _emergencyKey, "휴대폰 제어 중 즉시 PC 입력으로 돌아옵니다."));
        shortcuts.Controls.Add(HotkeyRow("휴대폰 잠금 + 복귀", _lockAndReturnKey, "휴대폰 Power 키를 보내고 즉시 PC로 돌아옵니다."));
        shortcuts.Controls.Add(_shortcutSummary);
        shortcuts.Controls.Add(Row(
            Button("설정 저장", () => { ApplyInputSettings(save: true); return Task.CompletedTask; }, ConPhoneButtonKind.Primary),
            Button("기본값 복원", () => { ResetInputSettings(); return Task.CompletedTask; }, ConPhoneButtonKind.Ghost)));
        page.Controls.Add(shortcuts);
        return page;
    }

    private Panel BuildClipboardPage()
    {
        var page = PageFlow();

        var info = CardFlow();
        info.BackColor = ConPhoneTheme.InformativeWeak;
        info.BorderColor = Color.FromArgb(203, 223, 250);
        info.Controls.Add(ConPhoneTheme.Title("클립보드 보관함", 16f));
        info.Controls.Add(ConPhoneTheme.Body("Windows에서 복사한 텍스트와 캡처 이미지를 로컬에 보관합니다. 항목을 다시 Windows 클립보드로 올린 뒤 원하는 프로그램에서 Ctrl+V로 붙여넣을 수 있습니다.", ConPhoneTheme.Text));
        info.Controls.Add(ConPhoneTheme.Body("휴대폰에서 복사한 실제 내용은 Bluetooth HID 표준으로 PC에 전달되지 않습니다. 휴대폰 → PC 자동 동기화는 별도 Android 보조앱/데이터 채널이 있어야 하므로 HID 연결과 분리해 구현해야 합니다.", ConPhoneTheme.TextSecondary));
        page.Controls.Add(info);

        var history = CardFlow();
        history.Controls.Add(ConPhoneTheme.Section("최근 항목"));
        _clipboardList.Width = 720; _clipboardList.Height = 260;
        history.Controls.Add(_clipboardList);
        history.Controls.Add(_clipboardDetail);
        history.Controls.Add(Row(
            Button("Windows 클립보드로 복사", () => { RestoreSelectedClipboard(); return Task.CompletedTask; }, ConPhoneButtonKind.Primary),
            Button("항목 삭제", () => { DeleteSelectedClipboard(); return Task.CompletedTask; }, ConPhoneButtonKind.Ghost),
            Button("전체 비우기", () => { ClearClipboardHistory(); return Task.CompletedTask; }, ConPhoneButtonKind.Ghost)));
        page.Controls.Add(history);

        var settings = CardFlow();
        settings.Controls.Add(ConPhoneTheme.Section("저장 설정"));
        settings.Controls.Add(Row(_captureClipboardText, _captureClipboardImages));
        settings.Controls.Add(Row(ConPhoneTheme.Body("최대 저장 개수", ConPhoneTheme.Text), _clipboardLimit));
        settings.Controls.Add(ConPhoneTheme.Body("클립보드 기록은 %LOCALAPPDATA%\\ConPhone 안에만 저장되며 네트워크로 전송하지 않습니다."));
        settings.Controls.Add(Button("클립보드 설정 저장", () =>
        {
            ApplyClipboardSettings(save: true);
            return Task.CompletedTask;
        }, ConPhoneButtonKind.Primary));
        page.Controls.Add(settings);
        return page;
    }

    private Panel BuildDiagnosticsPage()
    {
        var page = PageFlow();
        var card = CardFlow();
        card.Controls.Add(ConPhoneTheme.Title("진단", 16f));
        card.Controls.Add(ConPhoneTheme.Body("문제가 생기면 이 화면의 상태와 로그를 확인하세요. 진단 파일에는 입력한 문자나 기기 주소를 저장하지 않습니다."));
        card.Controls.Add(ConPhoneTheme.Section("Bluetooth"));
        _diagnosis.Width = 720; _diagnosis.Height = 120;
        card.Controls.Add(_diagnosis);
        card.Controls.Add(ConPhoneTheme.Section("실행 로그"));
        _events.Width = 720; _events.Height = 250;
        card.Controls.Add(_events);
        card.Controls.Add(Row(
            Button("진단 저장", SaveDiagnosticsAsync, ConPhoneButtonKind.Primary),
            Button("로그 지우기", () => { _events.Clear(); return Task.CompletedTask; }, ConPhoneButtonKind.Ghost)));
        page.Controls.Add(card);
        return page;
    }

    private static FlowLayoutPanel PageFlow() => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(0, 0, 8, 18),
        BackColor = Color.Transparent
    };

    private static ConPhoneCard CardFlow() => new()
    {
        Width = 790,
        MaximumSize = new Size(790, 0),
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink
    };

    private Control HotkeyRow(string title, HotkeyCaptureBox box, string help)
    {
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Width = 720, Margin = new Padding(0, 8, 0, 8) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        var labels = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        labels.Controls.Add(ConPhoneTheme.Section(title));
        labels.Controls.Add(ConPhoneTheme.Body(help, ConPhoneTheme.TextTertiary, 420));
        row.Controls.Add(labels, 0, 0);
        row.Controls.Add(box, 1, 0);
        return row;
    }

    private void LoadSettingsToUi()
    {
        _mouseSpeed.Value = _settings.MouseSpeedPercent;
        _scrollSpeed.Value = _settings.ScrollSpeedPercent;
        _invertScroll.Checked = _settings.InvertScroll;
        _toggleKey.Binding = _settings.ToggleHotkey;
        _emergencyKey.Binding = _settings.EmergencyHotkey;
        _lockAndReturnKey.Binding = _settings.LockAndReturnHotkey;
        _clipboardLimit.Value = _settings.ClipboardHistoryLimit;
        _captureClipboardText.Checked = _settings.CaptureClipboardText;
        _captureClipboardImages.Checked = _settings.CaptureClipboardImages;
        UpdateSettingLabels();
    }

    private void UpdateSettingLabels()
    {
        _mouseSpeedValue.Text = $"{_mouseSpeed.Value}%";
        _scrollSpeedValue.Text = $"{_scrollSpeed.Value}%";
        _shortcutSummary.Text =
            $"전환 {HotkeyCatalog.Display(_settings.ToggleHotkey)}   ·   복귀 {HotkeyCatalog.Display(_settings.EmergencyHotkey)}   ·   잠금+복귀 {HotkeyCatalog.Display(_settings.LockAndReturnHotkey)}";
    }

    private void ApplyInputSettings(bool save)
    {
        var toggle = _toggleKey.Binding;
        var emergency = _emergencyKey.Binding;
        var lockAndReturn = _lockAndReturnKey.Binding;
        if (HotkeyCatalog.Same(toggle, emergency) || HotkeyCatalog.Same(toggle, lockAndReturn) || HotkeyCatalog.Same(emergency, lockAndReturn))
            throw new InvalidOperationException("세 동작의 단축키 조합은 서로 달라야 합니다.");

        _settings.MouseSpeedPercent = _mouseSpeed.Value;
        _settings.ScrollSpeedPercent = _scrollSpeed.Value;
        _settings.InvertScroll = _invertScroll.Checked;
        _settings.ToggleHotkey = toggle;
        _settings.EmergencyHotkey = emergency;
        _settings.LockAndReturnHotkey = lockAndReturn;

        _pointerScaler.Configure(_settings.MouseSpeedPercent, _settings.ScrollSpeedPercent, _settings.InvertScroll);
        _input.ConfigureHotkeys(_settings.ToggleHotkey, _settings.EmergencyHotkey, _settings.LockAndReturnHotkey);
        UpdateSettingLabels();

        if (save)
        {
            SettingsStore.Save(_settings);
            Log($"입력 설정 저장 · 마우스 {_settings.MouseSpeedPercent}% · 전환 {HotkeyCatalog.Display(_settings.ToggleHotkey)} · 복귀 {HotkeyCatalog.Display(_settings.EmergencyHotkey)} · 잠금+복귀 {HotkeyCatalog.Display(_settings.LockAndReturnHotkey)}");
        }
    }

    private void ApplyClipboardSettings(bool save)
    {
        _settings.ClipboardHistoryLimit = (int)_clipboardLimit.Value;
        _settings.CaptureClipboardText = _captureClipboardText.Checked;
        _settings.CaptureClipboardImages = _captureClipboardImages.Checked;
        _clipboard?.Configure(_settings.ClipboardHistoryLimit, _settings.CaptureClipboardText, _settings.CaptureClipboardImages);
        if (save)
        {
            SettingsStore.Save(_settings);
            Log($"클립보드 설정 저장 · 최대 {_settings.ClipboardHistoryLimit}개");
        }
    }

    private void ResetInputSettings()
    {
        _mouseSpeed.Value = 100;
        _scrollSpeed.Value = 100;
        _invertScroll.Checked = false;
        _toggleKey.Binding = new HotkeyBinding { Key = 0x77 };
        _emergencyKey.Binding = new HotkeyBinding { Key = 0x78 };
        _lockAndReturnKey.Binding = new HotkeyBinding { Key = (int)Keys.L, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt };
        ApplyInputSettings(save: true);
    }

    private void RefreshClipboardList()
    {
        if (_clipboard is null || _closed) return;
        var selectedId = (_clipboardList.SelectedItem as ClipboardEntry)?.Id;
        _clipboardList.BeginUpdate();
        try
        {
            _clipboardList.Items.Clear();
            foreach (var entry in _clipboard.Entries) _clipboardList.Items.Add(entry);
            if (selectedId is not null)
            {
                for (var i = 0; i < _clipboardList.Items.Count; i++)
                    if (_clipboardList.Items[i] is ClipboardEntry item && item.Id == selectedId)
                        _clipboardList.SelectedIndex = i;
            }
            if (_clipboardList.SelectedIndex < 0 && _clipboardList.Items.Count > 0) _clipboardList.SelectedIndex = 0;
        }
        finally { _clipboardList.EndUpdate(); }
        UpdateClipboardDetail();
    }

    private void UpdateClipboardDetail()
    {
        if (_clipboardList.SelectedItem is not ClipboardEntry entry)
        {
            _clipboardDetail.Text = "저장된 클립보드 항목이 없습니다.";
            return;
        }
        _clipboardDetail.Text = entry.Kind == ClipboardEntryKind.Text
            ? $"텍스트 · {entry.CapturedAt:G} · {(entry.Text?.Length ?? 0):N0}자"
            : $"이미지 · {entry.CapturedAt:G} · {entry.Preview}";
    }

    private void RestoreSelectedClipboard()
    {
        if (_clipboardList.SelectedItem is not ClipboardEntry entry) return;
        if (_clipboard?.Restore(entry.Id) == true) Log("선택한 항목을 Windows 클립보드에 복원했습니다.");
        else Log("클립보드 복원에 실패했습니다. 다른 프로그램이 클립보드를 사용 중인지 확인하세요.");
    }

    private void DeleteSelectedClipboard()
    {
        if (_clipboardList.SelectedItem is ClipboardEntry entry) _clipboard?.Delete(entry.Id);
    }

    private void ClearClipboardHistory()
    {
        if (MessageBox.Show(this, "저장된 클립보드 기록을 모두 지울까요?", "클립보드 기록 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _clipboard?.Clear();
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(740, 0), BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 6) };
        row.Controls.AddRange(controls);
        return row;
    }

    private ConPhoneButton Button(string text, Func<Task> action, ConPhoneButtonKind kind = ConPhoneButtonKind.Secondary)
    {
        var b = new ConPhoneButton { Text = text, Kind = kind };
        b.Click += (_, _) => Dispatch(action);
        return b;
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

    private async Task LockPhoneAndReturnAsync()
    {
        ImmediateLocal();
        if (!_ble.Ready)
        {
            Log("휴대폰 잠금 요청을 보낼 연결이 없어 노트북 입력으로만 복귀했습니다.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        try
        {
            await _ble.TapKeyboardUsageAsync(Hid.KeyboardPowerUsage, timeout.Token);
            await _ble.SendAsync(new(false, new byte[Hid.MouseReportLength]), () => true, timeout.Token);
            Log("휴대폰에 Power 키를 보내고 노트북 입력으로 복귀했습니다. 기기에서 Power HID를 지원하지 않으면 화면은 그대로일 수 있습니다.");
        }
        catch (Exception ex)
        {
            _lastError = $"LockReturn:{ex.GetType().Name}:0x{ex.HResult:X8}";
            Log("노트북 입력으로는 복귀했지만 휴대폰 화면 잠금 전송을 확인하지 못했습니다.");
        }
    }

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
            Version = "0.2.0", OS = Environment.OSVersion.Version.ToString(), Runtime = Environment.Version.ToString(),
            TargetProfile = _profile.SelectedIndex == 0 ? "Android" : "iPhone", Bluetooth = await BleHidServer.DiagnoseAsync(),
            Advertisement = _ble.Advertisement, BleLastOperation = _ble.LastOperation, KeyboardSubscribers = _ble.KeyboardSubscribers, MouseSubscribers = _ble.MouseSubscribers, ApprovedAndSubscribed = _ble.Ready,
            SendOperations = Interlocked.Read(ref _sent), Pending = _outbox.Count, LastError = _lastError,
            MouseSpeedPercent = _settings.MouseSpeedPercent, ScrollSpeedPercent = _settings.ScrollSpeedPercent,
            ToggleHotkey = HotkeyCatalog.Display(_settings.ToggleHotkey), EmergencyHotkey = HotkeyCatalog.Display(_settings.EmergencyHotkey),
            LockAndReturnHotkey = HotkeyCatalog.Display(_settings.LockAndReturnHotkey),
            ClipboardEntries = _clipboard?.Entries.Count ?? 0,
            AndroidHardwareTest = "Phone lock HID Power is device-dependent and requires hardware validation.", IPhoneHardwareTest = "Not performed",
            Privacy = "Diagnostic export excludes clipboard contents, typed keys, pairing codes, device names and Bluetooth addresses. Clipboard history itself is stored locally under LocalAppData."
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
        _lifetime.Cancel(); _ble.Stop(); _input.Dispose(); _clipboard?.Dispose();
        if (_pump is not null) { try { await _pump; } catch (OperationCanceledException) { } }
        SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged;
        _timer.Stop(); _timer.Dispose(); _peerTimer.Stop(); _peerTimer.Dispose(); _tray.Visible = false; _tray.Dispose();
        _closed = true; Close();
    }
}
