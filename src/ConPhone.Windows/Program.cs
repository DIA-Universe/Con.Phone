namespace ConPhone.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length >= 2 && args[0].Equals("--snapshot", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var form = new MainForm
            {
                Size = new Size(980, 800)
            };

            form.CreateControl();
            form.PerformLayout();

            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        using var mutex = new Mutex(true, @"Local\DIAUniverse.ConPhone", out bool first);
        if (!first)
        {
            MessageBox.Show("Con.Phone이 이미 실행 중입니다.");
            return;
        }

        Application.Run(new MainForm());
    }
}
