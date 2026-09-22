using System.Text;

namespace PyPackager.Services;

/// <summary>一次环境检测的结果快照。</summary>
public sealed record EnvReport(
    string? PythonPath,
    string? PythonVersion,
    bool PipAvailable,
    bool PyInstallerAvailable,
    string? PyInstallerVersion,
    bool NuitkaAvailable,
    string? NuitkaVersion,
    bool HasCCompiler,
    string? CCompilerName,
    bool WslAvailable)
{
    public bool CanBuildExe => PythonPath is not null && PyInstallerAvailable;
    public bool CanBuildWithNuitka => PythonPath is not null && NuitkaAvailable;

    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine(PythonPath is null
                ? "Python     : 未检测到（请安装 Python 3.8+ 并加入 PATH）"
                : $"Python     : {PythonVersion}  ({PythonPath})");
            sb.AppendLine($"pip        : {(PipAvailable ? "可用" : "不可用")}");
            sb.AppendLine(PyInstallerAvailable
                ? $"PyInstaller: {PyInstallerVersion}"
                : "PyInstaller: 未安装（打包时会自动尝试安装）");
            sb.AppendLine(NuitkaAvailable
                ? $"Nuitka     : {NuitkaVersion}"
                : "Nuitka     : 未安装（用 Nuitka 打包时会自动尝试安装）");
            sb.AppendLine(HasCCompiler
                ? $"C 编译器   : {CCompilerName}（Nuitka 就绪）"
                : "C 编译器   : 未检测到（Nuitka 首次编译会自动下载 MinGW64，需联网）");
            sb.AppendLine($"WSL        : {(WslAvailable ? "可用（APK 打包就绪）" : "不可用（APK 打包暂未启用）")}");
            return sb.ToString().TrimEnd();
        }
    }
}

/// <summary>
/// 负责探测用户机器上的构建环境。所有探测都通过运行短命令完成，
/// 不写注册表、不改环境变量，纯只读。
/// </summary>
public sealed class EnvironmentService
{
    private static readonly string[] PythonCandidates = { "python", "py", "python3" };

    public async Task<EnvReport> DetectAsync(CancellationToken ct = default)
    {
        var wsl = await DetectWslAsync(ct).ConfigureAwait(false);

        var (pythonPath, pythonVersion) = await FindPythonAsync(ct).ConfigureAwait(false);
        if (pythonPath is null)
        {
            return new EnvReport(null, null, false, false, null, false, null, false, null, wsl);
        }

        var pip = await ProbeAsync(pythonPath, "-m pip --version", ct).ConfigureAwait(false);
        var pyi = await ProbeAsync(pythonPath, "-m PyInstaller --version", ct).ConfigureAwait(false);
        var nui = await ProbeAsync(pythonPath, "-m nuitka --version", ct).ConfigureAwait(false);
        var (hasCc, ccName) = await DetectCCompilerAsync(ct).ConfigureAwait(false);

        return new EnvReport(
            pythonPath,
            pythonVersion,
            pip.ExitCode == 0,
            pyi.ExitCode == 0,
            pyi.ExitCode == 0 ? FirstLine(pyi.Output) : null,
            nui.ExitCode == 0,
            nui.ExitCode == 0 ? ExtractNuitkaVersion(nui.Output) : null,
            hasCc,
            ccName,
            wsl);
    }

    private static async Task<(string? Path, string? Version)> FindPythonAsync(CancellationToken ct)
    {
        foreach (var candidate in PythonCandidates)
        {
            var result = await ProbeAsync(candidate, "--version", ct).ConfigureAwait(false);
            if (result.ExitCode == 0 && result.Output.Contains("Python", StringComparison.OrdinalIgnoreCase))
            {
                var where = await ProbeAsync("where", candidate, ct).ConfigureAwait(false);
                var resolved = where.ExitCode == 0
                    ? where.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                    : candidate;
                return (resolved ?? candidate, result.Output.Trim());
            }
        }
        return (null, null);
    }

    /// <summary>检测 MSVC(cl) 或 MinGW(gcc) 是否在 PATH 中。</summary>
    private static async Task<(bool Has, string? Name)> DetectCCompilerAsync(CancellationToken ct)
    {
        var cl = await ProbeAsync("where", "cl", ct).ConfigureAwait(false);
        if (cl.ExitCode == 0 && !string.IsNullOrWhiteSpace(cl.Output))
        {
            return (true, "MSVC (cl.exe)");
        }

        var gcc = await ProbeAsync("where", "gcc", ct).ConfigureAwait(false);
        if (gcc.ExitCode == 0 && !string.IsNullOrWhiteSpace(gcc.Output))
        {
            return (true, "MinGW (gcc)");
        }

        return (false, null);
    }

    private static async Task<bool> DetectWslAsync(CancellationToken ct)
    {
        var result = await ProbeAsync("wsl", "--status", ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return line?.Trim() ?? text.Trim();
    }

    private static string ExtractNuitkaVersion(string output)
    {
        // nuitka --version 输出多行，首行形如 "4.2.1"
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? "已安装" : line;
    }

    private static async Task<(int ExitCode, string Output)> ProbeAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            var code = await ProcessService.RunAsync(
                fileName, arguments, null,
                line => sb.AppendLine(line),
                ct).ConfigureAwait(false);
            return (code, sb.ToString());
        }
        catch
        {
            return (-1, sb.ToString());
        }
    }
}
