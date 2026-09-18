namespace ConPhone.Windows;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(args.Length>=2 && args[0]=="--self-test") return WindowsSmoke.Run(args[1],args.Contains("--quick"));
        using var mutex=new Mutex(true,@"Local\DIAUniverse.ConPhone",out bool first);
        if(!first) { MessageBox.Show("Con.Phone이 이미 실행 중입니다. 트레이에서 기존 앱을 완전히 종료하세요."); return 1; }
        MainForm? form=null;
        Application.ThreadException+=(_,e)=> { if(form is not null && !form.IsDisposed) form.HandleError(e.Exception); };
        try { form=new MainForm(); Application.Run(form); return 0; }
        catch(Exception ex) { MessageBox.Show($"Con.Phone 시작 실패: {ex.GetType().Name} 0x{ex.HResult:X8}\n{ex.Message}"); return 1; }
        finally { form?.Dispose(); }
    }
}
