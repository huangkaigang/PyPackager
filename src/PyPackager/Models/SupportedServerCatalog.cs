namespace PyPackager.Models;

/// <summary>支持目录中的一行：某个发行版家族下的代表系统与位宽支持情况。</summary>
public sealed record SupportedServerEntry(
    string FamilyLabel,
    string OsLabel,
    string Versions,
    string Archs64,
    string Archs32,
    string Note);

/// <summary>
/// 「支持的服务器系统」目录：既是界面展示的支持列表，也是系统识别时发行版 ID 的单一数据源，
/// 避免界面文案与实际识别逻辑各写一份而漂移。
/// </summary>
public static class SupportedServerCatalog
{
    // ---- 发行版 ID（/etc/os-release 的 ID），供 ServerProvisionService 识别家族 ----
    public static readonly string[] DebianIds =
        { "ubuntu", "debian", "linuxmint", "pop", "elementary", "zorin", "deepin", "uos", "raspbian", "kali", "parrot" };

    public static readonly string[] RhelIds =
        { "centos", "rhel", "rocky", "almalinux", "fedora", "ol", "amzn", "anolis", "alinux",
          "openeuler", "euleros", "openEuler", "kylin", "neokylin", "uos-server", "scientific", "mageia" };

    public static readonly string[] SuseIds =
        { "opensuse", "opensuse-leap", "opensuse-tumbleweed", "sles", "sled", "suse" };

    // ---- 界面展示的支持列表（含 32 / 64 位） ----
    public static readonly IReadOnlyList<SupportedServerEntry> Entries = new[]
    {
        new SupportedServerEntry(
            "Debian 系 · apt",
            "Ubuntu / Debian / LinuxMint / Deepin / UOS / Raspbian",
            "Ubuntu 18.04·20.04·22.04·24.04；Debian 10·11·12·13",
            "x86_64 · ARM64(aarch64)",
            "ARM 32(armhf/armv7)",
            "自动配置 apt 源；32 位 ARM 可装 Docker 但不可编译 APK"),
        new SupportedServerEntry(
            "RHEL 系 · yum/dnf",
            "CentOS / RHEL / Rocky / Alma / Fedora / openEuler / 麒麟 / Anolis / Alibaba",
            "CentOS 7·8·9(Stream)；RHEL 7·8·9；Rocky/Alma 8·9；openEuler 20.03+",
            "x86_64 · ARM64(aarch64)",
            "—（官方无 32 位）",
            "自动配置 yum/dnf 源；CentOS 7 等老内核同样支持"),
        new SupportedServerEntry(
            "SUSE 系 · zypper",
            "openSUSE Leap / Tumbleweed / SLES",
            "Leap 15.x；Tumbleweed；SLES 12·15",
            "x86_64 · ARM64(aarch64)",
            "ARM 32(armv7, 部分版本)",
            "自动配置 zypper 源"),
        new SupportedServerEntry(
            "位宽说明",
            "64 位 = 完整支持（装 Docker + 编译 APK）",
            "32 位 = 仅可安装/管理 Docker",
            "x86_64 / aarch64",
            "armhf / armv7 / i386",
            "APK 编译镜像(ubuntu:22.04 + Android NDK) 仅 64 位，32 位服务器无法编译 APK"),
    };

    /// <summary>把目录渲染成界面可直接显示的多行文本。</summary>
    public static IEnumerable<string> ToDisplayLines()
    {
        foreach (var e in Entries)
        {
            yield return $"◆ {e.FamilyLabel}｜{e.OsLabel}";
            yield return $"    版本：{e.Versions}";
            yield return $"    64 位：{e.Archs64}    32 位：{e.Archs32}";
            yield return $"    说明：{e.Note}";
        }
    }
}
