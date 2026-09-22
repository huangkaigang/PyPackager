using PyPackager.Models;

namespace PyPackager.Services;

/// <summary>解析“该用哪个 python 来打包”的结果。</summary>
public sealed record PythonResolution(
    string PythonPath,
    bool UsedInternalVenv,
    string? Error = null,
    bool UsedManagedRuntime = false)
{
    public bool Success => Error is null;

    /// <summary>运行时来源的中文标签，用于界面提示。</summary>
    public string SourceLabel =>
        UsedManagedRuntime ? "托管运行时" : UsedInternalVenv ? "内部虚拟环境" : "本机 Python";
}

/// <summary>
/// 全局共享服务定位器。由 MainWindow 在启动时初始化一次，各视图直接取用，
/// 避免重复检测环境与重复加载历史。
/// </summary>
public static class AppServices
{
    public static EnvironmentService Environment { get; } = new();
    public static VenvService Venv { get; } = new();
    public static HistoryService History { get; } = new();
    public static RuntimeManager Runtimes { get; } = new();
    public static ApkDoctorService ApkDoctor { get; } = new();
    public static ApkSetupService ApkSetup { get; } = new(ApkDoctor);
    public static ApkLocalBuilder ApkLocal { get; } = new();
    public static ApkCloudClient ApkCloud { get; } = new();
    public static ApkPreflightService ApkPreflight { get; } = new();
    public static ServerProvisionService ServerProvision { get; } = new();

    /// <summary>最近一次环境检测结果缓存，供各页面快速读取。</summary>
    public static EnvReport? LastEnvReport { get; set; }

    /// <summary>最近一次 APK 本地环境体检结果缓存。</summary>
    public static ApkEnvReport? LastApkReport { get; set; }

    /// <summary>全局状态总线：各视图把状态文字发到这里，由 MainWindow 底部状态栏统一显示。</summary>
    public static event Action<string>? StatusChanged;

    public static void RaiseStatus(string message) => StatusChanged?.Invoke(message);

    /// <summary>某引擎在系统 Python 下对应的 pip 包名。</summary>
    private static string PackageForEngine(BuildEngine engine) =>
        engine == BuildEngine.Nuitka ? "nuitka" : "pyinstaller";

    /// <summary>某引擎对应的模块导入名（用于检测是否已安装）。</summary>
    private static string ModuleForEngine(BuildEngine engine) =>
        engine == BuildEngine.Nuitka ? "nuitka" : "PyInstaller";

    /// <summary>
    /// 解析用于打包的 Python：
    /// · 目标=Auto 且 架构=Auto → 本机系统 Python（缺工具则落到内部 venv），不下载；
    /// · 指定了目标系统或具体架构 → 用 RuntimeManager 准备对应 (版本×位数) 的托管运行时。
    /// 这正是“兼容更多 Windows / 32-64 位”的落地：产物位数与兼容系统由所选 Python 决定。
    /// </summary>
    public static async Task<PythonResolution> ResolveBuildPythonAsync(
        BuildEngine engine,
        WindowsTarget target,
        CpuArch arch,
        Action<string> log,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var profile = TargetCatalog.Get(target);

        // 引擎与目标系统的兼容性闸门
        if (engine == BuildEngine.Nuitka && !profile.NuitkaSupported)
        {
            return new PythonResolution("python", false,
                $"目标「{profile.Display}」不支持 Nuitka：新版 Nuitka 产物不保证能在该系统运行。请改用 PyInstaller，或更换目标系统。");
        }

        var needsManaged = profile.NeedsManagedRuntime || arch != CpuArch.Auto;
        if (needsManaged)
        {
            var effectiveArch = TargetCatalog.EffectiveArch(arch);
            // Auto 目标但指定了架构时，用现代 Python 版本产出对应位数的 EXE
            var version = profile.PythonVersion ?? TargetCatalog.Get(WindowsTarget.Win10).PythonVersion!;
            var spec = engine == BuildEngine.Nuitka ? profile.NuitkaSpec : profile.PyInstallerSpec;

            log($"目标平台：{profile.Display} · {TargetCatalog.ArchDisplay(effectiveArch)} → 托管 Python {version}");
            var rt = await Runtimes.EnsureAsync(version, effectiveArch, new[] { spec }, log, progress, ct)
                .ConfigureAwait(false);

            return rt.Success
                ? new PythonResolution(rt.PythonPath!, false, null, UsedManagedRuntime: true)
                : new PythonResolution("python", false, rt.Error, UsedManagedRuntime: true);
        }

        // ---- Auto：本机 Python / 内部 venv（原有行为，不下载）----
        var report = LastEnvReport ?? await Environment.DetectAsync(ct).ConfigureAwait(false);
        LastEnvReport = report;

        if (report.PythonPath is null)
        {
            return new PythonResolution("python", false,
                "未检测到系统 Python。内部虚拟环境也需要基于系统 Python 创建，请先安装 Python 3.8+ 并加入 PATH。");
        }

        var module = ModuleForEngine(engine);
        var probe = await ProcessService.RunAsync(
            report.PythonPath, $"-c \"import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('{module}') else 1)\"",
            null, _ => { }, ct).ConfigureAwait(false);

        if (probe == 0)
        {
            log($"系统 Python 已具备 {module}，直接使用：{report.PythonPath}");
            return new PythonResolution(report.PythonPath, false);
        }

        log($"系统 Python 未安装 {module}，切换到内部虚拟环境……");
        var venvResult = await Venv.EnsureAsync(
            report.PythonPath,
            new[] { PackageForEngine(engine) },
            log, progress, ct).ConfigureAwait(false);

        return venvResult.Success
            ? new PythonResolution(venvResult.PythonPath!, true)
            : new PythonResolution("python", true, venvResult.Error);
    }
}
