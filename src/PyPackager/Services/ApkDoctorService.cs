using System.Text;
using PyPackager.Models;

namespace PyPackager.Services;

/// <summary>
/// APK 本地环境“体检医生”：判断本机能否在本地（WSL2 内的 buildozer）编译 APK，
/// 并把结论分成四档——不可用 / 需进 BIOS / 可一键修复 / 已就绪。
///
/// 关键区分点：<b>固件虚拟化(VT-x/AMD-V) 是否开启</b> 与 <b>Windows“虚拟机平台”功能是否启用</b>
/// 是两回事。前者软件改不了（必须进 BIOS），后者可由一键流程提权启用 + 重启解决。
/// 所有探测均只读，不改系统。
/// </summary>
public sealed class ApkDoctorService
{
    private const string WslMarker = "__WSL_OK__";
    private static readonly Dictionary<string, string> Utf8Env = new() { ["WSL_UTF8"] = "1" };

    public async Task<ApkEnvReport> DetectAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var probes = new List<ApkProbe>();

        // 0) 操作系统与版本闸门：WSL2 需要 Windows 10 2004(build 19041) 及以上。
        var isWindows = OperatingSystem.IsWindows();
        var build = isWindows ? Environment.OSVersion.Version.Build : 0;
        if (!isWindows || build < 19041)
        {
            probes.Add(new ApkProbe("操作系统", ApkProbeState.Missing,
                isWindows ? $"Windows build {build}（过低）" : "非 Windows"));
            return Report(ApkLocalVerdict.Unavailable, isWindows, false, false, null,
                false, false, false, false, null, false, false, probes,
                "本机系统不满足 WSL2 要求",
                "本地编译 APK 依赖 WSL2，需要 Windows 10 2004（build 19041）或更高版本。\n" +
                "当前系统不满足，无法本地编译。请改用“云端服务器打包”。");
        }

        // 1) 固件虚拟化 + 是否已有 Hypervisor 在运行
        var (hyp, virtfw, slat) = await ProbeVirtualizationAsync(ct).ConfigureAwait(false);
        var firmwareVirt = hyp || virtfw;
        probes.Add(new ApkProbe("固件虚拟化 (VT-x/AMD-V)",
            firmwareVirt ? ApkProbeState.Ok : ApkProbeState.Missing,
            firmwareVirt ? (hyp ? "已开启（Hypervisor 运行中）" : "已开启") : "未在固件中开启"));

        if (!firmwareVirt)
        {
            probes.Add(new ApkProbe("WSL2", ApkProbeState.Missing, "被固件虚拟化阻塞，未检测"));
            return Report(ApkLocalVerdict.Blocked, true, false, false, null,
                false, hyp, false, false, null, false, false, probes,
                "固件里没开虚拟化——软件无法修复",
                "CPU 支持虚拟化，但在主板 BIOS/UEFI 里被关闭了（或处于被占用状态）。\n" +
                "这一步软件改不了，需要你手动操作：\n" +
                "  1) 重启电脑，开机时进入 BIOS/UEFI（常见键：Del / F2 / F10 / Esc）；\n" +
                "  2) 找到虚拟化设置并开启：\n" +
                "     · Intel 平台：Intel Virtualization Technology / Intel VT-x / SVM → Enabled\n" +
                "     · AMD 平台：SVM Mode → Enabled\n" +
                "  3) 保存退出（F10），进入系统后再点“检测本地环境”。\n" +
                "开启前无法本地编译，可先用“云端服务器打包”。");
        }

        // 2) Windows“虚拟机平台”功能状态（只读；无需管理员也能查询）
        var vmp = await ProbeVmPlatformFeatureAsync(ct).ConfigureAwait(false);
        probes.Add(new ApkProbe("Windows“虚拟机平台”功能",
            vmp switch { VmpState.Enabled => ApkProbeState.Ok, VmpState.Unknown => ApkProbeState.Warning, _ => ApkProbeState.Missing },
            vmp switch { VmpState.Enabled => "已启用", VmpState.Pending => "已启用（待重启）", VmpState.Unknown => "状态未知", _ => "未启用" }));

        // 3) wsl.exe 是否存在
        var wslPresent = await ProbeWslPresentAsync(ct).ConfigureAwait(false);
        probes.Add(new ApkProbe("WSL 程序 (wsl.exe)",
            wslPresent ? ApkProbeState.Ok : ApkProbeState.Missing,
            wslPresent ? "已安装" : "未安装"));

        // 4) WSL2 是否真的能跑起来（默认发行版可执行命令）
        var wslFunctional = false;
        string? distro = null;
        if (wslPresent)
        {
            wslFunctional = await ProbeWslFunctionalAsync(ct).ConfigureAwait(false);
            if (wslFunctional) distro = await ProbeDefaultDistroAsync(ct).ConfigureAwait(false);
        }
        probes.Add(new ApkProbe("WSL2 运行时",
            wslFunctional ? ApkProbeState.Ok : ApkProbeState.Missing,
            wslFunctional ? $"可用{(distro is null ? "" : $"（发行版：{distro}）")}" : "不可用（未装发行版 / 虚拟机平台未启用 / 需重启）"));

        // 5) 发行版内工具链（仅当 WSL 可用）
        bool jdk17 = false, sdk = false, buildozer = false;
        string? jdkVersion = null;
        if (wslFunctional)
        {
            var tools = await ProbeWslToolchainAsync(ct).ConfigureAwait(false);
            jdk17 = tools.Jdk17;
            jdkVersion = tools.JdkVersion;
            sdk = tools.Sdk;
            buildozer = tools.Buildozer;

            probes.Add(new ApkProbe("JDK 17（WSL 内）",
                jdk17 ? ApkProbeState.Ok : ApkProbeState.Missing,
                jdk17 ? jdkVersion ?? "已安装" : "未安装或非 17"));
            probes.Add(new ApkProbe("Android SDK（WSL 内）",
                sdk ? ApkProbeState.Ok : ApkProbeState.Warning,
                sdk ? "已检测到" : "未检测到（buildozer 首次构建会自动下载）"));
            probes.Add(new ApkProbe("buildozer（WSL 内）",
                buildozer ? ApkProbeState.Ok : ApkProbeState.Missing,
                buildozer ? "已安装" : "未安装"));
        }
        else
        {
            probes.Add(new ApkProbe("JDK 17（WSL 内）", ApkProbeState.Missing, "WSL 未就绪，未检测"));
            probes.Add(new ApkProbe("Android SDK（WSL 内）", ApkProbeState.Missing, "WSL 未就绪，未检测"));
            probes.Add(new ApkProbe("buildozer（WSL 内）", ApkProbeState.Missing, "WSL 未就绪，未检测"));
        }

        // 6) 结论
        if (wslFunctional && buildozer && jdk17)
        {
            return Report(ApkLocalVerdict.Ready, true, wslPresent, true, distro,
                firmwareVirt, hyp, vmp is VmpState.Enabled or VmpState.Pending, jdk17, jdkVersion, sdk, buildozer, probes,
                "本地环境已就绪，可直接编译 APK",
                $"WSL2{(distro is null ? "" : $"（{distro}）")}内 buildozer 与 JDK 17 均已就绪。\n" +
                "选择“本地 WSL”后端即可开始打包。首次构建 buildozer 会联网下载 Android SDK/NDK，耗时较长请耐心等待。");
        }

        // 到这里：固件 OK，但缺 Windows 功能 / 发行版 / 工具链 → 可一键修复
        var missing = new List<string>();
        if (vmp is VmpState.Disabled) missing.Add("Windows“虚拟机平台”功能");
        if (!wslPresent) missing.Add("WSL 程序");
        if (!wslFunctional) missing.Add("WSL2 发行版（Ubuntu）");
        else
        {
            if (!jdk17) missing.Add("JDK 17");
            if (!buildozer) missing.Add("buildozer");
        }

        return Report(ApkLocalVerdict.Fixable, true, wslPresent, wslFunctional, distro,
            firmwareVirt, hyp, vmp is VmpState.Enabled or VmpState.Pending, jdk17, jdkVersion, sdk, buildozer, probes,
            "本地环境可一键修复",
            "固件虚拟化已开启，但还缺：" + (missing.Count > 0 ? string.Join("、", missing) : "部分组件") + "。\n" +
            "点“一键开启本地打包环境”，工具会自动：\n" +
            "  · 提权启用 Windows“虚拟机平台 / WSL”功能（弹 UAC，可能需要重启）；\n" +
            "  · 安装 Ubuntu 发行版（wsl --install）；\n" +
            "  · 在 Ubuntu 内安装 JDK 17、buildozer 及编译依赖。\n" +
            "如中途提示需重启，请重启后再次点击本按钮继续（流程可续跑）。\n" +
            "在本地就绪前，也可以直接用“云端服务器打包”。");
    }

    // ---------- 探测实现 ----------

    private enum VmpState { Enabled, Pending, Disabled, Unknown }

    private static async Task<(bool Hyp, bool VirtFw, bool Slat)> ProbeVirtualizationAsync(CancellationToken ct)
    {
        const string script =
            "$p = Get-CimInstance Win32_Processor | Select-Object -First 1\n" +
            "$c = Get-CimInstance Win32_ComputerSystem\n" +
            "\"hyp=$($c.HypervisorPresent)\"\n" +
            "\"virtfw=$($p.VirtualizationFirmwareEnabled)\"\n" +
            "\"slat=$($p.SecondLevelAddressTranslationExtensions)\"";
        var (exit, output) = await RunCaptureAsync("powershell.exe", EncodedPsArgs(script), null, ct).ConfigureAwait(false);
        if (exit != 0 && string.IsNullOrWhiteSpace(output)) return (false, false, false);
        return (ParseBool(output, "hyp"), ParseBool(output, "virtfw"), ParseBool(output, "slat"));
    }

    private static async Task<VmpState> ProbeVmPlatformFeatureAsync(CancellationToken ct)
    {
        const string script =
            "try { $s = (Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State; \"vmp=$s\" } " +
            "catch { \"vmp=Unknown\" }";
        var (_, output) = await RunCaptureAsync("powershell.exe", EncodedPsArgs(script), null, ct).ConfigureAwait(false);
        var val = ParseValue(output, "vmp");
        if (string.IsNullOrEmpty(val)) return VmpState.Unknown;
        if (val.Contains("Pending", StringComparison.OrdinalIgnoreCase)) return VmpState.Pending;
        if (val.Contains("Enabled", StringComparison.OrdinalIgnoreCase)) return VmpState.Enabled;
        if (val.Contains("Disabled", StringComparison.OrdinalIgnoreCase)) return VmpState.Disabled;
        return VmpState.Unknown;
    }

    private static async Task<bool> ProbeWslPresentAsync(CancellationToken ct)
    {
        var (exit, output) = await RunCaptureAsync("where.exe", "wsl", null, ct).ConfigureAwait(false);
        return exit == 0 && output.Contains("wsl", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ProbeWslFunctionalAsync(CancellationToken ct)
    {
        // 无发行版时 wsl 仍可能返回退出码 0，因此以 marker 是否出现为准，而非退出码。
        var (_, output) = await RunCaptureAsync(
            "wsl.exe", $"-e /bin/sh -c \"echo {WslMarker}\"", Utf8Env, ct).ConfigureAwait(false);
        return output.Contains(WslMarker, StringComparison.Ordinal);
    }

    private static async Task<string?> ProbeDefaultDistroAsync(CancellationToken ct)
    {
        var (exit, output) = await RunCaptureAsync("wsl.exe", "-l -q", Utf8Env, ct).ConfigureAwait(false);
        if (exit != 0) return null;
        var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.Contains("适用于", StringComparison.Ordinal) && !l.Contains("Windows Subsystem", StringComparison.OrdinalIgnoreCase));
        return first;
    }

    private static async Task<(bool Jdk17, string? JdkVersion, bool Sdk, bool Buildozer)> ProbeWslToolchainAsync(CancellationToken ct)
    {
        const string script = """
            if command -v buildozer >/dev/null 2>&1; then
              echo "buildozer=$(buildozer --version 2>/dev/null | head -1 | tr -d '\n')"
            else
              echo "buildozer="
            fi
            JV=$(java -version 2>&1 | head -1 | tr -d '\n')
            echo "java=$JV"
            SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
            if [ -z "$SDK" ]; then
              for d in "$HOME/Android/Sdk" "$HOME/android-sdk" "$HOME/.buildozer/android/platform/android-sdk"; do
                if [ -d "$d" ]; then SDK="$d"; break; fi
              done
            fi
            echo "sdk=$SDK"
            """;
        var (_, output) = await WslBashAsync(script, ct).ConfigureAwait(false);

        var buildozerLine = ParseValue(output, "buildozer");
        var javaLine = ParseValue(output, "java");
        var sdkLine = ParseValue(output, "sdk");

        var hasBuildozer = !string.IsNullOrWhiteSpace(buildozerLine);
        var jdk17 = !string.IsNullOrWhiteSpace(javaLine) && javaLine.Contains("17.", StringComparison.Ordinal);
        var hasSdk = !string.IsNullOrWhiteSpace(sdkLine);
        return (jdk17, string.IsNullOrWhiteSpace(javaLine) ? null : javaLine.Trim(), hasSdk, hasBuildozer);
    }

    /// <summary>在 WSL 默认发行版内跑一段 bash，用 base64 规避 Windows 参数引号问题。</summary>
    private static async Task<(int Exit, string Output)> WslBashAsync(string script, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        return await RunCaptureAsync("wsl.exe", $"-e /bin/bash -c \"echo {b64}|base64 -d|bash\"", Utf8Env, ct)
            .ConfigureAwait(false);
    }

    private static string EncodedPsArgs(string script)
    {
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"-NoProfile -EncodedCommand {b64}";
    }

    private static async Task<(int Exit, string Output)> RunCaptureAsync(
        string fileName, string arguments, IDictionary<string, string>? extraEnv, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            var exit = await ProcessService.RunAsync(
                fileName, arguments, null, line => sb.AppendLine(line), ct, extraEnv).ConfigureAwait(false);
            return (exit, sb.ToString());
        }
        catch
        {
            return (-1, sb.ToString());
        }
    }

    private static bool ParseBool(string output, string key)
    {
        var v = ParseValue(output, key);
        return v.Equals("True", StringComparison.OrdinalIgnoreCase) || v.Equals("1", StringComparison.Ordinal);
    }

    private static string ParseValue(string output, string key)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            if (string.Equals(line[..idx].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return line[(idx + 1)..].Trim();
        }
        return string.Empty;
    }

    private static ApkEnvReport Report(
        ApkLocalVerdict verdict, bool isWindows, bool wslPresent, bool wslFunctional, string? distro,
        bool firmwareVirt, bool hyp, bool vmpEnabled, bool jdk17, string? jdkVersion, bool sdk, bool buildozer,
        IReadOnlyList<ApkProbe> probes, string headline, string guidance) =>
        new(verdict, isWindows, wslPresent, wslFunctional, distro, firmwareVirt, hyp, vmpEnabled,
            jdk17, jdkVersion, sdk, buildozer, probes, headline, guidance);
}
