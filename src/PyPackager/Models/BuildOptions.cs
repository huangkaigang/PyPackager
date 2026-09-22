namespace PyPackager.Models;

/// <summary>可用的打包引擎。</summary>
public enum BuildEngine
{
    /// <summary>成熟、快、无需 C 编译器。适合大多数场景。</summary>
    PyInstaller,

    /// <summary>编译为 C 再编译机器码，运行更快、更难反编译，适合商业保护。需要 C 编译器（本机用 Zig，自动下载）。</summary>
    Nuitka,
}

/// <summary>Nuitka 使用的 C 编译器偏好。</summary>
public enum NuitkaCompiler
{
    /// <summary>让 Nuitka 自行选择/自动下载（Windows 上默认 Zig）。</summary>
    Auto,
    MinGW64,
    MSVC,
}

/// <summary>
/// 一次 EXE 打包任务的全部配置。由界面收集，交给 BuildService 翻译成命令行参数。
/// </summary>
public sealed class BuildOptions
{
    public BuildEngine Engine { get; set; } = BuildEngine.PyInstaller;

    public string ProjectPath { get; set; } = string.Empty;
    public string? EntryFile { get; set; }
    public string? AppName { get; set; }
    public string? IconPath { get; set; }

    public bool OneFile { get; set; } = true;
    public bool NoConsole { get; set; }

    // ---- 目标平台：决定用哪个 Python 运行时打包（进而决定 EXE 的兼容系统与位数）----
    public WindowsTarget Target { get; set; } = WindowsTarget.Auto;
    public CpuArch Arch { get; set; } = CpuArch.Auto;

    // ---- 两引擎共享：数据文件 / 隐藏导入 / 版本元数据 ----
    public List<string> Data { get; } = new();
    public List<string> HiddenImports { get; } = new();
    public string? Version { get; set; }
    public string? CompanyName { get; set; }
    public string? ProductName { get; set; }
    public string? FileDescription { get; set; }
    public string? LegalCopyright { get; set; }

    // ---- PyInstaller 专属 ----
    public bool UseUpx { get; set; }
    public string? UpxDir { get; set; }

    // ---- Nuitka 专属 ----
    public NuitkaCompiler Compiler { get; set; } = NuitkaCompiler.Auto;
    public bool Lto { get; set; }
    public int Jobs { get; set; }
    public bool RemoveOutput { get; set; } = true;
    public List<string> Plugins { get; } = new();
    public List<string> NoFollow { get; } = new();
}
