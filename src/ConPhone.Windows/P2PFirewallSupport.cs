using System.Diagnostics;

namespace ConPhone.Windows;

public static class P2PFirewallSupport
{
    public const string RuleName = "Con.Phone P2P Clipboard";
    public const int Port = P2PClipboardServer.DataPort;

    public static async Task ConfigureAsync(CancellationToken token)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("현재 Con.Phone 실행 파일 경로를 확인할 수 없습니다.");

        var escapedExe = exe.Replace("'", "''", StringComparison.Ordinal);
        var escapedRule = RuleName.Replace("'", "''", StringComparison.Ordinal);

        var command =
            $"$ErrorActionPreference='Stop';" +
            $"$name='{escapedRule}';" +
            $"$exe='{escapedExe}';" +
            $"& netsh advfirewall firewall delete rule name=\"$name\" | Out-Null;" +
            $"& netsh advfirewall firewall add rule " +
            $"name=\"$name\" dir=in action=allow protocol=TCP localport={Port} " +
            $"program=\"$exe\" enable=yes profile=private,public | Out-Null;" +
            $"if ($LASTEXITCODE -ne 0) {{ exit $LASTEXITCODE }}";

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows 방화벽 설정 프로세스를 시작하지 못했습니다.");

        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Windows 방화벽 P2P 규칙 설정 실패 (exit {process.ExitCode}).");
    }
}
