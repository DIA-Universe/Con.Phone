using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ConPhone.Windows;

/// <summary>
/// Focus the box and press the desired key combination. The control records the combination
/// instead of requiring the user to pick modifiers and a key from separate dropdowns.
/// </summary>
public sealed class HotkeyCaptureBox : TextBox
{
    private HotkeyBinding _binding = new();

    public event Action<HotkeyBinding>? BindingChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HotkeyBinding Binding
    {
        get => _binding.Clone();
        set
        {
            _binding = value?.Clone() ?? new HotkeyBinding();
            Text = HotkeyCatalog.Display(_binding);
        }
    }

    public HotkeyCaptureBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        TabStop = true;
        Width = 220;
        Height = 34;
        BackColor = ConPhoneTheme.Surface;
        ForeColor = ConPhoneTheme.Text;
        BorderStyle = BorderStyle.FixedSingle;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        SelectAll();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;

        if (HotkeyCatalog.IsModifierKey((int)e.KeyCode))
        {
            Text = HotkeyCatalog.DisplayModifiers(ReadModifiers());
            return;
        }

        if (!HotkeyCatalog.IsAllowedKey((int)e.KeyCode))
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var next = new HotkeyBinding
        {
            Key = (int)e.KeyCode,
            Modifiers = ReadModifiers()
        };
        _binding = next;
        Text = HotkeyCatalog.Display(next);
        BindingChanged?.Invoke(next.Clone());
        SelectAll();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        if (HotkeyCatalog.IsModifierKey((int)e.KeyCode))
            Text = HotkeyCatalog.Display(_binding);
    }

    private static HotkeyModifiers ReadModifiers()
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= HotkeyModifiers.Control;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= HotkeyModifiers.Alt;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= HotkeyModifiers.Shift;
        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0)
            modifiers |= HotkeyModifiers.Windows;
        return modifiers;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
