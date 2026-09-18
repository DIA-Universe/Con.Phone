using System.Diagnostics;
using System.Text.Json;
using ConPhone.Core;
using Microsoft.Win32;

namespace ConPhone.Windows;

public sealed class MainForm : Form
{
    private const int PointerFrameMilliseconds = 6;

    private static readonly Color Bg = ColorTranslator.FromHtml("#F7F8F9");
    private static readonly Color Surface = Color.White;
    private static readonly Color Border = ColorTranslator.FromHtml("#DCDEE3");
    private static readonly Color TextColor = ColorTranslator.FromHtml("#1A1C20");
    private static readonly Color Secondary = ColorTranslator.FromHtml("#555D6D");
    private static readonly Color Tertiary = ColorTranslator.FromHtml("#868B94");
    private static readonly Color Brand = ColorTranslator.FromHtml("#FF6600");
    private static readonly Color BrandPressed = ColorTranslator.FromHtml("#E14D00");
    private static readonly Color BrandWeak = ColorTranslator.FromHtml("#FFF2EC");
    private static readonly Color InfoWeak = ColorTranslator.FromHtml("#EFF6FF");

    private readonly BleHidServer _ble = new();
    private readonly InputCapture _input = new();
    private readonly Outbox _outbox = new();
    private readonly PointerScaler _pointerScaler = new();
    private readonly ConPhoneSettings _settings = SettingsStore.Load();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _actions = new(1, 1);

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

    private readonly ComboBox _targetProfile = TargetProfileBox();
    private readonly CheckBox _appleSwapCommandOption = new()
    {
        Text = "Apple 키 배열: Alt=Command, Win=Option",
        AutoSize = true,
        ForeColor = TextColor
    };
    private readonly ComboBox[] _specialKeyBoxes = SpecialKeyBoxes();

    private readonly TextBox _toggleHotkeyBox = HotkeyBox();
    private readonly TextBox _emergencyHotkeyBox = HotkeyBox();
    private readonly TextBox _lockHotkeyBox = HotkeyBox();
    private readonly Label _connectionShortcutSummary = Help("");

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

    private ClipboardHistoryService? _clipboard;

    private readonly NotifyIcon _tray = new()
    {
        Text = "Con.Phone · 노트북 입력",
        Icon = SystemIcons.Application,
        Visible = true
    };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 750 };
    private readonly System.Windows.Forms.Timer _peerTimer = new() { Interval = 5000 };

    private readonly bool _previewMode;
    private Task? _pump;
    private long _epoch, _sent;
    private bool _closing, _closed, _exitRequested;
    private string _lastError = "None";

    public MainForm(bool previewMode = false)
    {
        _previewMode = previewMode;
        Text = "Con.Phone 0.2.2 · Bluetooth 입력 허브";
        Width = 980;
        Height = 800;
        MinimumSize = new Size(880, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Bg;
        ForeColor = TextColor;
        AutoScaleMode = AutoScaleMode.Dpi;

        LoadSettingsToUi();
        ApplyInputSettings(save: false);
        BuildUi();

        if (_previewMode)
        {
            _tray.Visible = false;
            _state.Text = "노트북 조작 중 · 광고:Started · K:1 M:1 LOCK:1 · 승인:준비됨";
            _peers.Items.Add(new Peer(
                "preview-device",
                "Galaxy / Android",
                "A1B2C3D4",
                Keyboard: true,
                Mouse: true,
                SystemControl: true,
                ConsumerControl: true,
                Paired: true,
                Connected: true));
            _peers.SelectedIndex = 0;
            _diagnosis.Text = "Bluetooth LE: True\r\nPeripheral 역할: True\r\n무선 상태: On";
        }
        else
        {
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

    private sealed record SpecialActionChoice(SpecialKeyAction Action, string Label)
    {
        public override string ToString() => Label;
    }

    private static ComboBox TargetProfileBox()
    {
        var box = new ComboBox
        {
            Width = 260,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        box.Items.AddRange(["Android", "iPhone / iPad", "Mac"]);
        box.SelectedIndex = 0;
        return box;
    }

    private static ComboBox[] SpecialKeyBoxes()
    {
        return Enumerable.Range(0, 12)
            .Select(_ =>
            {
                var box = new ComboBox
                {
                    Width = 260,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                foreach (var action in Enum.GetValues<SpecialKeyAction>())
                    box.Items.Add(new SpecialActionChoice(action, SpecialKeyCatalog.Display(action)));
                box.SelectedIndex = 0;
                return box;
            })
            .ToArray();
    }

    private static SpecialKeyAction SelectedSpecialAction(ComboBox box) =>
        box.SelectedItem is SpecialActionChoice choice
            ? choice.Action
            : SpecialKeyAction.None;

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 18, 20, 20),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Bg
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };

        header.Controls.Add(new Label
        {
            Text = "Con.Phone",
            AutoSize = true,
            Font = new Font("Segoe UI", 24, FontStyle.Bold),
            ForeColor = TextColor,
            Margin = new Padding(0)
        });

        header.Controls.Add(new Label
        {
            Text = "노트북의 키보드·마우스를 Bluetooth HID로 휴대폰과 공유합니다.",
            AutoSize = true,
            ForeColor = Secondary,
            Margin = new Padding(1, 4, 0, 0)
        });

        root.Controls.Add(header, 0, 0);

        _state.Margin = new Padding(1, 14, 0, 12);
        root.Controls.Add(_state, 0, 1);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10),
            Padding = new Point(18, 7)
        };

        tabs.TabPages.Add(BuildConnectionTab());
        tabs.TabPages.Add(BuildInputTab());
        tabs.TabPages.Add(BuildClipboardTab());
        tabs.TabPages.Add(BuildDiagnosticsTab());
        root.Controls.Add(tabs, 0, 2);

        _mouseSpeed.ValueChanged += (_, _) => _mouseSpeedValue.Text = $"{_mouseSpeed.Value}%";
        _scrollSpeed.ValueChanged += (_, _) => _scrollSpeedValue.Text = $"{_scrollSpeed.Value}%";
        _clipboardList.SelectedIndexChanged += (_, _) => UpdateClipboardDetail();
    }

    private TabPage BuildConnectionTab()
    {
        var tab = NewTab("연결");
        var flow = TabFlow();
        tab.Controls.Add(flow);

        var connect = Section(170);
        connect.Controls.Add(SectionTitle("Bluetooth 연결"));
        connect.Controls.Add(Help(
            "0.2.2는 한/영 LANG1·LANG2와 Consumer Control 특수키 Report를 포함합니다. HID descriptor가 바뀌므로 이 버전 첫 사용 시 기존 PC 등록을 삭제하고 한 번 다시 페어링해 주세요."));
        connect.Controls.Add(Row(
            ActionButton("Bluetooth 다시 검사", async () => _diagnosis.Text = await BleHidServer.DiagnoseAsync()),
            ActionButton("광고 확인 / 시작", async () =>
            {
                await _ble.StartAsync();
                Log("Bluetooth HID 광고 상태를 확인했습니다.");
            }, primary: true),
            ActionButton("서비스 중지", async () =>
            {
                await ReturnLocalAsync();
                _ble.Stop();
            }),
            ActionButton("업데이트 페이지", OpenUpdatePageAsync)));
        flow.Controls.Add(connect);

        var devices = Section(245);
        devices.Controls.Add(SectionTitle("연결된 휴대폰"));
        devices.Controls.Add(Help(
            "여러 기기가 동시에 페어링/구독돼도 한 번에 승인된 1대에만 입력을 보냅니다. K/M은 키보드·마우스, LOCK은 잠금, C는 Consumer Control 특수키 구독 상태입니다."));
        devices.Controls.Add(_peers);
        devices.Controls.Add(Row(
            ActionButton("목록 갱신", () => RefreshPeersAsync(false)),
            ActionButton("선택한 기기로 전환 / 승인", ApproveSelectedAsync, primary: true),
            ActionButton("승인 해제", async () =>
            {
                await ReturnLocalAsync();
                _ble.Revoke();
            })));
        flow.Controls.Add(devices);

        var quick = Section(150);
        quick.Controls.Add(SectionTitle("빠른 동작"));
        RefreshConnectionShortcutSummary();
        quick.Controls.Add(_connectionShortcutSummary);
        quick.Controls.Add(Row(
            ActionButton("휴대폰으로 전환", ToggleAsync, primary: true),
            ActionButton("노트북으로 복귀", ReturnLocalAsync),
            ActionButton("휴대폰 잠금 + 복귀", LockPhoneAndReturnAsync)));
        flow.Controls.Add(quick);

        return tab;
    }

    private void RefreshConnectionShortcutSummary()
    {
        _connectionShortcutSummary.Text =
            $"전환 {HotkeyCatalog.Display(_settings.ToggleHotkey)} · " +
            $"복귀 {HotkeyCatalog.Display(_settings.EmergencyHotkey)} · " +
            $"잠금+복귀 {HotkeyCatalog.Display(_settings.LockAndReturnHotkey)}";
    }

    private TabPage BuildInputTab()
    {
        var tab = NewTab("입력 설정");
        var flow = TabFlow();
        tab.Controls.Add(flow);

        var compatibility = Section(225);
        compatibility.Controls.Add(SectionTitle("Android / iOS / Mac 호환성"));
        compatibility.Controls.Add(Help(
            "대상 프로필은 안내와 키 배열 설정에 사용합니다. iPhone/iPad는 Eject 특수키로 화면 키보드를 호출할 수 있고, Mac 모드는 필요할 때 Alt/Win을 Command/Option 위치로 바꿉니다."));
        compatibility.Controls.Add(LabeledRow("대상 프로필", _targetProfile));
        compatibility.Controls.Add(_appleSwapCommandOption);
        compatibility.Controls.Add(Row(
            ActionButton("호환성 설정 저장", () =>
            {
                ApplyInputSettings(save: true);
                return Task.CompletedTask;
            }, primary: true)));
        flow.Controls.Add(compatibility);

        var pointer = Section(235);
        pointer.Controls.Add(SectionTitle("마우스 / 스크롤"));
        pointer.Controls.Add(Help(
            "Windows 마우스 설정은 건드리지 않고 휴대폰으로 보내는 상대 좌표만 보정합니다. 100%가 원본 Raw Input입니다."));
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

        var hotkeys = Section(305);
        hotkeys.Controls.Add(SectionTitle("단축키"));
        hotkeys.Controls.Add(Help(
            "드롭다운이 아니라 실제 키보드 조합을 읽습니다. ‘변경’을 누른 뒤 Ctrl·Alt·Shift·Win을 포함한 원하는 조합을 그대로 누르세요."));
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

        var special = Section(690);
        special.Controls.Add(SectionTitle("F1–F12 특수키 매핑"));
        special.Controls.Add(Help(
            "EasyBlue 방식처럼 F1–F12에 Consumer Control 동작을 지정합니다. Eject는 iPhone/iPad 화면 키보드, Power+는 휴대폰 잠금 후 PC 복귀에 사용합니다. F8/F9 기본 전환키와 같은 키를 매핑하면 전환 단축키가 우선합니다."));
        for (var i = 0; i < _specialKeyBoxes.Length; i++)
            special.Controls.Add(LabeledRow($"F{i + 1}", _specialKeyBoxes[i]));
        special.Controls.Add(Row(
            ActionButton("특수키 저장", () =>
            {
                ApplyInputSettings(save: true);
                return Task.CompletedTask;
            }, primary: true),
            ActionButton("특수키 모두 해제", () =>
            {
                foreach (var box in _specialKeyBoxes) box.SelectedIndex = 0;
                ApplyInputSettings(save: true);
                return Task.CompletedTask;
            })));
        flow.Controls.Add(special);

        return tab;
    }

    private TabPage BuildClipboardTab()
    {
        var tab = NewTab("클립보드");
        var flow = TabFlow();
        tab.Controls.Add(flow);

        var info = Section(145, InfoWeak);
        info.Controls.Add(SectionTitle("Windows 클립보드 기록"));
        info.Controls.Add(Help(
            "텍스트와 스크린샷/이미지를 이 PC에만 보관하고, 나중에 다시 Windows 클립보드로 복원해 다른 프로그램에 붙여넣을 수 있습니다.",
            TextColor));
        info.Controls.Add(Help(
            "휴대폰에서 복사한 실제 내용은 Bluetooth HID만으로 PC에 전달되지 않습니다. 휴대폰 → PC 자동 동기화는 Android 보조앱과 별도 데이터 채널이 필요합니다.",
            Secondary));
        flow.Controls.Add(info);

        var history = Section(420);
        history.Controls.Add(SectionTitle("최근 기록"));
        history.Controls.Add(_clipboardList);
        history.Controls.Add(_clipboardDetail);
        history.Controls.Add(Row(
            ActionButton("현재 클립보드 가져오기", () =>
            {
                _clipboard?.CaptureNow();
                return Task.CompletedTask;
            }),
            ActionButton("선택 항목 복원", () =>
            {
                RestoreSelectedClipboard();
                return Task.CompletedTask;
            }, primary: true),
            ActionButton("삭제", () =>
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

        var options = Section(145);
        options.Controls.Add(SectionTitle("기록 설정"));
        options.Controls.Add(Row(_captureClipboardText, _captureClipboardImages));
        options.Controls.Add(LabeledRow("최대 저장 개수", _clipboardLimit));
        options.Controls.Add(ActionButton("클립보드 설정 저장", () =>
        {
            ApplyClipboardSettings(save: true);
            return Task.CompletedTask;
        }, primary: true));
        flow.Controls.Add(options);

        return tab;
    }

    private TabPage BuildDiagnosticsTab()
    {
        var tab = NewTab("진단");
        var flow = TabFlow();
        tab.Controls.Add(flow);

        var status = Section(205);
        status.Controls.Add(SectionTitle("Bluetooth 진단"));
        status.Controls.Add(_diagnosis);
        status.Controls.Add(ActionButton("Bluetooth 다시 검사", async () =>
            _diagnosis.Text = await BleHidServer.DiagnoseAsync()));
        flow.Controls.Add(status);

        var logs = Section(365);
        logs.Controls.Add(SectionTitle("실행 로그"));
        logs.Controls.Add(_events);
        logs.Controls.Add(Row(
            ActionButton("진단 JSON 저장", SaveDiagnosticsAsync, primary: true),
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

    private static FlowLayoutPanel TabFlow() => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(16),
        BackColor = Bg
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

    private Button ActionButton(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(92, 36),
            Padding = new Padding(10, 3, 10, 3),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            BackColor = primary ? Brand : Color.White,
            ForeColor = primary ? Color.White : TextColor
        };

        button.FlatAppearance.BorderColor = primary ? Brand : Border;
        button.FlatAppearance.MouseDownBackColor = primary ? BrandPressed : ColorTranslator.FromHtml("#EEEFF1");
        button.FlatAppearance.MouseOverBackColor = primary ? ColorTranslator.FromHtml("#FF7A23") : ColorTranslator.FromHtml("#F3F4F5");
        button.Click += (_, _) => Dispatch(action);
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
        _input.Packet += packet =>
        {
            var outgoing = packet is PointerPacket pointer
                ? _pointerScaler.Apply(pointer)
                : packet;

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

        _input.Toggle += () => DispatchCritical(ToggleAsync);
        _input.Emergency += () => DispatchCritical(ReturnLocalAsync);
        _input.LockAndReturn += () => DispatchCritical(LockPhoneAndReturnAsync);
        _input.Fault += text => DispatchCritical(async () =>
        {
            ImmediateLocal();
            await ReleaseAsync();
            Log(text);
        });

        _ble.Status += text => Post(() => Log(text));
        _ble.Changed += () =>
        {
            if (_input.IsRemote && !_ble.Ready)
            {
                ImmediateLocal();
                Post(() => Log("Bluetooth 연결 상태가 바뀌어 노트북 입력으로 복귀했습니다."));
            }
        };

        _timer.Tick += (_, _) =>
        {
            if (_input.IsRemote && !_ble.Ready)
                ImmediateLocal();

            var emergency = HotkeyCatalog.Display(_settings.EmergencyHotkey);
            _state.Text = _input.IsRemote
                ? $"휴대폰 조작 중 · {emergency}로 즉시 복귀"
                : $"노트북 조작 중 · 광고:{_ble.Advertisement} · K:{_ble.KeyboardSubscribers} M:{_ble.MouseSubscribers} LOCK:{_ble.SystemSubscribers} C:{_ble.ConsumerSubscribers} · 승인:{(_ble.Ready ? "준비됨" : "대기")}";

            _tray.Text = _input.IsRemote
                ? $"Con.Phone · 휴대폰 입력 / {emergency} 복귀"
                : "Con.Phone · 노트북 입력";
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
            InitializeClipboardHistory();

            _diagnosis.Text = await BleHidServer.DiagnoseAsync();
            await _ble.StartAsync();

            Log("Bluetooth HID 광고를 시작했습니다.");
            Log("0.2.2는 한/영 LANG1·LANG2 및 Consumer Control Report가 추가되었습니다. C:0이거나 한/영이 동작하지 않으면 기존 PC 등록을 삭제하고 다시 페어링해 주세요.");
        });
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
            _clipboard.CaptureNow();
            RefreshClipboardList();
        }
        catch (Exception ex)
        {
            Log($"클립보드 기록 시작 실패: {ex.Message}");
        }
    }

    private void LoadSettingsToUi()
    {
        _mouseSpeed.Value = _settings.MouseSpeedPercent;
        _scrollSpeed.Value = _settings.ScrollSpeedPercent;
        _invertScroll.Checked = _settings.InvertScroll;
        _targetProfile.SelectedIndex = Math.Clamp((int)_settings.TargetProfile, 0, 2);
        _appleSwapCommandOption.Checked = _settings.AppleSwapCommandOption;

        foreach (var box in _specialKeyBoxes)
            box.SelectedIndex = 0;
        foreach (var mapping in _settings.SpecialKeys)
        {
            var index = mapping.SourceKey - (int)Keys.F1;
            if (index is < 0 or >= 12) continue;
            for (var item = 0; item < _specialKeyBoxes[index].Items.Count; item++)
            {
                if (_specialKeyBoxes[index].Items[item] is SpecialActionChoice choice &&
                    choice.Action == mapping.Action)
                {
                    _specialKeyBoxes[index].SelectedIndex = item;
                    break;
                }
            }
        }

        _toggleHotkeyBox.Text = HotkeyCatalog.Display(_settings.ToggleHotkey);
        _emergencyHotkeyBox.Text = HotkeyCatalog.Display(_settings.EmergencyHotkey);
        _lockHotkeyBox.Text = HotkeyCatalog.Display(_settings.LockAndReturnHotkey);

        _clipboardLimit.Value = _settings.ClipboardHistoryLimit;
        _captureClipboardText.Checked = _settings.CaptureClipboardText;
        _captureClipboardImages.Checked = _settings.CaptureClipboardImages;

        _mouseSpeedValue.Text = $"{_mouseSpeed.Value}%";
        _scrollSpeedValue.Text = $"{_scrollSpeed.Value}%";
    }

    private void ApplyInputSettings(bool save)
    {
        _settings.MouseSpeedPercent = _mouseSpeed.Value;
        _settings.ScrollSpeedPercent = _scrollSpeed.Value;
        _settings.InvertScroll = _invertScroll.Checked;
        _settings.TargetProfile = (TargetProfile)Math.Clamp(_targetProfile.SelectedIndex, 0, 2);
        _settings.AppleSwapCommandOption = _appleSwapCommandOption.Checked;
        _settings.SpecialKeys = _specialKeyBoxes
            .Select((box, index) => new SpecialKeyBinding
            {
                SourceKey = (int)Keys.F1 + index,
                Action = SelectedSpecialAction(box)
            })
            .ToList();

        _pointerScaler.Configure(
            _settings.MouseSpeedPercent,
            _settings.ScrollSpeedPercent,
            _settings.InvertScroll);

        _input.ConfigureHotkeys(
            _settings.ToggleHotkey,
            _settings.EmergencyHotkey,
            _settings.LockAndReturnHotkey);
        _input.ConfigureAdvancedInput(
            _settings.AppleSwapCommandOption,
            _settings.SpecialKeys);

        _mouseSpeedValue.Text = $"{_mouseSpeed.Value}%";
        _scrollSpeedValue.Text = $"{_scrollSpeed.Value}%";
        RefreshConnectionShortcutSummary();

        if (save)
        {
            SettingsStore.Save(_settings);
            Log($"입력 설정 저장 · 프로필 {_settings.TargetProfile} · 마우스 {_settings.MouseSpeedPercent}% · 스크롤 {_settings.ScrollSpeedPercent}%");
        }
    }

    private enum HotkeySlot
    {
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

    private void ValidateHotkeyCandidate(HotkeySlot slot, HotkeyBinding candidate)
    {
        if (!HotkeyCatalog.IsAllowedKey(candidate.Key))
            throw new InvalidOperationException("이 키는 단축키로 사용할 수 없습니다.");

        if (slot != HotkeySlot.Toggle && HotkeyCatalog.Same(candidate, _settings.ToggleHotkey))
            throw new InvalidOperationException("이미 PC ↔ 휴대폰 전환에 사용 중인 조합입니다.");

        if (slot != HotkeySlot.Emergency && HotkeyCatalog.Same(candidate, _settings.EmergencyHotkey))
            throw new InvalidOperationException("이미 비상 복귀에 사용 중인 조합입니다.");

        if (slot != HotkeySlot.LockAndReturn && HotkeyCatalog.Same(candidate, _settings.LockAndReturnHotkey))
            throw new InvalidOperationException("이미 휴대폰 잠금 + 복귀에 사용 중인 조합입니다.");
    }

    private void ResetHotkeys()
    {
        _settings.ToggleHotkey = SettingsStore.DefaultToggle();
        _settings.EmergencyHotkey = SettingsStore.DefaultEmergency();
        _settings.LockAndReturnHotkey = SettingsStore.DefaultLockAndReturn();

        _toggleHotkeyBox.Text = HotkeyCatalog.Display(_settings.ToggleHotkey);
        _emergencyHotkeyBox.Text = HotkeyCatalog.Display(_settings.EmergencyHotkey);
        _lockHotkeyBox.Text = HotkeyCatalog.Display(_settings.LockAndReturnHotkey);

        ApplyInputSettings(save: true);
        Log("단축키를 기본값으로 복원했습니다.");
    }

    private void ApplyClipboardSettings(bool save)
    {
        _settings.ClipboardHistoryLimit = (int)_clipboardLimit.Value;
        _settings.CaptureClipboardText = _captureClipboardText.Checked;
        _settings.CaptureClipboardImages = _captureClipboardImages.Checked;

        _clipboard?.Configure(
            _settings.ClipboardHistoryLimit,
            _settings.CaptureClipboardText,
            _settings.CaptureClipboardImages);

        if (save)
        {
            SettingsStore.Save(_settings);
            Log($"클립보드 기록 설정 저장 · 최대 {_settings.ClipboardHistoryLimit}개");
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
        if (_clipboardList.SelectedItem is not ClipboardEntry entry)
        {
            _clipboardDetail.Text = "저장된 항목이 없습니다.";
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
        await _actions.WaitAsync();

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
        if (_events.TextLength > 16000)
            _events.Clear();

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

            if (_ble.ConsumerReady)
            {
                await _ble.SendAsync(
                    new WireReport(false, new byte[Hid.ConsumerReportLength], Consumer: true),
                    () => epoch == Volatile.Read(ref _epoch),
                    timeout.Token);
            }
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

        _pointerScaler.Reset();
        _input.Activate();

        Log($"휴대폰 입력 시작 · {HotkeyCatalog.Display(_settings.EmergencyHotkey)}로 즉시 복귀");
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
            Log("Android LOCK System Control 키를 보내고 노트북 입력으로 복귀했습니다.");
        }
        catch (NotSupportedException ex)
        {
            Log(ex.Message);
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
        Log($"기기 승인 완료 · {HotkeyCatalog.Display(_settings.ToggleHotkey)}로 휴대폰 입력을 시작할 수 있습니다.");
    }

    private async Task RefreshPeersAsync(bool silent = false)
    {
        var selectedId = (_peers.SelectedItem as Peer)?.Id;
        var peers = await _ble.GetPeersAsync();

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

    private async Task PumpAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var envelope = await _outbox.TakePointerFrameAsync(
                    _lifetime.Token,
                    PointerFrameMilliseconds);

                foreach (var report in Hid.Encode(envelope.Packet))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(750);

                    await _ble.SendAsync(
                        report,
                        () =>
                            envelope.Epoch == Volatile.Read(ref _epoch) &&
                            _input.IsRemote,
                        timeout.Token);

                    Interlocked.Increment(ref _sent);
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

    private Task OpenUpdatePageAsync()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/DIA-Universe/Con.Phone/releases",
            UseShellExecute = true
        });
        Log("업데이트/릴리스 페이지를 기본 브라우저에서 열었습니다.");
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
            Version = "0.2.2",
            OS = Environment.OSVersion.Version.ToString(),
            Runtime = Environment.Version.ToString(),
            Bluetooth = await BleHidServer.DiagnoseAsync(),
            Advertisement = _ble.Advertisement,
            BleLastOperation = _ble.LastOperation,
            KeyboardSubscribers = _ble.KeyboardSubscribers,
            MouseSubscribers = _ble.MouseSubscribers,
            SystemSubscribers = _ble.SystemSubscribers,
            ConsumerSubscribers = _ble.ConsumerSubscribers,
            ApprovedAndSubscribed = _ble.Ready,
            TargetProfile = _settings.TargetProfile.ToString(),
            AppleSwapCommandOption = _settings.AppleSwapCommandOption,
            SpecialKeys = _settings.SpecialKeys
                .Where(x => x.Action != SpecialKeyAction.None)
                .Select(x => $"{HotkeyCatalog.NameOf(x.SourceKey)}={SpecialKeyCatalog.Display(x.Action)}")
                .ToArray(),
            SendOperations = Interlocked.Read(ref _sent),
            Pending = _outbox.Count,
            PointerFrameMilliseconds,
            LastError = _lastError,
            MouseSpeedPercent = _settings.MouseSpeedPercent,
            ScrollSpeedPercent = _settings.ScrollSpeedPercent,
            ToggleHotkey = HotkeyCatalog.Display(_settings.ToggleHotkey),
            EmergencyHotkey = HotkeyCatalog.Display(_settings.EmergencyHotkey),
            LockAndReturnHotkey = HotkeyCatalog.Display(_settings.LockAndReturnHotkey),
            ClipboardEntries = _clipboard?.Entries.Count ?? 0,
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
                await Task.Delay(1200);
                await _ble.StartAsync();
                Log("절전 복귀 · Bluetooth HID 광고를 다시 확인했습니다.");
            });
        }
    }

    private void SessionChanged(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            ImmediateLocal();
            Log("Windows 잠금 감지 · Bluetooth 승인/광고는 유지하고 입력만 노트북으로 복귀했습니다.");
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
        _peerTimer.Stop();
        _peerTimer.Dispose();

        _tray.Visible = false;
        _tray.Dispose();

        _closed = true;
        Close();
    }
}
