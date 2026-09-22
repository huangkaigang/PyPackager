namespace PyPackager.Models;

/// <summary>Linux 发行版家族，决定用哪套包管理器装 Docker。</summary>
public enum LinuxFamily
{
    /// <summary>Debian / Ubuntu / Mint / Deepin / UOS 等，用 apt。</summary>
    Debian,
    /// <summary>CentOS / RHEL / Rocky / Alma / Fedora / openEuler 等，用 yum/dnf。</summary>
    Rhel,
    /// <summary>openSUSE / SLES 等，用 zypper。</summary>
    Suse,
    /// <summary>无法识别，需人工处理。</summary>
    Unknown,
}

/// <summary>远程服务器的系统与环境探测结果。</summary>
public sealed record ServerOsInfo(
    string PrettyName,
    string Id,
    string VersionId,
    string Arch,
    string Kernel,
    int Bitness,
    LinuxFamily Family,
    bool DockerInstalled,
    bool ComposeAvailable,
    IReadOnlyList<string> ExistingSubnets)
{
    /// <summary>架构位数标签，如 "64 位" / "32 位"。</summary>
    public string BitnessLabel => Bitness == 64 ? "64 位" : Bitness == 32 ? "32 位" : "未知位宽";

    /// <summary>
    /// 该架构能否安装 Docker Engine（官方提供构建）：64 位 x86/ARM 与 32 位 ARM(armhf)。
    /// 32 位 x86（i386/i686）Docker 官方不再提供，视为不可装。
    /// </summary>
    public bool DockerArchSupported => Arch switch
    {
        "x86_64" or "amd64" or "aarch64" or "arm64" => true,
        "armv7l" or "armv7" or "armhf" or "armv6l" or "armv6" => true,
        _ => false,
    };

    /// <summary>
    /// 该架构能否跑 APK 编译镜像（ubuntu:22.04 + Android SDK/NDK 仅提供 64 位）。
    /// 32 位系统即使装好 Docker 也无法编译 APK。
    /// </summary>
    public bool CompileImageSupported => Bitness == 64 && DockerArchSupported;

    /// <summary>兼容旧调用：等价于 DockerArchSupported。</summary>
    public bool ArchSupported => DockerArchSupported;
}

/// <summary>一次自动部署的结果。</summary>
public sealed record ProvisionResult(
    bool Success,
    string? ServerUrl,
    string Stage,
    string? Error = null)
{
    public static ProvisionResult Fail(string stage, string error) => new(false, null, stage, error);
    public static ProvisionResult Ok(string url) => new(true, url, "完成", null);
}
