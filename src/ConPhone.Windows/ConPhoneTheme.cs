using System.ComponentModel;

namespace ConPhone.Windows;

// Stable WinForms interpretation of design.md.
// It uses the token palette, spacing and type hierarchy but avoids custom painting/layout tricks
// that can break at non-100% Windows display scaling.
public static class ConPhoneTheme
{
    public static readonly Color Basement = ColorTranslator.FromHtml("#F3F4F5");
    public static readonly Color Surface = ColorTranslator.FromHtml("#FFFFFF");
    public static readonly Color Fill = ColorTranslator.FromHtml("#F7F8F9");
    public static readonly Color FillPressed = ColorTranslator.FromHtml("#EEEFF1");
    public static readonly Color Stroke = ColorTranslator.FromHtml("#DCDEE3");
    public static readonly Color Text = ColorTranslator.FromHtml("#1A1C20");
    public static readonly Color TextSecondary = ColorTranslator.FromHtml("#555D6D");
    public static readonly Color TextTertiary = ColorTranslator.FromHtml("#868B94");
    public static readonly Color Brand = ColorTranslator.FromHtml("#FF6600");
    public static readonly Color BrandPressed = ColorTranslator.FromHtml("#E14D00");
    public static readonly Color BrandWeak = ColorTranslator.FromHtml("#FFF2EC");
    public static readonly Color Positive = ColorTranslator.FromHtml("#10AB7D");
    public static readonly Color PositiveWeak = ColorTranslator.FromHtml("#EDFAF6");
    public static readonly Color Informative = ColorTranslator.FromHtml("#217CF9");
    public static readonly Color InformativeWeak = ColorTranslator.FromHtml("#EFF6FF");
    public static readonly Color Critical = ColorTranslator.FromHtml("#FA342C");
    public static readonly Color CriticalWeak = ColorTranslator.FromHtml("#FDF0F0");

    public static Font Font(float size = 10f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style, GraphicsUnit.Point);

    public static Label Title(string text, float size = 20f) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Font(size, FontStyle.Bold),
        ForeColor = Text,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 8)
    };

    public static Label Body(string text, Color? color = null, int maxWidth = 760) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(maxWidth, 0),
        Font = Font(10f),
        ForeColor = color ?? TextSecondary,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 8)
    };

    public static Label Section(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Font(11f, FontStyle.Bold),
        ForeColor = Text,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 8, 0, 6)
    };
}

public enum ConPhoneButtonKind { Primary, Secondary, Ghost, Danger }

public sealed class ConPhoneButton : Button
{
    private ConPhoneButtonKind _kind;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ConPhoneButtonKind Kind
    {
        get => _kind;
        set { _kind = value; ApplyVisual(); }
    }

    public ConPhoneButton()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(88, 38);
        Padding = new Padding(12, 5, 12, 5);
        Font = ConPhoneTheme.Font(9.5f, FontStyle.Bold);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 1;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        ApplyVisual();
    }

    private void ApplyVisual()
    {
        switch (_kind)
        {
            case ConPhoneButtonKind.Primary:
                BackColor = ConPhoneTheme.Brand;
                ForeColor = Color.White;
                FlatAppearance.BorderColor = ConPhoneTheme.Brand;
                break;
            case ConPhoneButtonKind.Danger:
                BackColor = ConPhoneTheme.Critical;
                ForeColor = Color.White;
                FlatAppearance.BorderColor = ConPhoneTheme.Critical;
                break;
            case ConPhoneButtonKind.Ghost:
                BackColor = ConPhoneTheme.Surface;
                ForeColor = ConPhoneTheme.TextSecondary;
                FlatAppearance.BorderColor = ConPhoneTheme.Surface;
                break;
            default:
                BackColor = ConPhoneTheme.Surface;
                ForeColor = ConPhoneTheme.Text;
                FlatAppearance.BorderColor = ConPhoneTheme.Stroke;
                break;
        }
    }
}

public sealed class ConPhoneCard : FlowLayoutPanel
{
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 0; // retained for source compatibility; standard WinForms border is used.

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = ConPhoneTheme.Stroke;

    public ConPhoneCard()
    {
        BackColor = ConPhoneTheme.Surface;
        Padding = new Padding(20);
        Margin = new Padding(0, 0, 0, 16);
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BorderStyle = BorderStyle.FixedSingle;
        MinimumSize = new Size(420, 0);
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is not null) FitChild(e.Control);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        foreach (Control child in Controls) FitChild(child);
    }

    private void FitChild(Control child)
    {
        var width = Math.Max(320, ClientSize.Width - Padding.Horizontal - 8);
        if (child is Label label && label.AutoSize)
            label.MaximumSize = new Size(width, 0);
        if (Equals(child.Tag, "stretch"))
            child.Width = width;
    }
}

public sealed class ConPhoneNavButton : Button
{
    private bool _selected;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            BackColor = value ? ConPhoneTheme.BrandWeak : ConPhoneTheme.Surface;
            ForeColor = value ? ConPhoneTheme.BrandPressed : ConPhoneTheme.TextSecondary;
        }
    }

    public ConPhoneNavButton()
    {
        Height = 44;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(16, 0, 10, 0);
        Font = ConPhoneTheme.Font(10f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        BackColor = ConPhoneTheme.Surface;
        ForeColor = ConPhoneTheme.TextSecondary;
    }
}
