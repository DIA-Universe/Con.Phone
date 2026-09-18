using System.Drawing.Drawing2D;

namespace ConPhone.Windows;

// Product-facing implementation of the uploaded design.md. Names intentionally stay product-neutral.
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

    public static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Label Title(string text, float size = 20f) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Font(size, FontStyle.Bold),
        ForeColor = Text,
        BackColor = Color.Transparent
    };

    public static Label Body(string text, Color? color = null, int maxWidth = 760) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(maxWidth, 0),
        Font = Font(10f),
        ForeColor = color ?? TextSecondary,
        BackColor = Color.Transparent
    };

    public static Label Section(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Font(11f, FontStyle.Bold),
        ForeColor = Text,
        BackColor = Color.Transparent
    };
}

public enum ConPhoneButtonKind { Primary, Secondary, Ghost, Danger }

public sealed class ConPhoneButton : Button
{
    private ConPhoneButtonKind _kind;
    private bool _hover;
    private bool _pressed;

    public ConPhoneButtonKind Kind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    public ConPhoneButton()
    {
        AutoSize = true;
        MinimumSize = new Size(88, 38);
        Padding = new Padding(13, 5, 13, 5);
        Font = ConPhoneTheme.Font(9.5f, FontStyle.Bold);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = ConPhoneTheme.Rounded(rect, 10);

        Color bg, fg, stroke;
        switch (Kind)
        {
            case ConPhoneButtonKind.Primary:
                bg = _pressed ? ConPhoneTheme.BrandPressed : _hover ? Color.FromArgb(255, 117, 31) : ConPhoneTheme.Brand;
                fg = Color.White; stroke = bg; break;
            case ConPhoneButtonKind.Danger:
                bg = _pressed ? ColorTranslator.FromHtml("#CA1D13") : _hover ? ColorTranslator.FromHtml("#FE6A66") : ConPhoneTheme.Critical;
                fg = Color.White; stroke = bg; break;
            case ConPhoneButtonKind.Ghost:
                bg = _pressed ? ConPhoneTheme.FillPressed : _hover ? ConPhoneTheme.Fill : Color.Transparent;
                fg = ConPhoneTheme.TextSecondary; stroke = Color.Transparent; break;
            default:
                bg = _pressed ? ConPhoneTheme.FillPressed : _hover ? ConPhoneTheme.Fill : ConPhoneTheme.Surface;
                fg = ConPhoneTheme.Text; stroke = ConPhoneTheme.Stroke; break;
        }

        using var bgBrush = new SolidBrush(bg);
        e.Graphics.FillPath(bgBrush, path);
        if (stroke.A > 0)
        {
            using var pen = new Pen(stroke, 1);
            e.Graphics.DrawPath(pen, path);
        }

        TextRenderer.DrawText(e.Graphics, Text, Font, rect, Enabled ? fg : ConPhoneTheme.TextTertiary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

public sealed class ConPhoneCard : Panel
{
    public int Radius { get; set; } = 16;
    public Color BorderColor { get; set; } = ConPhoneTheme.Stroke;

    public ConPhoneCard()
    {
        BackColor = ConPhoneTheme.Surface;
        Padding = new Padding(20);
        Margin = new Padding(0, 0, 0, 16);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = ConPhoneTheme.Rounded(rect, Radius);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        using var pen = new Pen(BorderColor, 1);
        e.Graphics.DrawPath(pen, path);
    }
}

public sealed class ConPhoneNavButton : Button
{
    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public ConPhoneNavButton()
    {
        Height = 44;
        Dock = DockStyle.Top;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(16, 0, 10, 0);
        Font = ConPhoneTheme.Font(10f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(4, 3, Width - 8, Height - 6);
        using var path = ConPhoneTheme.Rounded(rect, 10);
        using var brush = new SolidBrush(Selected ? ConPhoneTheme.BrandWeak : BackColor);
        e.Graphics.FillPath(brush, path);
        var fg = Selected ? ConPhoneTheme.BrandPressed : ConPhoneTheme.TextSecondary;
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(18, 0, Width - 24, Height), fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
