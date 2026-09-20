using System.Text.Json;
using ConPhone.Core;
using Microsoft.Win32;

namespace ConPhone.Windows;

public sealed partial class MainForm : Form
{
    private static readonly Color Bg = ColorTranslator.FromHtml("#F3F5F9");
    private static readonly Color Surface = Color.White;
    private static readonly Color Border = ColorTranslator.FromHtml("#DCDEE3");
    private static readonly Color TextColor = ColorTranslator.FromHtml("#1A1C20");
    private static readonly Color Secondary = ColorTranslator.FromHtml("#555D6D");
    private static readonly Color Tertiary = ColorTranslator.FromHtml("#868B94");
    private static readonly Color Brand = ColorTranslator.FromHtml("#2563EB");
    private static readonly Color BrandPressed = ColorTranslator.FromHtml("#1D4ED8");
    private static readonly Color BrandWeak = ColorTranslator.FromHtml("#EAF1FF");
    private static readonly Color InfoWeak = ColorTranslator.FromHtml("#EFF6FF");

    private readonly BleHidServer _ble = new();
    private readonly InputCapture _input = new();
    private readonly Outbox _outbox = new();
    private readonly PointerScaler _pointerScaler = new();
    private readonly ConPhoneSettings _settings = SettingsStore.Load();
    private HangulKeyMode _activeHangulMode;
    private ModifierProfile _activeModifierProfile;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private int _healthCheckRunning, _clipboardSendRunning, _peerRefreshRunning;
    private readonly SemaphoreSlim _inputActions = new(1, 1);
    private long _lastPointerDispatchTicks;
    private readonly Label _clipboardConnection = new() { AutoSize = true, MaximumSize = new Size(780, 0) };

    private readonly Label _state = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 11, FontStyle.Bold),
        ForeColor = TextColor
    };

    private readonly TextBox _diagnosis = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Width = 820,
        Height = 120,
        BackColor = Color.White,
        ForeColor = Secondary
    };

    private readonly ListBox _peers = new()
    {
        Width = 820,
        Height = 120,
        BackColor = Color.White,
        ForeColor = TextColor
    };

    private readonly TextBox _events = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Width = 820,
        Height = 260,
        BackColor = Color.White,
        ForeColor = Secondary
    };

    private readonly TrackBar _mouseSpeed = new()
    {
        Minimum = 25,
        Maximum = 300,
        TickFrequency = 25,
        SmallChange = 5,
        LargeChange = 25,
        Width = 440
    };

    private readonly TrackBar _scrollSpeed = new()
    {
        Minimum = 25,
        Maximum = 300,
        TickFrequency = 25,
        SmallChange = 5,
        LargeChange = 25,
        Width = 440
    };

    private readonly Label _mouseSpeedValue = new() { AutoSize = true, Width = 70, ForeColor = Secondary };
    private readonly Label _scrollSpeedValue = new() { AutoSize = true, Width = 70, ForeColor = Secondary };
    private readonly CheckBox _invertScroll = new() { Text = "스크롤 방향 반전", AutoSize = true, ForeColor = TextColor };

    private readonly ComboBox _hangulMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240
    };

    private readonly ComboBox _modifierProfile = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240
    };

    private readonly TextBox _nextDeviceHotkeyBox = HotkeyBox();
    private readonly TextBox _toggleHotkeyBox = HotkeyBox();
    private readonly TextBox _emergencyHotkeyBox = HotkeyBox();
    private readonly TextBox _lockHotkeyBox = HotkeyBox();

    private readonly Label _quickHotkeyHelp = new()
    {
        AutoSize = true,
        MaximumSize = new Size(810, 0),
        ForeColor = Secondary,
        Margin = new Padding(0, 0, 0, 10)
    };

    private readonly ListBox _clipboardList = new()
    {
        Width = 820,
        Height = 270,
        BackColor = Color.White,
        ForeColor = TextColor
    };

    private readonly Label _clipboardDetail = new()
    {
        AutoSize = true,
        MaximumSize = new Size(820, 0),
        ForeColor = Secondary
    };

    private readonly NumericUpDown _clipboardLimit = new()
    {
        Minimum = 10,
        Maximum = 200,
        Increment = 10,
        Width = 90
    };

    private readonly CheckBox _captureClipboardText = new()
    {
        Text = "텍스트 기록",
        AutoSize = true,
        ForeColor = TextColor
    };

    private readonly CheckBox _captureClipboardImages = new()
    {
        Text = "이미지 / 캡처 기록",
        AutoSize = true,
        ForeColor = TextColor
    };

    private readonly CheckBox _clipboardSyncEnabled = new()
    {
        Text = "기기간 복붙 사용",
        AutoSize = true,
        ForeColor = TextColor
    };

    private readonly CheckBox _clipboardSyncText = new()
    {
        Text = "텍스트 자동 전송",
        AutoSize = true,
        ForeColor = TextColor
    };

    private readonly CheckBox _clipboardSyncImages = new()
    {
        Text = "이미지 자동 전송",
        AutoSize = true,
        ForeColor = TextColor
    };

    private readonly NumericUpDown _clipboardBleImageLimit = new()
    {
        Minimum = 64,
        Maximum = 4096,
        Increment = 64,
        Width = 90
    };

    private readonly TextBox _p2pLinkName = new()
    {
        Width = 220,
        MaxLength = 18,
        BackColor = Color.White,
        ForeColor = TextColor,
        BorderStyle = BorderStyle.FixedSingle
    };

    private ClipboardHistoryService? _clipboard;
    private ClipboardSyncCoordinator? _clipboardSync;
    private ClipboardContent? _latestLocalClipboard;
    private long _latestClipboardGeneration;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long>
        _lastSentClipboardGenerationByDevice = new(StringComparer.Ordinal);

    private readonly NotifyIcon _tray = new()
    {
        Text = "Con.Phone · 노트북 입력",
        Icon = SystemIcons.Application,
        Visible = true
    };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 750 };
    private readonly System.Windows.Forms.Timer _healthTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _peerTimer = new() { Interval = 5000 };

    private readonly bool _previewMode;
    private Task? _pump;
    private long _epoch, _sent, _lastPointerSentTicks, _bleNotReadySinceTicks;
    private int _lastObservedPointerButtons, _lastSentPointerButtons;
    private bool _closing, _closed, _exitRequested, _servicesPaused;
    private string _lastError = "None";

    public MainForm(bool previewMode = false)
    {
        _previewMode = previewMode;
        _activeHangulMode = _settings.HangulKeyMode;
        _activeModifierProfile = _settings.ModifierProfile;
        Text = $"Con.Phone {LocalUpdateService.CurrentVersion.ToString(3)} · Bluetooth 입력 허브";
        Width = 1060;
        Height = 860;
        MinimumSize = new Size(900, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Bg;
        ForeColor = TextColor;
        AutoScaleMode = AutoScaleMode.Dpi;

        _hangulMode.Items.AddRange(
        [
            "Right Alt (Galaxy 권장)",
            "HID LANG1",
            "Ctrl+Space",
            "Shift+Space"
        ]);
        _modifierProfile.Items.AddRange(
        [
            "표준 Windows/Android",
            "Mac/iPad · Ctrl ↔ Command"
        ]);

        LoadSettingsToUi();
        ApplyInputSettings(save: false);
        BuildUi();

        if (_previewMode)
        {
            _tray.Visible = false;
            _state.Text = "이 PC 사용 중  ·  휴대폰으로 전환할 준비가 되었어요";
            _peers.Items.Add(new Peer(
                "preview-device",
                "Galaxy / Android",
                "A1B2C3D4",
                Keyboard: true,
                Mouse: true,
                SystemControl: true,
                Paired: true,
                Connected: true,
                Trusted: true,
                Active: true));
            _peers.SelectedIndex = 0;
            _diagnosis.Text = "Bluetooth LE: True\r\nPeripheral 역할: True\r\n무선 상태: On";
            _feedback.Text = "화면 미리보기 · 기기 정보는 예시입니다";
            _toggleButton.Enabled = true;
        }
        else
        {
            _clipboardSync = new ClipboardSyncCoordinator(
                () => _ble.ApprovedId,
                id => _ble.IsTrustedDevice(id),
                () => _settings.ClipboardSyncEnabled,
                () => _settings.ClipboardBleImageLimitKb * 1024,
                () => _input.IsRemote,
                IsPointerRecentlyActive,
                () => _settings.P2PLinkName);
            ConfigureTray();
            ConfigureRuntimeEvents();
            _pump = Task.Run(PumpAsync);

            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            FormClosing += ClosingAsync;
        }
    }

    private static TextBox HotkeyBox() => new()
    {
        ReadOnly = true,
        Width = 230,
        Height = 34,
        TextAlign = HorizontalAlignment.Center,
        BackColor = Color.White,
        ForeColor = TextColor,
        BorderStyle = BorderStyle.FixedSingle,
        TabStop = false
    };

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 12),
            ColumnCount = 1, RowCount = 4, BackColor = Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        header.Controls.Add(new Label
        {
            Text = "Con.Phone", AutoSize = true, Location = new Point(0, 0),
            Font = new Font("Segoe UI", 23, FontStyle.Bold), ForeColor = TextColor
        });
        header.Controls.Add(new Label
        {
            Text = "키보드, 마우스, 클립보드. 내 PC와 휴대폰을 하나처럼.",
            AutoSize = true, Location = new Point(2, 44), ForeColor = Secondary
        });
        root.Controls.Add(header, 0, 0);
        var status = new Panel { Dock = DockStyle.Fill, BackColor = BrandWeak, Margin = new Padding(0, 0, 0, 8) };
        _state.Dock = DockStyle.Fill;
        _state.AutoSize = false;
        _state.Padding = new Padding(12, 0, 0, 0);
        _state.TextAlign = ContentAlignment.MiddleLeft;
        _state.ForeColor = BrandPressed;
        _state.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _state.Text = "이 PC 사용 중  ·  Bluetooth를 확인하고 있어요";
        status.Controls.Add(_state);
        root.Controls.Add(status, 0, 1);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10),
            SizeMode = TabSizeMode.Fixed, ItemSize = new Size(148, 42),
            DrawMode = TabDrawMode.OwnerDrawFixed, Margin = Padding.Empty
        };
        tabs.DrawItem += (_, e) =>
        {
            bool selected = e.Index == tabs.SelectedIndex;
            using var background = new SolidBrush(selected ? Surface : Bg);
            e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font,
                e.Bounds, selected ? BrandPressed : Secondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected)
            {
                using var accent = new SolidBrush(Brand);
                e.Graphics.FillRectangle(accent, e.Bounds.X + 18, e.Bounds.Bottom - 3, e.Bounds.Width - 36, 3);
            }
            if (tabs.Focused && selected) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -5, -5));
        };
        tabs.TabPages.Add(BuildConnectionTab());
        tabs.TabPages.Add(BuildInputTab());
        tabs.TabPages.Add(BuildClipboardTab());
        tabs.TabPages.Add(BuildDiagnosticsTab());
        root.Controls.Add(tabs, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _quickHotkeyHelp.Margin = new Padding(0, 12, 0, 0);
        footer.Controls.Add(_quickHotkeyHelp, 0, 0);
        _toggleButton = ActionButton("휴대폰으로 전환", ToggleAsync, primary: true, critical: true);
        _toggleButton.Enabled = false;
        var back = ActionButton("이 PC로 돌아오기", ReturnLocalAsync, critical: true);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
        actions.Controls.Add(back);
        actions.Controls.Add(_toggleButton);
        footer.Controls.Add(actions, 1, 0);
        _feedback.Dock = DockStyle.Fill;
        _feedback.AutoEllipsis = true;
        _feedback.ForeColor = Secondary;
        _feedback.Text = "휴대폰을 연결하면 여기에서 바로 전환할 수 있어요.";
        footer.Controls.Add(_feedback, 0, 1);
        footer.SetColumnSpan(_feedback, 2);
        root.Controls.Add(footer, 0, 3);

        ConfigureFriendlyLists();
        _mouseSpeed.Width = 280;
        _scrollSpeed.Width = 280;
        _mouseSpeed.TickStyle = TickStyle.None;
        _scrollSpeed.TickStyle = TickStyle.None;
        _mouseSpeed.ValueChanged += (_, _) => _mouseSpeedValue.Text = $"{_mouseSpeed.Value}%";
        _scrollSpeed.ValueChanged += (_, _) => _scrollSpeedValue.Text = $"{_scrollSpeed.Value}%";
        _clipboardList.SelectedIndexChanged += (_, _) => UpdateClipboardDetail();
        UpdateClipboardDetail();
    }

    private TabPage BuildConnectionTab()
    {
        var tab = NewTab("휴대폰 연결");
        var flow = TabFlow();
        tab.Controls.Add(flow);

        var connect = AutoSection();
        connect.Controls.Add(SectionTitle("1. 휴대폰 연결하기"));
        connect.Controls.Add(Help(
            "PC와 휴대폰의 Bluetooth를 켠 뒤, 휴대폰 Bluetooth 설정에서 이 PC를 선택하세요."));
        connect.Controls.Add(Help(
            "처음 연결하나요? 아래 ‘휴대폰 연결 시작’을 누르고 양쪽 화면의 페어링 요청을 확인하세요.",
            Secondary));
        connect.Controls.Add(Row(
            ActionButton("Bluetooth 설정 열기", () => OpenBluetoothSettingsAsync()),
            ActionButton("휴대폰 연결 시작", async () =>
            {
                _servicesPaused = false;
                await _ble.StartAsync();

                if (_settings.ClipboardSyncEnabled && _clipboardSync is not null)
                    await _clipboardSync.SetActiveAsync(true);

                Log("Bluetooth HID/Clipboard 서비스를 시작했습니다.");
            }, primary: true),
            ActionButton("연결 일시 중지", async () =>
            {
                _servicesPaused = true;
                await ReturnLocalAsync();

                if (_clipboardSync is not null)
                    await _clipboardSync.SetActiveAsync(false);

                _ble.Stop();
                Log("Bluetooth HID/Clipboard 서비스를 사용자가 중지했습니다.");
            })));
        flow.Controls.Add(connect);

        var devices = AutoSection();
        devices.Controls.Add(SectionTitle("2. 사용할 휴대폰 선택"));
        devices.Controls.Add(Help(
            $"목록에서 내 휴대폰을 선택하고 ‘이 기기 사용’을 누르세요. 최대 {TrustedDeviceStore.MaxDevices}대를 등록할 수 있어요."));
        devices.Controls.Add(_peers);
        devices.Controls.Add(Row(
            ActionButton("새로 고침", () => RefreshPeersAsync(false)),
            ActionButton("이 기기 사용", ApproveSelectedAsync, primary: true),
            ActionButton("선택 기기로 전환", SelectSelectedTargetAsync),
            ActionButton("기기 등록 해제", RevokeSelectedAsync)));
        flow.Controls.Add(devices);

        var tip = AutoSection(InfoWeak);
        tip.Controls.Add(SectionTitle("이제 PC 키보드와 마우스로"));
        tip.Controls.Add(Help("아래 ‘휴대폰으로 전환’을 누르면 입력이 휴대폰으로 이동합니다. 언제든 PC 복귀 단축키로 돌아오세요."));
        tip.Controls.Add(ActionButton("휴대폰 잠그고 PC로 돌아오기", LockPhoneAndReturnAsync, critical: true));
        flow.Controls.Add(tip);

        return tab;
    }

    private TabPage BuildInputTab()
    {
        var tab = NewTab("입력 설정");
        var flow = TabFlow();
        tab.Controls.Add(flow);

        var pointer = AutoSection();
        pointer.Controls.Add(SectionTitle("마우스 / 스크롤"));
        pointer.Controls.Add(Help(
            "휴대폰에서 움직이는 마우스와 스크롤 속도를 조절하세요. 기본값은 100%입니다."));
        pointer.Controls.Add(LabeledRow("마우스 감도", Row(_mouseSpeed, _mouseSpeedValue)));
        pointer.Controls.Add(LabeledRow("스크롤 감도", Row(_scrollSpeed, _scrollSpeedValue, _invertScroll)));
        pointer.Controls.Add(Row(
            ActionButton("감도 저장", () =>
            {
                ApplyInputSettings(save: true);
                return Task.CompletedTask;
            }, primary: true),
            ActionButton("감도 기본값", () =>
            {
                _mouseSpeed.Value = 100;
                _scrollSpeed.Value = 100;
                _invertScroll.Checked = false;
                ApplyInputSettings(save: true);
                return Task.CompletedTask;
            })));
        flow.Controls.Add(pointer);

        var keyboard = AutoSection();
        keyboard.Controls.Add(SectionTitle("키보드 / 한영키"));
        keyboard.Controls.Add(Help(
            "Galaxy 기본값은 Right Alt입니다. 기기에서 한영 전환이 안 되면 LANG1, Ctrl+Space, Shift+Space 순서로 바꿔 테스트할 수 있습니다."));
        keyboard.Controls.Add(LabeledRow("한영키 전송 방식", _hangulMode));
        keyboard.Controls.Add(LabeledRow("키보드 배열", _modifierProfile));
        keyboard.Controls.Add(Row(
            ActionButton("키보드 설정 저장", () =>
            {
                ApplyInputSettings(save: true);
                return Task.CompletedTask;
            }, primary: true),
            ActionButton("Galaxy 권장값", () =>
            {
                _hangulMode.SelectedIndex = 0;
                ApplyInputSettings(save: true);
                return Task.CompletedTask;
            })));
        flow.Controls.Add(keyboard);

        var special = AutoSection();
        special.Controls.Add(SectionTitle("특수키 / 미디어"));
        special.Controls.Add(Help(
            "연결한 휴대폰이 미디어키를 지원하면 음악과 볼륨을 조절할 수 있어요."));
        special.Controls.Add(Row(
            ActionButton("재생 / 일시정지", () => SendConsumerActionAsync(ConsumerAction.PlayPause), primary: true),
            ActionButton("이전 곡", () => SendConsumerActionAsync(ConsumerAction.PreviousTrack)),
            ActionButton("다음 곡", () => SendConsumerActionAsync(ConsumerAction.NextTrack))));
        special.Controls.Add(Row(
            ActionButton("음소거", () => SendConsumerActionAsync(ConsumerAction.Mute)),
            ActionButton("볼륨 -", () => SendConsumerActionAsync(ConsumerAction.VolumeDown)),
            ActionButton("볼륨 +", () => SendConsumerActionAsync(ConsumerAction.VolumeUp))));
        flow.Controls.Add(special);

        var hotkeys = AutoSection();
        hotkeys.Controls.Add(SectionTitle("단축키"));
        hotkeys.Controls.Add(Help(
            "‘변경’을 누른 뒤 원하는 키 조합을 누르세요. Esc로 취소할 수 있어요."));
        hotkeys.Controls.Add(HotkeyRow("다음 등록 기기", _nextDeviceHotkeyBox, () => BeginHotkeyCapture(HotkeySlot.NextDevice)));
        hotkeys.Controls.Add(HotkeyRow("PC ↔ 휴대폰 전환", _toggleHotkeyBox, () => BeginHotkeyCapture(HotkeySlot.Toggle)));
        hotkeys.Controls.Add(HotkeyRow("비상 노트북 복귀", _emergencyHotkeyBox, () => BeginHotkeyCapture(HotkeySlot.Emergency)));
        hotkeys.Controls.Add(HotkeyRow("휴대폰 잠금 + 복귀", _lockHotkeyBox, () => BeginHotkeyCapture(HotkeySlot.LockAndReturn)));
        hotkeys.Controls.Add(Row(
            ActionButton("단축키 기본값 복원", () =>
            {
                ResetHotkeys();
                return Task.CompletedTask;
            })));
        flow.Controls.Add(hotkeys);

        return tab;
    }

    private TabPage BuildClipboardTab()
    {
        var tab = NewTab("클립보드");
        var flow = TabFlow();
        tab.Controls.Add(flow);

        var info = AutoSection(InfoWeak);
        info.Controls.Add(SectionTitle("복사한 내용을 휴대폰에서도"));
        info.Controls.Add(Help(
            "기기간 복붙을 켜면 PC에서 복사할 때 자동으로 보냅니다. 폰에서 복사한 내용은 Companion을 열거나 공유 → Con.Phone으로 보내세요.",
            TextColor));
        info.Controls.Add(Help(
            "휴대폰에 Con.Phone Companion이 필요해요. 같은 Wi-Fi에 연결하지 않아도 사용할 수 있습니다.",
            Secondary));
        info.Controls.Add(_clipboardConnection);
        flow.Controls.Add(info);

        var history = AutoSection();
        history.Controls.Add(SectionTitle("최근 기록"));
        history.Controls.Add(_clipboardList);
        history.Controls.Add(_clipboardDetail);
        history.Controls.Add(Row(
            ActionButton("지금 복사한 내용 가져오기", () =>
            {
                _clipboard?.CaptureNow();
                return Task.CompletedTask;
            }),
            ClipboardSelectionButton("PC에 다시 복사", () =>
            {
                RestoreSelectedClipboard();
                return Task.CompletedTask;
            }, primary: true),
            ClipboardSelectionButton("휴대폰으로 보내기", SendSelectedClipboardToPhoneAsync),
            ClipboardSelectionButton("삭제", () =>
            {
                DeleteSelectedClipboard();
                return Task.CompletedTask;
            }),
            ActionButton("전체 비우기", () =>
            {
                ClearClipboardHistory();
                return Task.CompletedTask;
            })));
        flow.Controls.Add(history);

        var options = AutoSection();
        options.Controls.Add(SectionTitle("자동 전송 및 기록 설정"));
        options.Controls.Add(Row(_captureClipboardText, _captureClipboardImages));
        options.Controls.Add(Row(_clipboardSyncEnabled, _clipboardSyncText, _clipboardSyncImages));
        options.Controls.Add(LabeledRow("최대 저장 개수", _clipboardLimit));
        options.Controls.Add(LabeledRow("Bluetooth 이미지 한도 (KB)", _clipboardBleImageLimit));
        options.Controls.Add(LabeledRow("빠른 연결 이름", _p2pLinkName));
        options.Controls.Add(Row(
            ActionButton("클립보드 설정 저장", () =>
            {
                ApplyClipboardSettings(save: true);
                return Task.CompletedTask;
            }, primary: true),
            ActionButton("빠른 연결 문제 해결", ConfigureP2PFirewallAsync)));
        options.Controls.Add(Help(
            "P2P 연결은 TCP 47911을 사용합니다. 연결이 계속 실패할 때만 방화벽 허용 버튼을 누르세요. UAC 승인이 필요합니다.",
            Secondary));

        flow.Controls.Add(options);

        return tab;
    }

    private TabPage BuildDiagnosticsTab()
    {
        var tab = NewTab("도움말 · 진단");
        var flow = TabFlow();
        tab.Controls.Add(flow);

        var status = AutoSection();
        status.Controls.Add(SectionTitle("Bluetooth 진단"));
        status.Controls.Add(_diagnosis);
        status.Controls.Add(ActionButton("Bluetooth 다시 검사", async () =>
            _diagnosis.Text = await BleHidServer.DiagnoseAsync()));
        flow.Controls.Add(status);

        var update = AutoSection(InfoWeak);
        update.Controls.Add(SectionTitle("앱 업데이트"));
        update.Controls.Add(Help(
            $"현재 버전 {LocalUpdateService.CurrentVersion.ToString(3)} · 새로 받은 ConPhone.exe를 선택하면 앱을 업데이트합니다.",
            TextColor));
        update.Controls.Add(Row(
            ActionButton("업데이트 EXE 적용", ApplyLocalUpdateAsync, primary: true),
            ActionButton("GitHub 릴리스 열기", () =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/DIA-Universe/Con.Phone/releases",
                    UseShellExecute = true
                });
                return Task.CompletedTask;
            })));
        flow.Controls.Add(update);

        var logs = AutoSection();
        logs.Controls.Add(SectionTitle("실행 로그"));
        logs.Controls.Add(_events);
        logs.Controls.Add(Row(
            ActionButton("진단 파일 저장", SaveDiagnosticsAsync, primary: true),
            ActionButton("로그 지우기", () =>
            {
                _events.Clear();
                return Task.CompletedTask;
            })));
        flow.Controls.Add(logs);

        return tab;
    }

    private static TabPage NewTab(string text) => new()
    {
        Text = text,
        BackColor = Bg,
        Padding = new Padding(0)
    };

    private static FlowLayoutPanel TabFlow()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(14), BackColor = Bg
        };
        flow.SizeChanged += (_, _) => ResizeSections(flow);
        flow.ControlAdded += (_, _) => ResizeSections(flow);
        return flow;
    }

    private static FlowLayoutPanel AutoSection(Color? background = null) => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(860, 0),
        MaximumSize = new Size(860, 0),
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(16),
        Margin = new Padding(0, 0, 0, 14),
        BackColor = background ?? Surface,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static FlowLayoutPanel Section(int height, Color? background = null) => new()
    {
        Width = 860,
        Height = height,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(16),
        Margin = new Padding(0, 0, 0, 14),
        BackColor = background ?? Surface,
        BorderStyle = BorderStyle.FixedSingle
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 12, FontStyle.Bold),
        ForeColor = TextColor,
        Margin = new Padding(0, 0, 0, 6)
    };

    private static Label Help(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(810, 0),
        ForeColor = color ?? Secondary,
        Margin = new Padding(0, 0, 0, 10)
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            MaximumSize = new Size(820, 0),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 4)
        };

        row.Controls.AddRange(controls);
        return row;
    }

    private static FlowLayoutPanel LabeledRow(string label, Control control)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 4)
        };

        row.Controls.Add(new Label
        {
            Text = label,
            AutoSize = false,
            Width = 145,
            Height = 34,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextColor
        });

        row.Controls.Add(control);
        return row;
    }

    private Control HotkeyRow(string label, TextBox box, Action capture)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 5, 0, 5)
        };

        row.Controls.Add(new Label
        {
            Text = label,
            AutoSize = false,
            Width = 185,
            Height = 36,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextColor
        });

        row.Controls.Add(box);

        var change = new Button
        {
            Text = "변경",
            AutoSize = true,
            MinimumSize = new Size(72, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = TextColor
        };
        change.FlatAppearance.BorderColor = Border;
        change.Click += (_, _) =>
        {
            try { capture(); }
            catch (Exception ex) { HandleActionError(ex); }
        };

        row.Controls.Add(change);
        return row;
    }

    private Button ActionButton(string text, Func<Task> action, bool primary = false, bool critical = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(100, 38),
            Padding = new Padding(10, 3, 10, 3),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            BackColor = primary ? Brand : Color.White,
            ForeColor = primary ? Color.White : TextColor
        };

        button.FlatAppearance.BorderColor = primary ? Brand : Border;
        button.FlatAppearance.MouseDownBackColor = primary ? BrandPressed : ColorTranslator.FromHtml("#EEEFF1");
        button.FlatAppearance.MouseOverBackColor = primary ? ColorTranslator.FromHtml("#3B75F0") : ColorTranslator.FromHtml("#F3F4F5");
        button.AccessibleName = text;
        button.Cursor = Cursors.Hand;
        button.EnabledChanged += (_, _) =>
        {
            button.BackColor = button.Enabled ? (primary ? Brand : Surface) : ColorTranslator.FromHtml("#E5E7EB");
            button.ForeColor = button.Enabled ? (primary ? Color.White : TextColor) : Secondary;
        };
        button.Click += (_, _) =>
        {
            if (critical) DispatchCritical(action);
            else Dispatch(action);
        };
        return button;
    }

    private void ConfigureTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("창 열기", null, (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        });
        menu.Items.Add("노트북으로 복귀", null, (_, _) => DispatchCritical(ReturnLocalAsync));
        menu.Items.Add("종료", null, (_, _) =>
        {
            _exitRequested = true;
            Close();
        });

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        };
    }

    private void ConfigureRuntimeEvents()
    {
        _clipboardSyncEnabled.CheckedChanged += (_, _) => ApplyClipboardSettings(save: true);
        _input.Packet += packet =>
        {
            var outgoing = packet is PointerPacket pointer
                ? _pointerScaler.Apply(pointer)
                : packet;

            if (outgoing is PointerPacket scaled)
            {
                var previousButtons = (byte)Interlocked.Exchange(
                    ref _lastObservedPointerButtons,
                    scaled.Buttons);
                var buttonTransition = scaled.Buttons != previousButtons;

                if (!buttonTransition &&
                    scaled.X == 0 &&
                    scaled.Y == 0 &&
                    scaled.Wheel == 0 &&
                    scaled.Pan == 0)
                    return;
            }

            if (_input.IsRemote &&
                !_outbox.TryAdd(new Envelope(Volatile.Read(ref _epoch), outgoing)))
            {
                ImmediateLocal();
                DispatchCritical(async () =>
                {
                    await ReleaseAsync();
                    Log("입력 대기열이 가득 차 노트북 입력으로 복귀했습니다.");
                });
            }
        };

        _input.NextDevice += () => DispatchCritical(SelectNextTrustedTargetAsync);
        _input.Toggle += () => DispatchCritical(ToggleAsync);
        _input.Emergency += () => DispatchCritical(ReturnLocalAsync);
        _input.LockAndReturn += () => DispatchCritical(LockPhoneAndReturnAsync);
        _input.Consumer += action => Post(() => _ = SendConsumerActionSafeAsync(action));
        _input.Fault += text => DispatchCritical(async () =>
        {
            ImmediateLocal();
            await ReleaseAsync();
            Log(text);
        });

        _ble.Status += text => Post(() => Log(text));

        if (_clipboardSync is not null)
        {
            _clipboardSync.Status +=
                text => Post(() => Log(text));
            _clipboardSync.SetReceiver(
                ApplyReceivedClipboardAsync);
        }

        _ble.Changed += () =>
        {
            if (!_input.IsRemote)
                return;

            if (_ble.Ready)
            {
                Interlocked.Exchange(ref _bleNotReadySinceTicks, 0);
                return;
            }

            Interlocked.CompareExchange(
                ref _bleNotReadySinceTicks,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                0);
        };

        _timer.Tick += (_, _) =>
        {
            _clipboardConnection.Text = !_settings.ClipboardSyncEnabled
                ? "동기화 꺼짐 · 아래 기기간 복붙을 켜 주세요."
                : _clipboardSync?.ActiveTargetConnected == true
                    ? "휴대폰 클립보드 채널 연결됨 · 복사한 내용을 자동으로 보냅니다."
                    : "휴대폰 앱 연결 대기 · Companion의 ‘PC 연결 시작’을 누르고 PC에서 기기를 등록해 주세요.";
            if (_input.IsRemote)
            {
                if (_ble.Ready)
                {
                    Interlocked.Exchange(ref _bleNotReadySinceTicks, 0);
                }
                else
                {
                    var since = Interlocked.Read(ref _bleNotReadySinceTicks);
                    if (since == 0)
                    {
                        Interlocked.CompareExchange(
                            ref _bleNotReadySinceTicks,
                            System.Diagnostics.Stopwatch.GetTimestamp(),
                            0);
                    }
                    else
                    {
                        var elapsedMs =
                            (System.Diagnostics.Stopwatch.GetTimestamp() - since) *
                            1000.0 /
                            System.Diagnostics.Stopwatch.Frequency;

                        if (elapsedMs >= 1500)
                        {
                            ImmediateLocal();
                            Log("Bluetooth 입력 준비 상태가 1.5초 이상 끊겨 노트북 입력으로 복귀했습니다.");
                        }
                    }
                }
            }

            var emergency = HotkeyCatalog.Display(_settings.EmergencyHotkey);
            _state.Text = _input.IsRemote
                ? $"휴대폰 조작 중 · {emergency}로 즉시 복귀"
                : _servicesPaused ? "이 PC 사용 중  ·  휴대폰 연결을 일시 중지했어요"
                : _ble.Ready ? "이 PC 사용 중  ·  휴대폰으로 전환할 준비가 되었어요"
                : "이 PC 사용 중  ·  휴대폰을 연결하고 사용할 기기를 선택하세요";
            _toggleButton.Text = _input.IsRemote ? "이 PC로 전환" : "휴대폰으로 전환";
            _toggleButton.Enabled = _input.IsRemote || _ble.Ready;

            _tray.Text = _input.IsRemote
                ? $"Con.Phone · 휴대폰 입력 / {emergency} 복귀"
                : "Con.Phone · 노트북 입력";
        };
        _timer.Start();

        _healthTimer.Tick += (_, _) =>
        {
            _ = SyncLatestClipboardToPhoneSafeAsync(Volatile.Read(ref _epoch), _ble.ApprovedId);
            // Maintenance must not hold the user-action gate while awaiting WinRT.
            if (Interlocked.Exchange(ref _healthCheckRunning, 1) != 0) return;
            _ = CheckTransportHealthAsync();
        };
        _healthTimer.Start();

        _peerTimer.Tick += (_, _) =>
        {
            if (_ble.Advertisement is "Started" or "StartedWithoutAllAdvertisementData")
                _ = RefreshPeersInBackgroundAsync();
        };
        _peerTimer.Start();

        Shown += (_, _) => Dispatch(async () =>
        {
            _servicesPaused = false;
            await _input.Started;
            InitializeClipboardHistory();

            _diagnosis.Text = await BleHidServer.DiagnoseAsync();

            // Keep the GATT database stable before HID advertising begins. The clipboard
            // service is defined now but is only advertised/usable after the user opts in.
            if (_clipboardSync is not null)
                await _clipboardSync.PrepareBleAsync();

            await _ble.StartAsync();
            if (_settings.ClipboardSyncEnabled && _clipboardSync is not null)
                await _clipboardSync.StartAsync();

            Log("Bluetooth HID 검색 신호를 시작했습니다.");
            Log("HID 프로필이 바뀌었습니다. LOCK/MEDIA가 0이면 휴대폰에서 기존 PC 등록을 삭제하고 한 번 다시 페어링해 주세요.");
        });
    }

    private async Task CheckTransportHealthAsync()
    {
        try
        {
            if (!_closing)
            {
                if (!_servicesPaused && _ble.DesiredRunning)
                    await _ble.EnsureAdvertisingAsync();

                if (!_servicesPaused &&
                    _settings.ClipboardSyncEnabled &&
                    _clipboardSync is not null)
                    await _clipboardSync.EnsureHealthyAsync();
            }
        }
        catch (Exception ex) { Log($"연결 상태 확인 재시도: {ex.Message}"); }
        finally { Volatile.Write(ref _healthCheckRunning, 0); }

    }

    private async Task<bool> ApplyReceivedClipboardAsync(
        string sourceDeviceId,
        ClipboardContent content)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (!string.Equals(
                    _ble.ApprovedId,
                    sourceDeviceId,
                    StringComparison.Ordinal))
                return false;

            var applied = await ApplyReceivedClipboardOnUiAsync(
                sourceDeviceId,
                content).ConfigureAwait(false);

            if (applied)
                return true;

            if (attempt < 5)
                await Task.Delay(120).ConfigureAwait(false);
        }

        return false;
    }

    private Task<bool> ApplyReceivedClipboardOnUiAsync(
        string sourceDeviceId,
        ClipboardContent content)
    {
        var completion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        void ApplyOnUi()
        {
            if (_closed ||
                IsDisposed ||
                _clipboard is null ||
                !string.Equals(
                    _ble.ApprovedId,
                    sourceDeviceId,
                    StringComparison.Ordinal))
            {
                completion.TrySetResult(false);
                return;
            }

            try
            {
                var applied =
                    _clipboard.ApplyRemote(
                        content,
                        out var changed);

                if (applied && changed)
                {
                    _latestLocalClipboard = content;
                    var generation =
                        Interlocked.Increment(ref _latestClipboardGeneration);

                    _lastSentClipboardGenerationByDevice[sourceDeviceId] =
                        generation;

                    Log(
                        "휴대폰 클립보드를 Windows에 적용했습니다.");
                }
                else if (applied)
                {
                    // ACK/retry of an already-applied remote value succeeds
                    // without creating a new clipboard generation.
                    var generation =
                        Math.Max(
                            1,
                            Interlocked.Read(
                                ref _latestClipboardGeneration));
                    _lastSentClipboardGenerationByDevice[sourceDeviceId] =
                        generation;
                }

                completion.TrySetResult(applied);
            }
            catch (Exception ex)
            {
                _lastError =
                    $"ClipboardApply:{ex.GetType().Name}:0x{ex.HResult:X8}";
                Log(
                    $"휴대폰 클립보드 적용 실패: {ex.Message}");
                completion.TrySetResult(false);
            }
        }

        if (_closed ||
            IsDisposed ||
            !IsHandleCreated)
        {
            completion.TrySetResult(false);
            return completion.Task;
        }

        try
        {
            if (InvokeRequired)
                BeginInvoke((Action)ApplyOnUi);
            else
                ApplyOnUi();
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private void InitializeClipboardHistory()
    {
        if (_clipboard is not null) return;

        try
        {
            _clipboard = new ClipboardHistoryService(
                _settings.ClipboardHistoryLimit,
                _settings.CaptureClipboardText,
                _settings.CaptureClipboardImages);

            _clipboard.Changed += () => Post(RefreshClipboardList);
            _clipboard.Status += text => Post(() => Log(text));
            _clipboard.ContentCaptured += content => Post(() =>
            {
                _latestLocalClipboard = content;
                Interlocked.Increment(ref _latestClipboardGeneration);

                Log(content.Kind == ClipboardContentKind.TextUtf8
                    ? "텍스트 복사 감지 · 연결된 휴대폰에 동기화 대기"
                    : $"이미지 복사 감지 · {content.Data.Length / 1024.0:N1} KiB · 연결된 휴대폰에 동기화 대기");

                // Copying is independent of who currently owns keyboard/mouse input.
                if (_settings.ClipboardSyncEnabled)
                {
                    _ = SyncLatestClipboardToPhoneSafeAsync(
                        Volatile.Read(ref _epoch),
                        _ble.ApprovedId);
                }
            });

            if (_clipboard.TryGetLatestContent(out var latest))
            {
                _latestLocalClipboard = latest;
                Interlocked.CompareExchange(
                    ref _latestClipboardGeneration,
                    1,
                    0);
            }

            _clipboard.CaptureNow();
            RefreshClipboardList();
        }
        catch (Exception ex)
        {
            Log($"클립보드 기록 시작 실패: {ex.Message}");
        }
    }

    private void LoadActiveDeviceProfileToUi()
    {
        var hangul = _settings.HangulKeyMode;
        var modifiers = _settings.ModifierProfile;

        if (_ble.ApprovedId is { } deviceId)
        {
            var profile = DeviceProfileStore.Get(
                deviceId,
                hangul,
                modifiers);
            hangul = profile.HangulKeyMode;
            modifiers = profile.ModifierProfile;
        }

        _activeHangulMode = hangul;
        _activeModifierProfile = modifiers;

        _hangulMode.SelectedIndex = hangul switch
        {
            HangulKeyMode.RightAlt => 0,
            HangulKeyMode.Lang1 => 1,
            HangulKeyMode.CtrlSpace => 2,
            HangulKeyMode.ShiftSpace => 3,
            _ => 0
        };

        _modifierProfile.SelectedIndex =
            modifiers == ModifierProfile.MacCtrlCommandSwap ? 1 : 0;
    }

    private void RefreshHotkeyUi()
    {
        _nextDeviceHotkeyBox.Text = HotkeyCatalog.Display(_settings.NextDeviceHotkey);
        _toggleHotkeyBox.Text = HotkeyCatalog.Display(_settings.ToggleHotkey);
        _emergencyHotkeyBox.Text = HotkeyCatalog.Display(_settings.EmergencyHotkey);
        _lockHotkeyBox.Text = HotkeyCatalog.Display(_settings.LockAndReturnHotkey);
        _quickHotkeyHelp.Text =
            $"다음 기기 {HotkeyCatalog.Display(_settings.NextDeviceHotkey)} · " +
            $"전환 {HotkeyCatalog.Display(_settings.ToggleHotkey)} · " +
            $"복귀 {HotkeyCatalog.Display(_settings.EmergencyHotkey)} · " +
            $"잠금+복귀 {HotkeyCatalog.Display(_settings.LockAndReturnHotkey)}";
    }

    private void LoadSettingsToUi()
    {
        _mouseSpeed.Value = _settings.MouseSpeedPercent;
        _scrollSpeed.Value = _settings.ScrollSpeedPercent;
        _invertScroll.Checked = _settings.InvertScroll;
        LoadActiveDeviceProfileToUi();

        RefreshHotkeyUi();

        _clipboardLimit.Value = _settings.ClipboardHistoryLimit;
        _captureClipboardText.Checked = _settings.CaptureClipboardText;
        _captureClipboardImages.Checked = _settings.CaptureClipboardImages;
        _clipboardSyncEnabled.Checked = _settings.ClipboardSyncEnabled;
        _clipboardSyncText.Checked = _settings.ClipboardAutoSendText;
        _clipboardSyncImages.Checked = _settings.ClipboardAutoSendImages;
        _clipboardBleImageLimit.Value = _settings.ClipboardBleImageLimitKb;
        _p2pLinkName.Text = _settings.P2PLinkName;

        _mouseSpeedValue.Text = $"{_mouseSpeed.Value}%";
        _scrollSpeedValue.Text = $"{_scrollSpeed.Value}%";
    }

    private void ApplyInputSettings(bool save)
    {
        _settings.MouseSpeedPercent = _mouseSpeed.Value;
        _settings.ScrollSpeedPercent = _scrollSpeed.Value;
        _settings.InvertScroll = _invertScroll.Checked;
        _activeHangulMode = _hangulMode.SelectedIndex switch
        {
            1 => HangulKeyMode.Lang1,
            2 => HangulKeyMode.CtrlSpace,
            3 => HangulKeyMode.ShiftSpace,
            _ => HangulKeyMode.RightAlt
        };
        _activeModifierProfile = _modifierProfile.SelectedIndex == 1
            ? ModifierProfile.MacCtrlCommandSwap
            : ModifierProfile.Standard;

        // With no active trusted target this edits the global fallback profile.
        if (_ble.ApprovedId is null)
        {
            _settings.HangulKeyMode = _activeHangulMode;
            _settings.ModifierProfile = _activeModifierProfile;
        }

        _pointerScaler.Configure(
            _settings.MouseSpeedPercent,
            _settings.ScrollSpeedPercent,
            _settings.InvertScroll);

        _input.ConfigureHotkeys(
            _settings.NextDeviceHotkey,
            _settings.ToggleHotkey,
            _settings.EmergencyHotkey,
            _settings.LockAndReturnHotkey);
        _input.ConfigureKeyboard(
            _activeHangulMode,
            _activeModifierProfile);
        RegisterShortcutFallbacks();

        _mouseSpeedValue.Text = $"{_mouseSpeed.Value}%";
        _scrollSpeedValue.Text = $"{_scrollSpeed.Value}%";
        RefreshHotkeyUi();

        if (save)
        {
            SettingsStore.Save(_settings);

            if (_ble.ApprovedId is { } deviceId)
            {
                DeviceProfileStore.Save(
                    deviceId,
                    _activeHangulMode,
                    _activeModifierProfile);
            }

            Log(
                $"입력 설정 저장 · 마우스 {_settings.MouseSpeedPercent}% · " +
                $"스크롤 {_settings.ScrollSpeedPercent}% · 한영키 {_activeHangulMode} · " +
                $"수정키 {_activeModifierProfile}");
        }
    }

    private enum HotkeySlot
    {
        NextDevice,
        Toggle,
        Emergency,
        LockAndReturn
    }

    private void BeginHotkeyCapture(HotkeySlot slot)
    {
        if (_input.IsRemote)
            throw new InvalidOperationException("단축키를 바꾸기 전에 노트북으로 복귀해 주세요.");

        var box = slot switch
        {
            HotkeySlot.NextDevice => _nextDeviceHotkeyBox,
            HotkeySlot.Toggle => _toggleHotkeyBox,
            HotkeySlot.Emergency => _emergencyHotkeyBox,
            _ => _lockHotkeyBox
        };

        var previous = box.Text;
        box.Text = "키 조합 입력 중…";
        box.BackColor = BrandWeak;

        _input.BeginHotkeyCapture(binding =>
        {
            Post(() =>
            {
                try
                {
                    ValidateHotkeyCandidate(slot, binding);

                    switch (slot)
                    {
                        case HotkeySlot.NextDevice:
                            _settings.NextDeviceHotkey = binding;
                            _nextDeviceHotkeyBox.Text = HotkeyCatalog.Display(binding);
                            break;
                        case HotkeySlot.Toggle:
                            _settings.ToggleHotkey = binding;
                            _toggleHotkeyBox.Text = HotkeyCatalog.Display(binding);
                            break;
                        case HotkeySlot.Emergency:
                            _settings.EmergencyHotkey = binding;
                            _emergencyHotkeyBox.Text = HotkeyCatalog.Display(binding);
                            break;
                        case HotkeySlot.LockAndReturn:
                            _settings.LockAndReturnHotkey = binding;
                            _lockHotkeyBox.Text = HotkeyCatalog.Display(binding);
                            break;
                    }

                    box.BackColor = Color.White;
                    RefreshHotkeyUi();
                    ApplyInputSettings(save: true);
                    Log($"단축키 변경 완료: {HotkeyCatalog.Display(binding)}");
                }
                catch (Exception ex)
                {
                    box.Text = previous;
                    box.BackColor = Color.White;
                    HandleActionError(ex);
                }
            });
        });
    }

    private void ValidateHotkeyCandidate(
        HotkeySlot slot,
        HotkeyBinding candidate)
    {
        if (!HotkeyCatalog.IsAllowedKey(candidate.Key))
            throw new InvalidOperationException("이 키는 단축키로 사용할 수 없습니다.");

        var existing = new[]
        {
            (HotkeySlot.NextDevice, _settings.NextDeviceHotkey, "다음 등록 기기"),
            (HotkeySlot.Toggle, _settings.ToggleHotkey, "PC ↔ 휴대폰 전환"),
            (HotkeySlot.Emergency, _settings.EmergencyHotkey, "비상 복귀"),
            (HotkeySlot.LockAndReturn, _settings.LockAndReturnHotkey, "휴대폰 잠금 + 복귀")
        };

        foreach (var (otherSlot, binding, label) in existing)
        {
            if (otherSlot != slot && HotkeyCatalog.Same(candidate, binding))
                throw new InvalidOperationException(
                    $"이미 '{label}'에 사용 중인 조합입니다.");
        }
    }

    private void ResetHotkeys()
    {
        _settings.NextDeviceHotkey = SettingsStore.DefaultNextDevice();
        _settings.ToggleHotkey = SettingsStore.DefaultToggle();
        _settings.EmergencyHotkey = SettingsStore.DefaultEmergency();
        _settings.LockAndReturnHotkey = SettingsStore.DefaultLockAndReturn();

        RefreshHotkeyUi();
        ApplyInputSettings(save: true);
        Log("단축키를 기본값으로 복원했습니다.");
    }

    private void ApplyClipboardSettings(bool save)
    {
        var previousP2PLinkName = _settings.P2PLinkName;

        _settings.ClipboardHistoryLimit = (int)_clipboardLimit.Value;
        _settings.CaptureClipboardText = _captureClipboardText.Checked;
        _settings.CaptureClipboardImages = _captureClipboardImages.Checked;
        _settings.ClipboardSyncEnabled = _clipboardSyncEnabled.Checked;
        _settings.ClipboardAutoSendText = _clipboardSyncText.Checked;
        _settings.ClipboardAutoSendImages = _clipboardSyncImages.Checked;
        _settings.ClipboardBleImageLimitKb = (int)_clipboardBleImageLimit.Value;
        _settings.P2PLinkName = _p2pLinkName.Text;
        _clipboard?.Configure(
            _settings.ClipboardHistoryLimit,
            _settings.CaptureClipboardText,
            _settings.CaptureClipboardImages);

        if (save)
        {
            SettingsStore.Save(_settings);
            _p2pLinkName.Text = _settings.P2PLinkName;

            if (!string.Equals(
                    previousP2PLinkName,
                    _settings.P2PLinkName,
                    StringComparison.Ordinal))
            {
                _clipboardSync?.TargetChanged();
                Log($"P2P 링크 이름 변경 · DIRECT-CP-{_settings.P2PLinkName}");
            }

            Log($"클립보드 설정 저장 · 최대 {_settings.ClipboardHistoryLimit}개 · 동기화:{_settings.ClipboardSyncEnabled} · BLE 이미지 {_settings.ClipboardBleImageLimitKb} KiB");

            if (_clipboardSync is not null)
            {
                if (_settings.ClipboardSyncEnabled)
                    _ = StartClipboardSyncSafeAsync();
                else
                    _ = _clipboardSync.SetActiveAsync(false);
            }
        }
    }

    private async Task StartClipboardSyncSafeAsync()
    {
        if (_clipboardSync is null || !_settings.ClipboardSyncEnabled)
            return;

        try
        {
            await _clipboardSync.StartAsync();

            using var firewallTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var firewallReady = await P2PFirewallSupport.IsConfiguredAsync(
                firewallTimeout.Token);

            Log(
                firewallReady
                    ? "기기간 복붙 서비스 시작 · Direct Wi-Fi/P2P → BLE · 방화벽 준비됨"
                    : "기기간 복붙 서비스 시작 · Direct Wi-Fi/P2P → BLE · " +
                      "P2P 방화벽 규칙 미확인(큰 이미지 P2P가 실패하면 방화벽 허용을 실행하세요)");
        }
        catch (Exception ex)
        {
            _lastError = $"ClipboardSyncStart:{ex.GetType().Name}:0x{ex.HResult:X8}";
            Log($"기기간 복붙 서비스 시작 실패: {ex.Message}");
        }
    }

    private void RefreshClipboardList()
    {
        if (_clipboard is null || _closed) return;

        var selectedId = (_clipboardList.SelectedItem as ClipboardEntry)?.Id;

        _clipboardList.BeginUpdate();
        try
        {
            _clipboardList.Items.Clear();
            foreach (var entry in _clipboard.Entries)
                _clipboardList.Items.Add(entry);

            if (selectedId is not null)
            {
                for (var i = 0; i < _clipboardList.Items.Count; i++)
                    if (_clipboardList.Items[i] is ClipboardEntry item && item.Id == selectedId)
                        _clipboardList.SelectedIndex = i;
            }

            if (_clipboardList.SelectedIndex < 0 && _clipboardList.Items.Count > 0)
                _clipboardList.SelectedIndex = 0;
        }
        finally
        {
            _clipboardList.EndUpdate();
        }

        UpdateClipboardDetail();
    }

    private void UpdateClipboardDetail()
    {
        foreach (var button in _clipboardSelectionButtons)
            button.Enabled = _clipboardList.SelectedItem is ClipboardEntry;
        if (_clipboardList.SelectedItem is not ClipboardEntry entry)
        {
            _clipboardDetail.Text = "아직 복사한 내용이 없어요. 텍스트나 이미지를 복사하면 여기에 표시됩니다.";
            return;
        }

        _clipboardDetail.Text = entry.Kind == ClipboardEntryKind.Text
            ? $"텍스트 · {entry.CapturedAt:G} · {(entry.Text?.Length ?? 0):N0}자"
            : $"이미지 · {entry.CapturedAt:G} · {entry.Preview}";
    }

    private void RestoreSelectedClipboard()
    {
        if (_clipboardList.SelectedItem is not ClipboardEntry entry) return;
        if (_clipboard?.Restore(entry.Id) != true)
            Log("선택한 클립보드 항목을 복원하지 못했습니다.");
    }

    private void DeleteSelectedClipboard()
    {
        if (_clipboardList.SelectedItem is ClipboardEntry entry)
            _clipboard?.Delete(entry.Id);
    }

    private void ClearClipboardHistory()
    {
        if (MessageBox.Show(
                this,
                "저장된 클립보드 기록을 모두 지울까요?",
                "클립보드 기록 삭제",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) == DialogResult.Yes)
            _clipboard?.Clear();
    }

    private void Post(Action action)
    {
        if (_closed || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void Dispatch(Func<Task> action) => Post(async () =>
    {
        if (!await _actions.WaitAsync(0)) return;

        try
        {
            if (!_closing) await action();
        }
        catch (Exception ex)
        {
            HandleActionError(ex);
        }
        finally
        {
            _actions.Release();
        }
    });

    private void DispatchCritical(Func<Task> action) => Post(async () =>
    {
        await _inputActions.WaitAsync();

        try
        {
            if (!_closing) await action();
        }
        catch (Exception ex)
        {
            HandleActionError(ex);
        }
        finally
        {
            _inputActions.Release();
        }
    });

    private void HandleActionError(Exception ex)
    {
        ImmediateLocal();
        _lastError = $"{ex.GetType().Name}:0x{ex.HResult:X8}";

        var ble = ex.HResult == unchecked((int)0x8000000E)
            ? $" · BLE:{_ble.LastOperation} · 검색:{_ble.Advertisement}"
            : "";

        Log($"{ex.Message} ({_lastError}{ble})");
    }

    private void Log(string text)
    {
        if (_events.TextLength > 16000)
            _events.Clear();

        _events.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\r\n");
        _feedback.Text = text;
    }

    private void ImmediateLocal()
    {
        _input.Deactivate();
        _input.RefreshHooks();
        _ble.SetInteractiveMode(false);
        Interlocked.Exchange(ref _bleNotReadySinceTicks, 0);
        _pointerScaler.Reset();
        Interlocked.Exchange(ref _lastPointerSentTicks, 0);
        Interlocked.Exchange(ref _lastPointerDispatchTicks, 0);
        Interlocked.Exchange(ref _lastObservedPointerButtons, 0);
        Interlocked.Exchange(ref _lastSentPointerButtons, 0);
        Interlocked.Increment(ref _epoch);
        _outbox.Clear();
    }

    private async Task ReleaseAsync()
    {
        if (!_ble.Ready) return;

        long epoch = Volatile.Read(ref _epoch);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));

        try
        {
            await _ble.SendAsync(
                new WireReport(true, new byte[Hid.KeyboardReportLength]),
                () => epoch == Volatile.Read(ref _epoch),
                timeout.Token);

            await _ble.SendAsync(
                new WireReport(false, new byte[Hid.MouseReportLength]),
                () => epoch == Volatile.Read(ref _epoch),
                timeout.Token);
        }
        catch (Exception ex)
        {
            _lastError = $"Release:{ex.GetType().Name}:0x{ex.HResult:X8}";
            Log("휴대폰에 입력 해제 Report를 확인하지 못했지만 Bluetooth 서비스는 유지합니다.");
        }
    }

    private async Task ReturnLocalAsync()
    {
        ImmediateLocal();
        await ReleaseAsync();
    }

    private async Task ToggleAsync()
    {
        if (_input.IsRemote)
        {
            await ReturnLocalAsync();
            return;
        }

        if (!_ble.Ready)
            throw new InvalidOperationException("키보드·마우스 구독과 기기 승인을 먼저 완료하세요.");

        ImmediateLocal();
        long epoch = Volatile.Read(ref _epoch);
        await ReleaseAsync();

        if (epoch != Volatile.Read(ref _epoch) || !_ble.Ready)
            return;

        await _input.RefreshHooksAsync();
        if (epoch != Volatile.Read(ref _epoch) || !_ble.Ready) return;
        _pointerScaler.Reset();
        Interlocked.Exchange(ref _bleNotReadySinceTicks, 0);
        _ble.SetInteractiveMode(true);
        _input.Activate();

        Log(
            $"휴대폰 입력 시작 · 마우스 pacing {_ble.MousePacingIntervalMs}ms · " +
            $"{HotkeyCatalog.Display(_settings.EmergencyHotkey)}로 즉시 복귀");

        // Clipboard data transfer must never block or cancel HID input switching.
        _ = SyncLatestClipboardToPhoneSafeAsync(
            Volatile.Read(ref _epoch),
            _ble.ApprovedId);
    }

    private async Task SyncLatestClipboardToPhoneSafeAsync(
        long epoch,
        string? targetId)
    {
        if (_closing || _servicesPaused || Interlocked.CompareExchange(ref _clipboardSendRunning, 1, 0) != 0) return;
        try
        {
            await SyncLatestClipboardToPhoneAsync();

            if (epoch != Volatile.Read(ref _epoch) ||
                !string.Equals(targetId, _ble.ApprovedId, StringComparison.Ordinal))
                return;
        }
        catch (OperationCanceledException)
        {
            Post(() => Log("클립보드 동기화 시간이 초과됐습니다. HID 입력은 계속 유지합니다."));
        }
        catch (Exception ex)
        {
            _lastError = $"ClipboardSend:{ex.GetType().Name}:0x{ex.HResult:X8}";
            Post(() => Log($"클립보드 동기화 실패 · HID 입력은 유지합니다. ({ex.Message})"));
        }
        finally { Volatile.Write(ref _clipboardSendRunning, 0); }
    }

    private static TimeSpan ClipboardSendTimeout(
        ClipboardContent content) =>
        content.Kind == ClipboardContentKind.TextUtf8
            ? TimeSpan.FromSeconds(12)
            : TimeSpan.FromSeconds(120);

    private async Task SyncLatestClipboardToPhoneAsync()
    {
        if (!_settings.ClipboardSyncEnabled ||
            _clipboardSync is null ||
            !_clipboardSync.ActiveTargetConnected ||
            _latestLocalClipboard is null)
            return;

        var targetId = _ble.ApprovedId;
        if (targetId is null)
            return;

        var content = _latestLocalClipboard;
        var generation =
            Interlocked.Read(ref _latestClipboardGeneration);

        if (generation <= 0)
            generation = 1;

        if (_lastSentClipboardGenerationByDevice.TryGetValue(
                targetId,
                out var lastGeneration) &&
            lastGeneration >= generation)
            return;

        if (content.Kind == ClipboardContentKind.TextUtf8 && !_settings.ClipboardAutoSendText)
            return;
        if (content.Kind != ClipboardContentKind.TextUtf8 && !_settings.ClipboardAutoSendImages)
            return;

        using var timeout = new CancellationTokenSource(
            ClipboardSendTimeout(content));

        if (await _clipboardSync.SendAsync(
                content,
                _settings.ClipboardBleImageLimitKb * 1024,
                timeout.Token) &&
            string.Equals(targetId, _ble.ApprovedId, StringComparison.Ordinal))
            _lastSentClipboardGenerationByDevice[targetId] = generation;
    }

    private async Task SendSelectedClipboardToPhoneAsync()
    {
        if (!_settings.ClipboardSyncEnabled)
            throw new InvalidOperationException("기기간 복붙 사용을 먼저 켜 주세요.");
        if (_clipboardSync is null || _clipboard is null)
            throw new InvalidOperationException("클립보드 동기화가 아직 준비되지 않았습니다.");
        if (_clipboardList.SelectedItem is not ClipboardEntry entry ||
            !_clipboard.TryGetContent(entry.Id, out var content) ||
            content is null)
            throw new InvalidOperationException("보낼 클립보드 항목을 선택해 주세요.");

        var targetId = _ble.ApprovedId
            ?? throw new InvalidOperationException("활성 휴대폰을 먼저 선택해 주세요.");

        using var timeout = new CancellationTokenSource(
            ClipboardSendTimeout(content));

        if (!await _clipboardSync.SendAsync(
                content,
                _settings.ClipboardBleImageLimitKb * 1024,
                timeout.Token))
            throw new InvalidOperationException("현재 사용 가능한 Clipboard 전송 경로가 없습니다.");

        if (string.Equals(targetId, _ble.ApprovedId, StringComparison.Ordinal) &&
            _latestLocalClipboard is { } latestContent &&
            string.Equals(latestContent.Hash, content.Hash, StringComparison.Ordinal))
        {
            var generation =
                Math.Max(1, Interlocked.Read(ref _latestClipboardGeneration));
            _lastSentClipboardGenerationByDevice[targetId] = generation;
        }
    }

    private async Task SendConsumerActionSafeAsync(ConsumerAction action)
    {
        try
        {
            await SendConsumerActionAsync(action);
        }
        catch (Exception ex)
        {
            _lastError = $"Consumer:{ex.GetType().Name}:0x{ex.HResult:X8}";
            Log($"미디어키 전송 실패 · HID 입력은 유지합니다. ({ex.Message})");
        }
    }

    private async Task SendConsumerActionAsync(ConsumerAction action)
    {
        if (!_ble.Ready)
            throw new InvalidOperationException("미디어키를 보낼 활성 휴대폰 연결이 없습니다.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        await _ble.SendConsumerAsync(action, timeout.Token);
        Log($"미디어키 전송 · {action}");
    }

    private async Task LockPhoneAndReturnAsync()
    {
        ImmediateLocal();
        await ReleaseAsync();

        if (!_ble.Ready)
        {
            Log("노트북 입력으로 복귀했습니다. 잠금 키를 보낼 휴대폰 연결은 없습니다.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        try
        {
            await _ble.SendSystemLockAsync(timeout.Token);
            Log("Galaxy 잠금 WIN+L을 보내고, 지원 시 System Sleep까지 보조 전송한 뒤 노트북 입력으로 복귀했습니다.");
        }
        catch (NotSupportedException ex)
        {
            Log($"휴대폰 잠금 보조 기능 미지원: {ex.Message}");
        }
        catch (Exception ex)
        {
            _lastError = $"LockReturn:{ex.GetType().Name}:0x{ex.HResult:X8}";
            Log($"노트북 입력으로는 복귀했지만 휴대폰 잠금 전송을 확인하지 못했습니다. ({ex.Message})");
        }
    }

    private async Task ApproveSelectedAsync()
    {
        await ReturnLocalAsync();

        if (_peers.SelectedItem is not Peer peer)
            throw new InvalidOperationException("기기를 먼저 선택해 주세요.");

        if (MessageBox.Show(
                this,
                $"본인 휴대폰이 맞습니까?\r\n{peer}",
                "입력 대상 승인",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        await _ble.ApproveAsync(peer.Id);
        _ = ClipboardPairingSecretStore.GetOrCreate(peer.Id);
        _clipboardSync?.TargetChanged();
        LoadActiveDeviceProfileToUi();
        ApplyInputSettings(save: false);
        await RefreshPeersAsync(true);

        Log(
            $"기기 등록/활성화 완료 · 등록 {_ble.TrustedDeviceCount}/{TrustedDeviceStore.MaxDevices} · " +
            $"{HotkeyCatalog.Display(_settings.ToggleHotkey)}로 휴대폰 입력을 시작할 수 있습니다.");
    }

    private async Task SelectNextTrustedTargetAsync()
    {
        var wasRemote = _input.IsRemote;

        var peers = await _ble.GetPeersAsync();
        var readyById = peers
            .Where(peer =>
                peer.Trusted &&
                peer.Keyboard &&
                peer.Mouse)
            .ToDictionary(
                peer => peer.Id,
                peer => peer,
                StringComparer.Ordinal);

        if (readyById.Count == 0)
        {
            Log("전환 가능한 등록 기기가 없습니다.");
            return;
        }

        // Cycle in the stable registration order, not the UI order.
        // GetPeersAsync deliberately moves the active peer to the top for display,
        // which otherwise makes F7 bounce between only two devices.
        var registrationOrder = TrustedDeviceStore.LoadAll();
        var nextId = TargetCycle.Next(
            registrationOrder,
            readyById.Keys.ToHashSet(StringComparer.Ordinal),
            _ble.ApprovedId);

        if (nextId is null ||
            !readyById.TryGetValue(nextId, out var next))
        {
            Log("전환 가능한 등록 기기가 없습니다.");
            return;
        }

        if (readyById.Count == 1 &&
            string.Equals(next.Id, _ble.ApprovedId, StringComparison.Ordinal))
        {
            Log($"등록 기기 1대 · 활성 대상 유지: {next.Name} [{next.Fingerprint}]");
            return;
        }

        if (wasRemote)
        {
            // Stop capture and explicitly release old-target state before changing
            // the active GATT client. This prevents stuck modifiers/buttons.
            ImmediateLocal();
            await ReleaseAsync();
        }

        await _ble.SelectTargetAsync(next.Id);
        _clipboardSync?.TargetChanged();
        LoadActiveDeviceProfileToUi();
        ApplyInputSettings(save: false);
        await RefreshPeersAsync(true);

        if (wasRemote && _ble.Ready)
        {
            _pointerScaler.Reset();
            _ble.SetInteractiveMode(true);
            _input.Activate(preserveLocalModifierUps: false);

            _ = SyncLatestClipboardToPhoneSafeAsync(
                Volatile.Read(ref _epoch),
                _ble.ApprovedId);
        }

        Log(
            $"다음 기기로 전환 · {next.Name} [{next.Fingerprint}] · " +
            $"입력:{(wasRemote ? "휴대폰 유지" : "노트북")} · " +
            $"등록 {_ble.TrustedDeviceCount}/{TrustedDeviceStore.MaxDevices}");
    }

    private async Task SelectSelectedTargetAsync()
    {
        await ReturnLocalAsync();

        if (_peers.SelectedItem is not Peer peer)
            throw new InvalidOperationException("활성 대상으로 바꿀 기기를 선택해 주세요.");
        if (!peer.Trusted)
            throw new InvalidOperationException("먼저 이 기기를 등록해 주세요.");

        await _ble.SelectTargetAsync(peer.Id);
        _clipboardSync?.TargetChanged();
        LoadActiveDeviceProfileToUi();
        ApplyInputSettings(save: false);
        await RefreshPeersAsync(true);

        Log($"활성 휴대폰 전환 완료 · {peer.Name} [{peer.Fingerprint}]");
    }

    private async Task RevokeSelectedAsync()
    {
        await ReturnLocalAsync();

        if (_peers.SelectedItem is not Peer peer)
            throw new InvalidOperationException("해제할 기기를 선택해 주세요.");
        if (!peer.Trusted)
            throw new InvalidOperationException("선택한 기기는 등록된 신뢰 기기가 아닙니다.");

        var revokedId = _ble.Revoke(peer.Id);
        if (revokedId is not null)
        {
            ClipboardPairingSecretStore.Forget(revokedId);
            DeviceProfileStore.Remove(revokedId);
            _lastSentClipboardGenerationByDevice.TryRemove(revokedId, out _);
        }

        _clipboardSync?.TargetChanged();
        LoadActiveDeviceProfileToUi();
        ApplyInputSettings(save: false);
        await RefreshPeersAsync(true);

        Log(
            $"기기 해제 완료 · 남은 등록 {_ble.TrustedDeviceCount}/{TrustedDeviceStore.MaxDevices}");
    }

    private async Task RefreshPeersInBackgroundAsync()
    {
        if (_closing || Interlocked.Exchange(ref _peerRefreshRunning, 1) != 0) return;
        try { await RefreshPeersAsync(true); }
        catch (Exception ex) { if (!_closing) Log($"기기 목록 조회 재시도: {ex.Message}"); }
        finally { Volatile.Write(ref _peerRefreshRunning, 0); }
    }

    private async Task RefreshPeersAsync(bool silent = false)
    {
        var selectedId = (_peers.SelectedItem as Peer)?.Id;
        var peers = await _ble.GetPeersAsync();
        if (_closing || IsDisposed) return;

        _peers.BeginUpdate();
        try
        {
            _peers.Items.Clear();
            foreach (var peer in peers)
                _peers.Items.Add(peer);

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
            Log("HID 구독 기기가 없습니다. 휴대폰 Bluetooth 페어링과 '입력 장치' 상태를 확인하세요.");
    }

    private bool IsPointerRecentlyActive()
    {
        if (!_input.IsRemote)
            return false;

        var last = Interlocked.Read(ref _lastPointerSentTicks);
        if (last == 0)
            return false;

        var elapsedMs =
            (System.Diagnostics.Stopwatch.GetTimestamp() - last) *
            1000.0 /
            System.Diagnostics.Stopwatch.Frequency;

        return elapsedMs < 350;
    }

    private async Task PacePointerAsync(
        PointerPacket pointer,
        CancellationToken token)
    {
        var previousButtons = (byte)Volatile.Read(ref _lastSentPointerButtons);
        var urgent =
            pointer.Buttons != previousButtons ||
            pointer.Wheel != 0 ||
            pointer.Pan != 0;

        if (urgent || (pointer.X == 0 && pointer.Y == 0))
            return;

        var last = Interlocked.Read(ref _lastPointerDispatchTicks);
        if (last == 0) return;

        var elapsedMs =
            (System.Diagnostics.Stopwatch.GetTimestamp() - last) *
            1000.0 /
            System.Diagnostics.Stopwatch.Frequency;

        var delayMs = _ble.MousePacingIntervalMs - elapsedMs;
        if (delayMs > 0.5)
            await Task.Delay(
                TimeSpan.FromMilliseconds(Math.Ceiling(delayMs)),
                token).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var envelope = await _outbox.TakeAsync(_lifetime.Token);
                var pointer = envelope.Packet as PointerPacket;
                if (pointer is not null)
                {
                    await PacePointerAsync(pointer, _lifetime.Token).ConfigureAwait(false);
                    envelope = _outbox.CoalescePendingMotion(envelope);
                    pointer = (PointerPacket)envelope.Packet;
                }

                foreach (var report in Hid.Encode(envelope.Packet))
                {
                    if (envelope.Epoch != Volatile.Read(ref _epoch) || !_input.IsRemote) break;
                    // Cadence is start-to-start: awaiting Notify already consumes radio time.
                    if (pointer is not null)
                        Interlocked.Exchange(ref _lastPointerDispatchTicks,
                            System.Diagnostics.Stopwatch.GetTimestamp());

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(750);

                    await _ble.SendAsync(
                        report,
                        () =>
                            envelope.Epoch == Volatile.Read(ref _epoch) &&
                            _input.IsRemote,
                        timeout.Token);

                    Interlocked.Increment(ref _sent);

                    if (pointer is not null)
                    {
                        // Update after every emitted mouse report, not once per source packet.
                        // A large coalesced move can encode into several HID reports; each
                        // subsequent pure-motion report must respect the negotiated BLE cadence.
                        Volatile.Write(ref _lastSentPointerButtons, pointer.Buttons);
                        Interlocked.Exchange(
                            ref _lastPointerSentTicks,
                            System.Diagnostics.Stopwatch.GetTimestamp());
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ImmediateLocal();
                _lastError = $"Send:{ex.GetType().Name}:0x{ex.HResult:X8}";

                Post(() => Log(
                    "Bluetooth 전송 실패/지연으로 노트북 입력으로 복귀했습니다. 서비스는 유지하며 재연결을 기다립니다."));
            }
        }
    }

    private async Task ConfigureP2PFirewallAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await P2PFirewallSupport.ConfigureAsync(timeout.Token);

        using var verifyTimeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var verified = await P2PFirewallSupport.IsConfiguredAsync(
            verifyTimeout.Token);

        if (!verified)
            throw new InvalidOperationException(
                "방화벽 규칙을 추가했지만 활성 상태를 다시 확인하지 못했습니다.");

        Log($"Windows 방화벽 P2P 허용/검증 완료 · TCP {P2PFirewallSupport.Port}");
    }

    private Task ApplyLocalUpdateAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Con.Phone 실행 파일 (ConPhone*.exe)|ConPhone*.exe|실행 파일 (*.exe)|*.exe",
            Title = "적용할 Con.Phone 업데이트 파일 선택"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return Task.CompletedTask;

        var candidate = LocalUpdateService.Inspect(dialog.FileName);
        var current = LocalUpdateService.CurrentVersion;

        if (!candidate.SignatureVerified)
        {
            if (MessageBox.Show(
                    this,
                    "현재 Con.Phone 또는 선택한 업데이트가 아직 Authenticode 코드서명되지 않아 " +
                    "동일 서명자 검증을 완료할 수 없습니다.\r\n" +
                    "SHA-256 파일 일치만 확인된 상태입니다. 신뢰하는 배포 파일일 때만 계속하세요.",
                    "업데이트 서명 확인",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                return Task.CompletedTask;
        }

        if (candidate.Version <= current)
        {
            if (MessageBox.Show(
                    this,
                    $"선택한 버전 {candidate.Version}은 현재 버전 {current}보다 새 버전이 아닙니다. 그래도 적용할까요?",
                    "업데이트 확인",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                return Task.CompletedTask;
        }
        else if (MessageBox.Show(
                     this,
                     $"Con.Phone {current} → {candidate.Version}으로 업데이트할까요?\r\n" +
                     "앱을 종료한 뒤 자동으로 교체하고 다시 실행합니다.",
                     "업데이트",
                     MessageBoxButtons.YesNo,
                     MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return Task.CompletedTask;
        }

        var staged = LocalUpdateService.Stage(candidate);
        LocalUpdateService.LaunchInstaller(
            staged,
            candidate.Sha256);
        Log(
            $"업데이트 적용 시작 · {current} → {candidate.Version} · " +
            $"SHA256:{candidate.Sha256[..12]}… · 체크섬:{candidate.ChecksumVerified} · " +
            $"동일서명자:{candidate.SignatureVerified}");

        _exitRequested = true;
        BeginInvoke(Close);
        return Task.CompletedTask;
    }

    private async Task SaveDiagnosticsAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = "ConPhone-diagnostics.json"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var report = new
        {
            Version = LocalUpdateService.CurrentVersion.ToString(),
            OS = Environment.OSVersion.Version.ToString(),
            Runtime = Environment.Version.ToString(),
            Bluetooth = await BleHidServer.DiagnoseAsync(),
            Advertisement = _ble.Advertisement,
            BleLastOperation = _ble.LastOperation,
            HidAutoRecoveryAttempts = _ble.AutoRecoveryAttempts,
            HidAutoRecoverySuccesses = _ble.AutoRecoverySuccesses,
            HidLastRecoverySuccess = _ble.LastRecoverySuccess?.ToString("O") ?? "",
            KeyboardSubscribers = _ble.KeyboardSubscribers,
            MouseSubscribers = _ble.MouseSubscribers,
            SystemSubscribers = _ble.SystemSubscribers,
            ConsumerSubscribers = _ble.ConsumerSubscribers,
            ApprovedAndSubscribed = _ble.Ready,
            ServicesPausedByUser = _servicesPaused,
            TrustedDeviceCount = _ble.TrustedDeviceCount,
            ActiveDeviceFingerprint = _ble.ApprovedId is { } activeDeviceId
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(activeDeviceId)))[..8]
                : "",
            SendOperations = Interlocked.Read(ref _sent),
            Pending = _outbox.Count,
            LastError = _lastError,
            MouseSpeedPercent = _settings.MouseSpeedPercent,
            ScrollSpeedPercent = _settings.ScrollSpeedPercent,
            MousePacingIntervalMs = _ble.MousePacingIntervalMs,
            HangulKeyMode = _activeHangulMode.ToString(),
            ModifierProfile = _activeModifierProfile.ToString(),
            NextDeviceHotkey = HotkeyCatalog.Display(_settings.NextDeviceHotkey),
            ToggleHotkey = HotkeyCatalog.Display(_settings.ToggleHotkey),
            EmergencyHotkey = HotkeyCatalog.Display(_settings.EmergencyHotkey),
            LockAndReturnHotkey = HotkeyCatalog.Display(_settings.LockAndReturnHotkey),
            ClipboardEntries = _clipboard?.Entries.Count ?? 0,
            ClipboardSyncEnabled = _settings.ClipboardSyncEnabled,
            ClipboardBleSubscribers = _clipboardSync?.BleSubscribers ?? 0,
            ClipboardBleImageLimitKb = _settings.ClipboardBleImageLimitKb,
            ClipboardP2PLinkRunning = _clipboardSync?.P2PLinkRunning ?? false,
            ClipboardP2PChannelConnected = _clipboardSync?.P2PChannelConnected ?? false,
            ClipboardP2PHostReady = !string.IsNullOrWhiteSpace(_clipboardSync?.P2PHostAddress),
            Privacy = "진단 파일에는 클립보드 내용, 입력 문자, 기기 이름, Bluetooth 주소를 저장하지 않습니다."
        };

        await File.WriteAllTextAsync(
            dialog.FileName,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }));

        Log("진단 저장 완료.");
    }

    private void PowerChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            ImmediateLocal();
            Log("절전 진입 · 입력만 노트북으로 복귀하고 Bluetooth provider는 OS에 맡깁니다.");
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Dispatch(async () =>
            {
                if (_servicesPaused)
                {
                    Log("절전 복귀 · 사용자가 서비스를 중지한 상태를 유지합니다.");
                    return;
                }

                await Task.Delay(1200);
                await _ble.StartAsync();

                if (_settings.ClipboardSyncEnabled && _clipboardSync is not null)
                    await _clipboardSync.EnsureHealthyAsync();

                Log("절전 복귀 · Bluetooth HID/Clipboard 검색 신호를 다시 확인했습니다.");
            });
        }
    }

    private void SessionChanged(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            ImmediateLocal();
            Log("Windows 잠금 감지 · Bluetooth 승인/검색 신호는 유지하고 입력만 노트북으로 복귀했습니다.");
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
            _tray.ShowBalloonTip(
                2200,
                "Con.Phone 계속 실행 중",
                "Bluetooth 연결 유지를 위해 트레이에서 계속 실행합니다. 완전 종료는 트레이 메뉴를 사용하세요.",
                ToolTipIcon.Info);
            return;
        }

        if (_closed) return;

        e.Cancel = true;
        if (_closing) return;
        _closing = true;

        await ReturnLocalAsync();

        _lifetime.Cancel();
        _input.Dispose();
        _clipboard?.Dispose();
        if (_clipboardSync is not null)
            await _clipboardSync.DisposeAsync();
        _ble.Dispose();

        if (_pump is not null)
        {
            try { await _pump; }
            catch (OperationCanceledException) { }
        }

        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.SessionSwitch -= SessionChanged;

        _timer.Stop();
        _timer.Dispose();
        _healthTimer.Stop();
        _healthTimer.Dispose();
        _peerTimer.Stop();
        _peerTimer.Dispose();

        _tray.Visible = false;
        _tray.Dispose();

        _closed = true;
        Close();
    }
}
