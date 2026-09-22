using System.IO;
using System.Text;
using PyPackager.Models;

namespace PyPackager.Services;

/// <summary>一键流程本次推进到的阶段。</summary>
public enum ApkSetupStage
{
    AlreadyReady,
    FeaturesEnabled,
    DistroInstalled,
    ToolchainInstalled,
    BlockedNeedsBios,
    Failed,
    Cancelled,
}

/// <summary>一键开启的结果。</summary>
public sealed record ApkSetupResult(bool Success, bool RebootRequired, ApkSetupStage Stage, string Message)
{
    /// <summary>用户是否需要再次点击以继续下一阶段（重启后或安装发行版后）。</summary>
    public bool NeedsAnotherClick =>
        Success && Stage is ApkSetupStage.FeaturesEnabled or ApkSetupStage.DistroInstalled;
}

/// <summary>
/// 全自动“一键开启本地 APK 打包环境”的编排器。可续跑：每次点击都先用
/// <see cref="ApkDoctorService"/> 重新体检，只推进“下一个尚未满足”的阶段——
/// 因为启用 Windows 功能后需要重启，无法在一次调用里走完。
///
/// 阶段：① 提权启用 VirtualMachinePlatform + WSL 功能（需 UAC，通常需重启）
///       ② 安装 Ubuntu 发行版（wsl --install）
///       ③ 在 Ubuntu 内安装 JDK 17 / buildozer / 编译依赖
/// 固件虚拟化未开启时（Blocked）不尝试任何修改，直接给出 BIOS 指引。
/// </summary>
public sealed class ApkSetupService
{
    private readonly ApkDoctorService _doctor;

    public ApkSetupService(ApkDoctorService doctor) => _doctor = doctor;

    public async Task<ApkSetupResult> SetupAsync(
        Action<string> log, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        log("—— 一键开启本地 APK 打包环境 ——");
        log("先重新体检，确定下一步……");
        var report = await _doctor.DetectAsync(log, ct).ConfigureAwait(false);
        AppServices.LastApkReport = report;
        log($"体检结论：{report.Verdict}（{report.Headline}）");

        switch (report.Verdict)
        {
            case ApkLocalVerdict.Ready:
                progress?.Report(100);
                return new ApkSetupResult(true, false, ApkSetupStage.AlreadyReady,
                    "本地环境已就绪，可直接选择“本地 WSL”后端编译 APK。");

            case ApkLocalVerdict.Blocked:
                return new ApkSetupResult(false, false, ApkSetupStage.BlockedNeedsBios,
                    "固件里没开虚拟化（VT-x/AMD-V），这一步软件改不了。\n" +
                    "请重启进入 BIOS/UEFI 开启虚拟化后再试；期间可改用“云端服务器打包”。\n\n" +
                    report.Guidance);

            case ApkLocalVerdict.Unavailable:
                return new ApkSetupResult(false, false, ApkSetupStage.Failed,
                    "本机系统不满足 WSL2 要求，无法本地编译。请改用“云端服务器打包”。");
        }

        // ---- Fixable：只推进下一个未满足的阶段 ----
        if (!report.VmPlatformFeatureEnabled || !report.WslExePresent)
        {
            return await EnableWindowsFeaturesAsync(log, progress, ct).ConfigureAwait(false);
        }

        if (!report.WslFunctional || report.DistroName is null)
        {
            return await InstallDistroAsync(log, progress, ct).ConfigureAwait(false);
        }

        if (!report.Buildozer || !report.Jdk17)
        {
            return await ProvisionToolchainAsync(report.DistroName, log, progress, ct).ConfigureAwait(false);
        }

        progress?.Report(100);
        return new ApkSetupResult(true, false, ApkSetupStage.AlreadyReady, "本地环境已就绪。");
    }

    // ---------- 阶段①：启用 Windows 功能（提权 + 可能重启）----------

    private static async Task<ApkSetupResult> EnableWindowsFeaturesAsync(
        Action<string> log, IProgress<int>? progress, CancellationToken ct)
    {
        log("阶段①：提权启用 Windows“虚拟机平台 / 适用于 Linux 的 Windows 子系统”功能（会弹 UAC，请点“是”）……");
        progress?.Report(10);

        const string script =
            "Write-Output 'STEP=enable-features'\n" +
            "$names = @('VirtualMachinePlatform','Microsoft-Windows-Subsystem-Linux')\n" +
            "$reboot = $false\n" +
            "foreach ($n in $names) {\n" +
            "  try {\n" +
            "    $r = Enable-WindowsOptionalFeature -Online -FeatureName $n -All -NoRestart\n" +
            "    if ($r.RestartNeeded -ne 'No') { $reboot = $true }\n" +
            "    Write-Output ('FEATURE=' + $n + ' OK')\n" +
            "  } catch {\n" +
            "    Write-Output ('FEATURE=' + $n + ' ERR ' + $_.Exception.Message)\n" +
            "  }\n" +
            "}\n" +
            "Write-Output ('REBOOT=' + $reboot)\n";

        var logFile = TempLog();
        var exit = await ProcessService.RunElevatedAsync(script, logFile, ct).ConfigureAwait(false);
        var lines = ReadAndDelete(logFile);
        foreach (var l in lines) log("  " + l);

        if (exit == -1)
        {
            return new ApkSetupResult(false, false, ApkSetupStage.Cancelled,
                "提权被取消（UAC 未通过）。如需本地打包，请重新点击并在弹窗中选择“是”；或改用云端打包。");
        }

        var reboot = lines.Any(l => l.Trim().Equals("REBOOT=True", StringComparison.OrdinalIgnoreCase));
        var anyOk = lines.Any(l => l.Contains(" OK", StringComparison.Ordinal));
        var anyErr = lines.Any(l => l.Contains(" ERR ", StringComparison.Ordinal));

        progress?.Report(35);
        if (!anyOk && anyErr)
        {
            return new ApkSetupResult(false, reboot, ApkSetupStage.Failed,
                "启用 Windows 功能失败，请看日志中的错误。若提示需要 TrustedInstaller/权限，请确认以管理员身份重试。");
        }

        return new ApkSetupResult(true, true, ApkSetupStage.FeaturesEnabled,
            "已启用 Windows“虚拟机平台 / WSL”功能。\n" +
            (reboot ? "请【重启电脑】，重启后回到本页再次点击“一键开启”，继续安装 Ubuntu 与工具链。"
                    : "请再次点击“一键开启”，继续安装 Ubuntu 发行版。"));
    }

    // ---------- 阶段②：安装 Ubuntu 发行版 ----------

    private static async Task<ApkSetupResult> InstallDistroAsync(
        Action<string> log, IProgress<int>? progress, CancellationToken ct)
    {
        log("阶段②：安装 Ubuntu 发行版（wsl --install -d Ubuntu --no-launch），首次需下载，请稍候……");
        progress?.Report(40);

        const string script =
            "Write-Output 'STEP=install-distro'\n" +
            "wsl --set-default-version 2 2>&1 | ForEach-Object { Write-Output $_ }\n" +
            "wsl --install -d Ubuntu --no-launch 2>&1 | ForEach-Object { Write-Output $_ }\n" +
            "Write-Output 'WSLINSTALL_DONE'\n";

        var logFile = TempLog();
        var exit = await ProcessService.RunElevatedAsync(script, logFile, ct).ConfigureAwait(false);
        var lines = ReadAndDelete(logFile);
        foreach (var l in lines) log("  " + l);

        if (exit == -1)
        {
            return new ApkSetupResult(false, false, ApkSetupStage.Cancelled, "提权被取消（UAC 未通过）。");
        }

        var done = lines.Any(l => l.Contains("WSLINSTALL_DONE", StringComparison.Ordinal));
        var rebootHint = lines.Any(l => l.Contains("restart", StringComparison.OrdinalIgnoreCase)
                                      || l.Contains("reboot", StringComparison.OrdinalIgnoreCase)
                                      || l.Contains("重启", StringComparison.Ordinal));

        progress?.Report(70);
        return new ApkSetupResult(done, rebootHint, ApkSetupStage.DistroInstalled,
            done
                ? "Ubuntu 发行版安装完成。\n" + (rebootHint
                    ? "如提示需要重启，请先重启，然后再次点击“一键开启”继续安装工具链。"
                    : "请再次点击“一键开启”，继续在 Ubuntu 内安装 JDK 17 与 buildozer。")
                : "Ubuntu 安装未确认成功，请看日志。若提示需要更新 WSL 内核或重启，请照做后再次点击继续。");
    }

    // ---------- 阶段③：在 Ubuntu 内安装工具链 ----------

    private static async Task<ApkSetupResult> ProvisionToolchainAsync(
        string distro, Action<string> log, IProgress<int>? progress, CancellationToken ct)
    {
        log($"阶段③：在 Ubuntu（{distro}）内安装 JDK 17、buildozer 及编译依赖（联网，耗时可能 10+ 分钟）……");
        progress?.Report(72);

        const string script = """
            set -e
            export DEBIAN_FRONTEND=noninteractive
            echo "[apt] update"
            apt-get update -y
            echo "[apt] install toolchain"
            apt-get install -y --no-install-recommends \
              git zip unzip ca-certificates curl wget file \
              openjdk-17-jdk autoconf libtool libltdl-dev pkg-config \
              build-essential automake cmake \
              python3 python3-pip python3-venv python3-dev \
              libssl-dev libffi-dev zlib1g-dev
            echo "[pip] buildozer"
            python3 -m pip install --upgrade pip setuptools wheel
            python3 -m pip install --upgrade "buildozer" "cython==0.29.36"
            echo "[verify]"
            java -version 2>&1 | head -1
            buildozer --version 2>&1 | head -1
            echo "PROVISION_DONE"
            """;

        var exit = await WslRootBashAsync(distro, script, line =>
        {
            if (!string.IsNullOrWhiteSpace(line)) log("  " + line.TrimEnd());
        }, ct).ConfigureAwait(false);

        var ok = exit == 0;
        progress?.Report(ok ? 100 : 80);
        return new ApkSetupResult(ok, false,
            ok ? ApkSetupStage.ToolchainInstalled : ApkSetupStage.Failed,
            ok
                ? "本地工具链安装完成！现在可选择“本地 WSL”后端编译 APK。\n" +
                  "提示：首次执行 buildozer 构建时，它还会自动下载 Android SDK/NDK（较大，需联网与耐心）。"
                : $"工具链安装失败（退出码 {exit}）。常见原因是网络问题；可重试，或改用“云端服务器打包”。");
    }

    // ---------- 辅助 ----------

    /// <summary>以 root 在指定 WSL 发行版内跑一段 bash（base64 规避引号问题），逐行回调。</summary>
    private static async Task<int> WslRootBashAsync(
        string distro, string script, Action<string> onLine, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var args = $"-d {ProcessService.Quote(distro)} -u root -e /bin/bash -c \"echo {b64}|base64 -d|bash\"";
        var env = new Dictionary<string, string> { ["WSL_UTF8"] = "1" };
        return await ProcessService.RunAsync("wsl.exe", args, null, onLine, ct, env).ConfigureAwait(false);
    }

    private static string TempLog() =>
        Path.Combine(Path.GetTempPath(), $"pypk_apksetup_{Guid.NewGuid():N}.log");

    private static string[] ReadAndDelete(string path)
    {
        try
        {
            var lines = File.Exists(path)
                ? File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
                : Array.Empty<string>();
            return lines;
        }
        catch { return Array.Empty<string>(); }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ } }
    }
}
