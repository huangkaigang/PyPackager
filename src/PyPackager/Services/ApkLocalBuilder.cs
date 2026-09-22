using System.IO;
using System.Text;

namespace PyPackager.Services;

/// <summary>本地 WSL 编译结果。</summary>
public sealed record ApkLocalBuildResult(bool Success, string? ApkPath, string? Error);

/// <summary>
/// 在本地 WSL2（Ubuntu）内调用 buildozer 编译 APK。要求先用 <see cref="ApkDoctorService"/>
/// 确认环境为 Ready（或用 <see cref="ApkSetupService"/> 一键装好）。
///
/// buildozer / python-for-android 只能在 Linux 下运行，因此这里把 Windows 上的项目目录
/// 通过 wslpath 映射进 WSL，再在发行版内执行 <c>buildozer android debug</c>，产物在项目 bin\*.apk。
/// </summary>
public sealed class ApkLocalBuilder
{
    public async Task<ApkLocalBuildResult> BuildAsync(
        string? distro, string projectDir, Action<string> log, CancellationToken ct = default)
    {
        if (!Directory.Exists(projectDir))
        {
            return new ApkLocalBuildResult(false, null, $"项目目录不存在：{projectDir}");
        }

        var specPath = Path.Combine(projectDir, "buildozer.spec");
        if (!File.Exists(specPath))
        {
            return new ApkLocalBuildResult(false, null,
                "项目目录下没有 buildozer.spec。请先点“生成 buildozer.spec 模板”。");
        }

        // 1) 把 Windows 路径转成 WSL 路径
        var wslPath = (await CaptureAsync(distro, false, "wslpath -u " + ShellQuote(projectDir), ct))
            .Output.Trim();
        if (string.IsNullOrWhiteSpace(wslPath))
        {
            return new ApkLocalBuildResult(false, null, "无法把项目路径映射进 WSL（wslpath 失败）。");
        }
        log($"项目 WSL 路径：{wslPath}");

        // 2) 执行 buildozer（首次会联网下载 Android SDK/NDK，耗时较长）
        log("开始执行 buildozer android debug（首次构建会下载 Android SDK/NDK，请耐心等待）……");
        var script =
            "set -e\n" +
            "cd " + ShellQuote(wslPath) + "\n" +
            "export ANDROID_HOME=\"${ANDROID_HOME:-$HOME/.buildozer/android/platform/android-sdk}\"\n" +
            "buildozer android debug\n" +
            "echo \"__BUILD_OK__\"\n";

        var buildExit = await RunBashAsync(distro, true, script, line =>
        {
            if (!string.IsNullOrWhiteSpace(line)) log(line.TrimEnd());
        }, ct).ConfigureAwait(false);

        if (buildExit != 0)
        {
            return new ApkLocalBuildResult(false, null, $"buildozer 编译失败（退出码 {buildExit}）。请查看上方日志。");
        }

        // 3) 定位产物 APK（取 bin 下最新的 .apk），并转回 Windows 路径
        var (findExit, findOut) = await CaptureAsync(distro, false,
            "cd " + ShellQuote(wslPath) + " && ls -t bin/*.apk 2>/dev/null | head -1", ct).ConfigureAwait(false);
        var apkWsl = findOut.Trim();
        if (findExit != 0 || string.IsNullOrWhiteSpace(apkWsl))
        {
            return new ApkLocalBuildResult(false, null, "编译看似成功，但在项目 bin/ 下未找到 .apk 产物。");
        }

        var apkWin = (await CaptureAsync(distro, false, "wslpath -w " + ShellQuote(apkWsl), ct)).Output.Trim();
        if (string.IsNullOrWhiteSpace(apkWin)) apkWin = apkWsl;

        log($"编译完成，APK：{apkWin}");
        return new ApkLocalBuildResult(true, apkWin, null);
    }

    // ---------- WSL 调用辅助 ----------

    /// <summary>在 WSL 内跑 bash 脚本并逐行回调；<paramref name="asRoot"/> 为真则以 root 运行。</summary>
    private static async Task<int> RunBashAsync(
        string? distro, bool asRoot, string script, Action<string> onLine, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var args = BuildWslArgs(distro, asRoot, $"echo {b64}|base64 -d|bash");
        var env = new Dictionary<string, string> { ["WSL_UTF8"] = "1" };
        return await ProcessService.RunAsync("wsl.exe", args, null, onLine, ct, env).ConfigureAwait(false);
    }

    private static async Task<(int Exit, string Output)> CaptureAsync(
        string? distro, bool asRoot, string shellCommand, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var args = BuildWslArgs(distro, asRoot, shellCommand);
        var env = new Dictionary<string, string> { ["WSL_UTF8"] = "1" };
        try
        {
            var exit = await ProcessService.RunAsync("wsl.exe", args, null,
                line => sb.AppendLine(line), ct, env).ConfigureAwait(false);
            return (exit, sb.ToString());
        }
        catch
        {
            return (-1, sb.ToString());
        }
    }

    private static string BuildWslArgs(string? distro, bool asRoot, string bashCommand)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(distro)) sb.Append("-d ").Append(ProcessService.Quote(distro)).Append(' ');
        if (asRoot) sb.Append("-u root ");
        // 用 sh -c 承载命令；对 base64 管道这类无空格整体已加引号处理
        sb.Append("-e /bin/bash -c ").Append(ProcessService.Quote(bashCommand));
        return sb.ToString();
    }

    /// <summary>为 bash 单引号安全地包裹一个路径。</summary>
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
