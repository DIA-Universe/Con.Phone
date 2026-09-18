using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ConPhone.Windows;

public sealed record UpdateCandidate(
    string Path,
    Version Version,
    long SizeBytes,
    string Sha256,
    bool ChecksumVerified,
    bool SignatureVerified);

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

        var sha256 = ComputeSha256(path);

        var checksumPath = path + ".sha256";
        if (!File.Exists(checksumPath))
            throw new InvalidOperationException(
                "업데이트 SHA-256 검증 파일이 없습니다. ConPhone.exe.sha256을 같은 폴더에 두세요.");

        var expected = File.ReadAllText(checksumPath)
            .Trim()
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (expected is null ||
            expected.Length != 64 ||
            !expected.All(Uri.IsHexDigit))
            throw new InvalidOperationException(
                "업데이트 SHA-256 파일 형식이 올바르지 않습니다.");

        if (!string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "업데이트 SHA-256이 일치하지 않습니다. 파일을 적용하지 않습니다.");

        var checksumVerified = true;

        var currentPath = Environment.ProcessPath;
        var currentSigner = string.IsNullOrWhiteSpace(currentPath)
            ? null
            : TryGetSignerThumbprint(currentPath);
        var candidateSigner = TryGetSignerThumbprint(path);

        var signatureVerified =
            currentSigner is not null &&
            candidateSigner is not null &&
            string.Equals(
                currentSigner,
                candidateSigner,
                StringComparison.OrdinalIgnoreCase);

        if (currentSigner is not null && !signatureVerified)
            throw new InvalidOperationException(
                "현재 Con.Phone과 업데이트 파일의 Authenticode 서명자가 다릅니다. 업데이트를 중단합니다.");

        return new UpdateCandidate(
            path,
            version,
            file.Length,
            sha256,
            checksumVerified,
            signatureVerified);
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

        var stagedHash = ComputeSha256(staged);
        if (!string.Equals(
                stagedHash,
                candidate.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(staged); } catch { }
            throw new InvalidOperationException(
                "업데이트 staging 파일의 SHA-256 검증에 실패했습니다.");
        }

        return staged;
    }

    public static void LaunchInstaller(
        string stagedPath,
        string expectedSha256)
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
        start.ArgumentList.Add(expectedSha256);
        Process.Start(start)
            ?? throw new InvalidOperationException("업데이트 프로세스를 시작하지 못했습니다.");
    }

    public static void ApplyFromStagedProcess(
        string targetPath,
        int oldProcessId,
        string expectedSha256)
    {
        if (expectedSha256.Length != 64 ||
            !expectedSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException(
                "업데이트 SHA-256 인수가 올바르지 않습니다.");

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

        var sourceHash = ComputeSha256(source);
        if (!string.Equals(
                sourceHash,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "실행 중인 staging 파일의 SHA-256이 변경되었습니다. 업데이트를 중단합니다.");

        var replacement = target + $".new-{Guid.NewGuid():N}";
        try
        {
            File.Copy(source, replacement, overwrite: false);

            var replacementHash = ComputeSha256(replacement);
            if (!string.Equals(
                    replacementHash,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "교체용 업데이트 파일의 SHA-256 검증에 실패했습니다.");

            File.Move(replacement, target, overwrite: true);

            var targetHash = ComputeSha256(target);
            if (!string.Equals(
                    targetHash,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "업데이트 적용 후 SHA-256 검증에 실패했습니다.");
        }
        finally
        {
            try
            {
                if (File.Exists(replacement))
                    File.Delete(replacement);
            }
            catch
            {
            }
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    private static string? TryGetSignerThumbprint(string path)
    {
        try
        {
#pragma warning disable SYSLIB0026
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0026
            return string.IsNullOrWhiteSpace(certificate.Thumbprint)
                ? null
                : certificate.Thumbprint;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.SequentialScan);

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(stream));
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
