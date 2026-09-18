namespace ConPhone.Windows;
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\DIAUniverse.ConPhone", out bool first);
        if (!first) { MessageBox.Show("Con.Phone이 이미 실행 중입니다."); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
