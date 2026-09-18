using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ConPhone.Windows;

/// <summary>
/// Focus this field and press a shortcut. ProcessCmdKey is used so Ctrl/Alt/Shift combinations
/// are captured before normal WinForms command processing can consume them.
/// </summary>
public sealed class HotkeyCaptureBox : TextBox
{
    private HotkeyBinding _binding = new();

    public event Action<HotkeyBinding>? BindingChanged;
    public event Action<bool>? CaptureStateChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HotkeyBinding Binding
    {
        get => _binding.Clone();
        set
        {
            _binding = value?.Clone() ?? new HotkeyBinding();
            Text = HotkeyCatalog.Display(_binding);
            SelectionStart = 0;
            SelectionLength = TextLength;
        }
    }

    public HotkeyCaptureBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        TabStop = true;
        Width = 230;
        Height = 34;
        BackColor = ConPhoneTheme.Surface;
        ForeColor = ConPhoneTheme.Text;
        BorderStyle = BorderStyle.FixedSingle;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        CaptureStateChanged?.Invoke(true);
        Text = "원하는 키 조합을 누르세요";
        SelectAll();
    }

    protected override void OnLeave(EventArgs e)
    {
        Text = HotkeyCatalog.Display(_binding);
        CaptureStateChanged?.Invoke(false);
        base.OnLeave(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused) return base.ProcessCmdKey(ref msg, keyData);

        var key = keyData & Keys.KeyCode;
        if (key == Keys.None || HotkeyCatalog.IsModifierKey((int)key))
        {
            Text = HotkeyCatalog.DisplayModifiers(ReadModifiers(keyData));
            return true;
        }

        if (!HotkeyCatalog.IsAllowedKey((int)key))
        {
            System.Media.SystemSounds.Beep.Play();
            return true;
        }

        var binding = new HotkeyBinding
        {
            Key = (int)key,
            Modifiers = ReadModifiers(keyData)
        };

        _binding = binding;
        Text = HotkeyCatalog.Display(binding);
        SelectionStart = 0;
        SelectionLength = TextLength;
        BindingChanged?.Invoke(binding.Clone());
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Modifier-only presses are not always routed through ProcessCmdKey.
        if (HotkeyCatalog.IsModifierKey((int)e.KeyCode))
        {
            Text = HotkeyCatalog.DisplayModifiers(ReadModifiers(e.KeyData));
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (HotkeyCatalog.IsModifierKey((int)e.KeyCode))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        base.OnKeyUp(e);
    }

    private static HotkeyModifiers ReadModifiers(Keys keyData)
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if ((keyData & Keys.Control) == Keys.Control || (GetAsyncKeyState(0x11) & 0x8000) != 0)
            modifiers |= HotkeyModifiers.Control;
        if ((keyData & Keys.Alt) == Keys.Alt || (GetAsyncKeyState(0x12) & 0x8000) != 0)
            modifiers |= HotkeyModifiers.Alt;
        if ((keyData & Keys.Shift) == Keys.Shift || (GetAsyncKeyState(0x10) & 0x8000) != 0)
            modifiers |= HotkeyModifiers.Shift;
        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0)
            modifiers |= HotkeyModifiers.Windows;
        return modifiers;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
