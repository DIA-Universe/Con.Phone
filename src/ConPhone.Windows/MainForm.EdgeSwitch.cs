using ConPhone.Core;

namespace ConPhone.Windows;

public sealed partial class MainForm
{
    private readonly CheckBox _edgeEnabled = new() { Text = "가장자리 자동 전환 사용", AutoSize = true };
    private readonly ComboBox _edgeSide = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _edgeTravel = new() { Minimum = 0, Maximum = 32767, Width = 100 };
    private readonly Label _edgeStatus = new() { AutoSize = true, MaximumSize = new Size(790, 0), ForeColor = Secondary };
    private EdgeSwitchProfile _edgeProfile = new();
    private volatile bool _inputConnectionReady;
    private volatile bool _desktopSuspended, _desktopLocked;
    private bool _desktopInteractive => !_desktopSuspended && !_desktopLocked;
    private PointerSession? _pointerSession;
    private ScreenBounds[] _desktopScreens = [];

    private sealed record PointerSession(long Epoch, string DeviceId, EdgeEntry? Entry,
        EstimatedPhonePointer? Estimate, PointerCalibration? Calibration, PhoneSide Side);

    private Control BuildEdgeSwitchSection()
    {
        var section = AutoSection();
        section.Controls.Add(SectionTitle("가장자리 자동 전환 · 실험 기능"));
        section.Controls.Add(Help("휴대폰 앱 없이 PC 화면 끝에서 넘어가요. 돌아올 때는 이동량으로 위치를 추정하므로 실제 화면 끝과 어긋날 수 있어요."));
        section.Controls.Add(Row(_edgeEnabled));
        section.Controls.Add(LabeledRow("휴대폰 위치", _edgeSide));
        section.Controls.Add(LabeledRow("보정한 이동 범위", Row(_edgeTravel, Help("값이 클수록 휴대폰의 반대쪽 끝까지 더 멀리 이동해요."))));
        section.Controls.Add(Row(
            ActionButton("이동 범위 보정", StartPointerCalibrationAsync, primary: true, critical: true),
            ActionButton("자동 전환 설정 저장", SaveEdgeSwitchAsync, critical: true)));
        section.Controls.Add(_edgeStatus);
        section.Controls.Add(Help("처음에는 꺼져 있어요. 보정을 마치면 켜져요. 휴대폰 감도나 화면 방향을 바꾸면 다시 보정해 주세요. 키나 마우스 버튼을 누른 동안에는 자동으로 전환하지 않아요."));
        return section;
    }

    private void LoadEdgeSwitchProfile()
    {
        if (_edgeSide.Items.Count == 0) _edgeSide.Items.AddRange(["오른쪽", "왼쪽"]);
        _edgeProfile = _ble.ApprovedId is { } id
            ? DeviceProfileStore.Get(id, _activeHangulMode, _activeModifierProfile).EdgeSwitch.Normalized()
            : new();
        _edgeEnabled.Checked = _edgeProfile.Enabled;
        _edgeSide.SelectedIndex = _edgeProfile.Side == PhoneSide.Right ? 0 : 1;
        _edgeTravel.Value = _edgeProfile.Travel;
        _edgeStatus.Text = _edgeProfile.Calibrated
            ? "보정값을 저장했어요. 실제 복귀 위치가 어긋나면 다시 보정해 주세요."
            : "휴대폰을 연결하고 활성 기기로 선택한 뒤 이동 범위를 보정해 주세요.";
        ConfigureAutomaticSwitch();
    }

    private void ConfigureAutomaticSwitch()
    {
        _desktopScreens = DesktopGeometry.Capture();
        _input.ConfigureEdgeSwitch(_desktopScreens, _edgeProfile);
        _input.SetAutomaticReady(_inputConnectionReady && _desktopInteractive && !_servicesPaused && !_closing);
    }

    private async Task SaveEdgeSwitchAsync()
    {
        var id = _ble.ApprovedId ?? throw new InvalidOperationException("먼저 사용할 휴대폰을 선택해 주세요.");
        var profile = new EdgeSwitchProfile
        {
            Enabled = _edgeEnabled.Checked,
            Side = _edgeSide.SelectedIndex == 1 ? PhoneSide.Left : PhoneSide.Right,
            Travel = (int)_edgeTravel.Value
        };
        if (profile.Enabled && !profile.Calibrated)
            throw new InvalidOperationException("‘이동 범위 보정’을 먼저 마쳐 주세요.");
        await ReturnLocalAsync();
        DeviceProfileStore.SaveEdgeSwitch(id, profile, _settings.HangulKeyMode, _settings.ModifierProfile);
        LoadEdgeSwitchProfile();
        Log(profile.Enabled ? "가장자리 자동 전환을 켰어요. 복귀 위치는 이동량으로 추정해요." : "가장자리 자동 전환을 껐어요.");
    }

    private async Task StartPointerCalibrationAsync()
    {
        await ReturnLocalAsync();
        var side = _edgeSide.SelectedIndex == 1 ? PhoneSide.Left : PhoneSide.Right;
        await EnterRemoteAsync(calibrationSide: side);
        if (!_input.IsRemote) return;
        ShowCalibrationStep(0, side);
    }

    private void ShowCalibrationStep(int stage, PhoneSide side)
    {
        var mark = HotkeyCatalog.Display(_settings.ToggleHotkey);
        var cancel = HotkeyCatalog.Display(_settings.EmergencyHotkey);
        var near = side == PhoneSide.Right ? "왼쪽" : "오른쪽";
        var far = side == PhoneSide.Right ? "오른쪽" : "왼쪽";
        _edgeStatus.Text = stage switch
        {
            0 => $"보정 1/3 · 휴대폰 커서를 {near} 끝에 놓고 {mark}를 눌러 주세요. {cancel}로 취소해요.",
            1 => $"보정 2/3 · 평소 속도로 {far} 끝까지 이동하고 {mark}를 눌러 주세요.",
            _ => $"보정 3/3 · 비슷한 속도로 {near} 끝으로 돌아와 {mark}를 눌러 주세요."
        };
        _feedback.Text = _edgeStatus.Text;
        if (!_previewMode) _tray.ShowBalloonTip(3500, "LINKO 이동 범위 보정", _edgeStatus.Text, ToolTipIcon.Info);
    }

    private async Task EnterFromEdgeAsync(long generation, EdgeEntry entry)
    {
        if (generation != _input.Generation || _input.IsRemote || !_edgeProfile.Enabled || _input.IsCapturingHotkey) return;
        await EnterRemoteAsync(entry: entry, expectedGeneration: generation);
    }

    private async Task EnterRemoteAsync(EdgeEntry? entry = null, long? expectedGeneration = null, PhoneSide? calibrationSide = null)
    {
        if (expectedGeneration.HasValue && expectedGeneration != _input.Generation) return;
        _inputConnectionReady = _ble.Ready;
        if (!_inputConnectionReady) throw new InvalidOperationException("휴대폰 키보드·마우스 연결이 준비되면 다시 전환해 주세요.");
        var target = _ble.ApprovedId!;
        ImmediateLocal();
        var epoch = Volatile.Read(ref _epoch);
        var generation = _input.Generation;
        bool Current() => !_closing && _desktopInteractive && epoch == Volatile.Read(ref _epoch) &&
            generation == _input.Generation && _inputConnectionReady && target == _ble.ApprovedId;
        if (!await ReleaseAsync())
        {
            Log("휴대폰의 입력 해제를 확인하지 못해 PC 입력을 유지해요. 연결이 준비되면 다시 전환해 주세요.");
            return;
        }
        if (!Current()) return;

        if (entry is not null)
        {
            // Keep PC input local during setup. This button-free report anchors
            // the host pointer horizontally; HID format/pairing remain unchanged.
            var atEdge = await _input.OnInputThreadAsync(() => Current() && !_input.HasHeldInput &&
                DesktopGeometry.Cursor(out var p) && entry.Screen.Contains(p.X, p.Y) &&
                (entry.Side == PhoneSide.Right ? p.X == entry.Screen.Right - 1 : p.X == entry.Screen.Left));
            if (!atEdge) return;
            var anchor = Hid.Encode(new PointerPacket(0, entry.Side == PhoneSide.Right ? -32767 : 32767)).Single();
            if (!await _ble.SendAsync(anchor, Current, _lifetime.Token) || !Current()) return;
        }

        var session = new PointerSession(epoch, target, entry,
            entry is null ? null : new EstimatedPhonePointer(_edgeProfile.Travel, entry.Side),
            calibrationSide.HasValue ? new PointerCalibration(calibrationSide.Value) : null,
            calibrationSide ?? entry?.Side ?? _edgeProfile.Side);
        Volatile.Write(ref _pointerSession, session);
        _input.CalibrationMode = calibrationSide.HasValue;
        _pointerScaler.Reset();
        var activated = entry is not null
            ? await _input.TryActivateAutomaticAsync(() => Current() && DesktopGeometry.Cursor(out var p) &&
                entry.Screen.Contains(p.X, p.Y) && (entry.Side == PhoneSide.Right ? p.X == entry.Screen.Right - 1 : p.X == entry.Screen.Left))
            : await ActivateInputWhenReleasedAsync(epoch);
        if (!activated)
        {
            Interlocked.CompareExchange(ref _pointerSession, null, session);
            if (epoch == Volatile.Read(ref _epoch)) _input.CalibrationMode = false;
            return;
        }
        SessionDiagnostics.Write($"input=remote source={(calibrationSide.HasValue ? "calibration" : entry is null ? "manual" : "edge")} epoch={epoch}");
        Log($"휴대폰 조작 중 · {HotkeyCatalog.Display(_settings.EmergencyHotkey)}로 PC에 바로 돌아와요.");
        _ = SyncLatestClipboardToPhoneSafeAsync(epoch, target);
    }

    private void ProcessCalibrationMark(long epoch, long generation)
    {
        var session = Volatile.Read(ref _pointerSession);
        if (session?.Epoch != epoch || session.Calibration is null) return;
        var travel = session.Calibration.Mark();
        if (travel is null)
        {
            Post(() => { if (ReferenceEquals(Volatile.Read(ref _pointerSession), session)) ShowCalibrationStep(session.Calibration.Stage, session.Side); });
            return;
        }
        if (!TryImmediateLocal(generation)) return;
        _ = ReleaseAsync();
        Post(() =>
        {
            DeviceProfileStore.SaveEdgeSwitch(session.DeviceId, new() { Enabled = true, Side = session.Side, Travel = travel.Value },
                _settings.HangulKeyMode, _settings.ModifierProfile);
            if (_ble.ApprovedId == session.DeviceId) LoadEdgeSwitchProfile();
            Log("이동 범위 보정을 마치고 자동 전환을 켰어요. PC 화면 끝으로 커서를 이동해 보세요.");
        });
    }

    private void ObserveDeliveredPointer(long epoch, long generation, byte[] report)
    {
        var session = Volatile.Read(ref _pointerSession);
        if (session?.Epoch != epoch || !_input.IsRemote) return;
        var x = (short)(report[1] | report[2] << 8);
        session.Calibration?.Advance(x);
        if (session.Estimate?.Advance(x, report[0] != 0 || _input.HasHeldInput) != true) return;
        if (!TryImmediateLocal(generation)) return;
        _ = ReleaseAsync();
        Post(() => Log("추정 경계를 넘어 PC로 돌아왔어요."));
    }

    private void ObserveInputConnection()
    {
        var generation = _input.Generation;
        var target = _ble.ApprovedId;
        var ready = _ble.Ready;
        if (target != _ble.ApprovedId) return;
        _inputConnectionReady = ready;
        _input.SetAutomaticReady(ready && _desktopInteractive && !_servicesPaused && !_closing);
        if (ready && !_input.IsRemote) RetryPendingInputRelease();
        if (ready || !_input.IsRemote) return;
        if (!TryImmediateLocal(generation)) return;
        SessionDiagnostics.Write("input-fallback reason=hid-not-ready");
        Post(() => Log("휴대폰 입력 연결을 사용할 수 없어 PC로 바로 돌아왔어요. Bluetooth 복구는 별도로 진행해요."));
    }

    private void DisplayLayoutChanged(object? sender, EventArgs e)
    {
        ImmediateLocal();
        Post(ConfigureAutomaticSwitch);
    }
}
