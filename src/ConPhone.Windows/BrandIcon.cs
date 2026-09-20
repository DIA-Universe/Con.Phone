namespace ConPhone.Windows;

internal static class BrandIcon
{
    public static Icon Load()
    {
        using var stream = typeof(BrandIcon).Assembly.GetManifestResourceStream("LINKO.Icon")!;
        using var icon = new Icon(stream, 64, 64);
        return (Icon)icon.Clone();
    }
}
