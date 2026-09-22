namespace PyPackager.Models;

/// <summary>本机能否在本地编译 APK 的总体结论。</summary>
public enum ApkLocalVerdict
{
    /// <summary>系统不支持（非 Windows，或 Windows 版本过旧无法装 WSL2）。</summary>
    Unavailable,

    /// <summary>固件里没开虚拟化（VT-x/AMD-V），软件无法修复，必须进 BIOS/UEFI 手动开启。</summary>
    Blocked,

    /// <summary>固件虚拟化已开，但缺 Windows 功能 / WSL 发行版 / 发行版内工具链——可一键修复（可能需要重启）。</summary>
    Fixable,

    /// <summary>WSL2 可用且发行版内 buildozer + JDK17 就绪，可直接本地编译 APK。</summary>
    Ready,
}

/// <summary>单个探测项，用于界面明细展示。</summary>
public sealed record ApkProbe(string Name, ApkProbeState State, string Detail);

public enum ApkProbeState
{
    Ok,
    Missing,
    Warning,
}

/// <summary>APK 本地环境体检报告。</summary>
public sealed record ApkEnvReport(
    ApkLocalVerdict Verdict,
    bool IsWindows,
    bool WslExePresent,
    bool WslFunctional,
    string? DistroName,
    bool FirmwareVirtualization,
    bool HypervisorPresent,
    bool VmPlatformFeatureEnabled,
    bool Jdk17,
    string? JdkVersion,
    bool AndroidSdk,
    bool Buildozer,
    IReadOnlyList<ApkProbe> Probes,
    string Headline,
    string Guidance)
{
    public bool CanBuildLocally => Verdict == ApkLocalVerdict.Ready;
    public bool CanAutoFix => Verdict == ApkLocalVerdict.Fixable;
}
