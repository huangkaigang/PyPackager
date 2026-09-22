namespace PyPackager.Models;

/// <summary>目标 CPU 架构。打包出的 EXE 位数由所用 Python 的位数决定，无法交叉编译。</summary>
public enum CpuArch
{
    /// <summary>跟随目标系统，默认按 64 位处理（Auto 目标下则用本机 Python）。</summary>
    Auto,
    X86,
    X64,
}

/// <summary>
/// 目标 Windows 世代。把用户关心的众多版本按“内核世代 + 所需 Python/PyInstaller”归并，
/// 每一档对应一套能产出兼容 EXE 的工具链。Server 版本按其对应的客户端内核归入同档。
/// </summary>
public enum WindowsTarget
{
    /// <summary>不指定：用本机系统 Python（或内部 venv），不下载运行时。</summary>
    Auto,
    /// <summary>Windows 7 SP1 / Server 2008 R2。</summary>
    Win7,
    /// <summary>Windows 8 / 8.1 / Server 2012 / 2012 R2。</summary>
    Win8,
    /// <summary>Windows 10 / Server 2016 / 2019。</summary>
    Win10,
    /// <summary>Windows 11 / Server 2022 / 2025 / 2026。</summary>
    Win11,
}

/// <summary>某个目标世代对应的工具链画像。</summary>
public sealed record TargetProfile(
    WindowsTarget Target,
    string Display,
    /// <summary>需要下载的 Python 版本；Auto 为 null（用本机）。</summary>
    string? PythonVersion,
    /// <summary>PyInstaller 的 pip 版本约束。</summary>
    string PyInstallerSpec,
    /// <summary>Nuitka 的 pip 版本约束。</summary>
    string NuitkaSpec,
    /// <summary>该世代是否支持用 Nuitka 产出兼容 EXE。</summary>
    bool NuitkaSupported,
    /// <summary>给用户的兼容性说明。</summary>
    string Note)
{
    /// <summary>该目标是否需要下载托管运行时（Auto 不需要）。</summary>
    public bool NeedsManagedRuntime => PythonVersion is not null;
}

/// <summary>目标世代 → 工具链画像的静态目录。版本均可在 NuGet python/pythonx86 包中获取。</summary>
public static class TargetCatalog
{
    // 各档选用的 Python 版本（均在 NuGet 有 x86 与 x64 包，且已联网核实存在）：
    //   Win7  → 3.8.10（最后一个支持 Win7 的 CPython）
    //   Win8+ → 3.12.10（支持 Win8.1 起、wheel 生态最广的稳定版）
    private const string PyWin7 = "3.8.10";
    private const string PyModern = "3.12.10";

    public static TargetProfile Get(WindowsTarget target) => target switch
    {
        WindowsTarget.Win7 => new TargetProfile(
            WindowsTarget.Win7,
            "Windows 7 / Server 2008 R2",
            PyWin7,
            "pyinstaller==5.13.2",   // PyInstaller 6.x 已放弃 Win7，5.13.2 是最后兼容版
            "nuitka",
            NuitkaSupported: false,  // 新版 Nuitka 产物不再保证 Win7 可运行
            "为兼容 Win7，将使用 Python 3.8.10 + PyInstaller 5.13.2（6.x 起已放弃 Win7）。此目标不支持 Nuitka，请改用 PyInstaller。"),

        WindowsTarget.Win8 => new TargetProfile(
            WindowsTarget.Win8,
            "Windows 8 / 8.1 / Server 2012 R2",
            PyModern,
            "pyinstaller",
            "nuitka",
            NuitkaSupported: true,
            "使用 Python 3.12（支持 Win8.1 起）。产物可在 Win8/8.1 及更新系统运行。"),

        WindowsTarget.Win10 => new TargetProfile(
            WindowsTarget.Win10,
            "Windows 10 / Server 2016 / 2019",
            PyModern,
            "pyinstaller",
            "nuitka",
            NuitkaSupported: true,
            "使用 Python 3.12。产物可在 Win10 / Server 2016 及更新系统运行。"),

        WindowsTarget.Win11 => new TargetProfile(
            WindowsTarget.Win11,
            "Windows 11 / Server 2022 / 2025 / 2026",
            PyModern,
            "pyinstaller",
            "nuitka",
            NuitkaSupported: true,
            "使用 Python 3.12。产物面向 Win11 / 最新 Server，同时向下兼容 Win10。"),

        _ => new TargetProfile(
            WindowsTarget.Auto,
            "自动 / 当前系统（用本机 Python）",
            null,
            "pyinstaller",
            "nuitka",
            NuitkaSupported: true,
            "不下载运行时，直接用本机系统 Python（缺工具时落到内部虚拟环境）。产物兼容性取决于本机 Python 版本。"),
    };

    /// <summary>目标架构 Auto 时的实际架构：显式目标默认按 64 位下载运行时。</summary>
    public static CpuArch EffectiveArch(CpuArch arch) => arch == CpuArch.Auto ? CpuArch.X64 : arch;

    /// <summary>架构对应的 NuGet 包 id：x86=pythonx86，x64=python。</summary>
    public static string NuGetPackageId(CpuArch arch) =>
        EffectiveArch(arch) == CpuArch.X86 ? "pythonx86" : "python";

    public static string ArchToken(CpuArch arch) =>
        EffectiveArch(arch) == CpuArch.X86 ? "x86" : "x64";

    public static string ArchDisplay(CpuArch arch) =>
        EffectiveArch(arch) == CpuArch.X86 ? "x86（32 位）" : "x64（64 位）";
}
