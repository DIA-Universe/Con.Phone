using System.ComponentModel;
using ConPhone.Core;
namespace ConPhone.Windows;

// Recording is routed by the SAME low-level state machine as shortcuts, and takes priority over them.
public sealed class HotkeyCaptureBox : Button
{
    private HotkeyBinding _binding = new();
    private HotkeyBinding? _before;
    private InputCapture? _input;
    public event Action<bool>? RecordingChanged;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HotkeyBinding Binding { get => _binding.Clone(); set { _binding=value.Clone(); Render(); } }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Recording { get; private set; }
    public HotkeyCaptureBox()
    {
        AutoSize=false; Height=40; MinimumSize=new Size(0,40); FlatStyle=FlatStyle.Flat;
        FlatAppearance.BorderColor=Color.FromArgb(220,222,227);
        BackColor=Color.White; ForeColor=Color.FromArgb(26,28,32); TabStop=true;
        AccessibleDescription="클릭 또는 Enter를 누른 뒤 키 조합을 입력하고 다른 곳을 클릭하세요. Esc로 취소합니다.";
        Click += (_,_) => StartRecording();
    }
    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        // A width minimum can exceed a percentage column on a small desktop at high DPI.
        // Keep the touch/keyboard height; let DockStyle.Fill determine available width.
        if (Parent is TableLayoutPanel) MinimumSize=new Size(0,MinimumSize.Height);
    }
    public void Attach(InputCapture input) => _input=input;
    internal void StartRecording()
    {
        if (_input is null) return;
        if (!Recording) _before=_binding.Clone();
        Recording=true; Text="키 조합을 누르세요 · Esc 취소"; BackColor=Color.FromArgb(255,242,236);
        RecordingChanged?.Invoke(true);
        _input.BeginRecording(value =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => Receive(value)); } catch (InvalidOperationException) { }
        });
    }
    private void Receive(KeyChord? chord)
    {
        if (!Recording) return;
        if (chord is null) { if (_before is not null) _binding=_before; FinishRecording(); return; }
        try { KeyboardRouter.Validate(chord.Value); _binding=HotkeyBinding.From(chord.Value); Text=HotkeyCatalog.Display(_binding)+"  ✓"; }
        catch (ArgumentException) { Text="예약 조합입니다 · 다른 키를 누르세요"; }
    }
    public void FinishRecording()
    {
        if (!Recording) return;
        Recording=false; _input?.EndRecording(); BackColor=Color.White; Render(); RecordingChanged?.Invoke(false);
    }
    protected override void OnLeave(EventArgs e) { FinishRecording(); base.OnLeave(e); }
    private void Render() { if (!Recording) Text=HotkeyCatalog.Display(_binding); }
}
