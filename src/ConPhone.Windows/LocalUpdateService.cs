using System.Diagnostics;
using System.Reflection;

namespace ConPhone.Windows;

public sealed record UpdateCandidate(
    string Path,
    Version Version,
    long SizeBytes);

public static class LocalUpdateService
{
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static UpdateCandidate Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ConPhone.exe 업데이트 파일을 선택해 주세요.");

        var info = FileVersionInfo.GetVersionInfo(path);
        var versionText = info.FileVersion ?? info.ProductVersion;
        if (!Version.TryParse(NormalizeVersion(versionText), out var version))
            throw new InvalidOperationException("업데이트 파일의 버전 정보를 읽을 수 없습니다.");

        var file = new FileInfo(path);
        if (file.Length < 1024 * 1024)
            throw new InvalidOperationException("업데이트 실행 파일 크기가 비정상적으로 작습니다.");

        return new UpdateCandidate(path, version, file.Length);
    }

    public static string Stage(UpdateCandidate candidate)
    {
        var current = Environment.ProcessPath
            ?? throw new InvalidOperationException("현재 Con.Phone 실행 파일 경로를 확인할 수 없습니다.");

        if (Path.GetFullPath(candidate.Path)
            .Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("현재 실행 중인 파일과 다른 업데이트 파일을 선택해 주세요.");

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConPhone",
            "Updates");
        Directory.CreateDirectory(directory);

        foreach (var stale in Directory.EnumerateFiles(directory, "ConPhone-update-*.exe"))
        {
            try
            {
                if (File.GetCreationTimeUtc(stale) < DateTime.UtcNow.AddDays(-7))
                    File.Delete(stale);
            }
            catch
            {
            }
        }

        var staged = Path.Combine(
            directory,
            $"ConPhone-update-{Guid.NewGuid():N}.exe");
        File.Copy(candidate.Path, staged, overwrite: false);
        return staged;
    }

    public static void LaunchInstaller(string stagedPath)
    {
        var target = Environment.ProcessPath
            ?? throw new InvalidOperationException("현재 Con.Phone 실행 파일 경로를 확인할 수 없습니다.");

        var start = new ProcessStartInfo
        {
            FileName = stagedPath,
            UseShellExecute = true
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(target);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(start)
            ?? throw new InvalidOperationException("업데이트 프로세스를 시작하지 못했습니다.");
    }

    public static void ApplyFromStagedProcess(
        string targetPath,
        int oldProcessId)
    {
        var source = Environment.ProcessPath
            ?? throw new InvalidOperationException("업데이트 실행 파일 경로를 확인할 수 없습니다.");

        if (oldProcessId > 0)
        {
            try
            {
                using var old = Process.GetProcessById(oldProcessId);
                old.WaitForExit(15000);
            }
            catch (ArgumentException)
            {
            }
        }

        var target = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        Exception? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                last = null;
                break;
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
                break;
            }
        }

        if (last is not null)
            throw new InvalidOperationException(
                "기존 Con.Phone 실행 파일을 교체하지 못했습니다. 쓰기 권한을 확인해 주세요.",
                last);

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var core = value.Split(
            ['+', '-', ' '],
            StringSplitOptions.RemoveEmptyEntries)[0];
        var parts = core.Split('.');
        if (parts.Length >= 4) return string.Join('.', parts.Take(4));
        return core;
    }
}
