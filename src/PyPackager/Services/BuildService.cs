using System.IO;
using System.Text;
using PyPackager.Models;

namespace PyPackager.Services;

/// <summary>一次打包任务的最终结果。</summary>
public sealed record BuildResult(bool Success, string? OutputExe, string? Error, int ExitCode);

/// <summary>
/// 打包流程的指挥官：把界面收集的 <see cref="BuildOptions"/> 翻译成对
/// Python 引擎脚本（build_exe.py / build_nuitka.py）的调用，解析其结构化输出，
/// 并通过事件驱动界面刷新。
/// </summary>
public sealed class BuildService
{
    /// <summary>普通日志行。</summary>
    public event Action<string>? LogReceived;

    /// <summary>进度 0-100。</summary>
    public event Action<int>? ProgressChanged;

    private readonly string _pythonPath;

    public BuildService(string? pythonPath = null)
    {
        _pythonPath = pythonPath ?? "python";
    }

    /// <summary>定位随程序分发的某个引擎脚本。</summary>
    private static string ResolveScriptPath(string scriptName)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "scripts", scriptName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // 开发期从源码目录直接运行时的兜底：向上查找仓库根 scripts\<scriptName>
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var guess = Path.Combine(dir.FullName, "scripts", scriptName);
            if (File.Exists(guess))
            {
                return guess;
            }
        }

        throw new FileNotFoundException($"找不到打包引擎脚本 scripts\\{scriptName}，请确认程序完整性。");
    }

    private void EmitLog(string line) => LogReceived?.Invoke(line);
    private void EmitProgress(int v) => ProgressChanged?.Invoke(v);

    public async Task<BuildResult> BuildExeAsync(BuildOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectPath) || !Directory.Exists(options.ProjectPath))
        {
            return new BuildResult(false, null, "项目目录无效。", -1);
        }

        var scriptName = options.Engine == BuildEngine.Nuitka ? "build_nuitka.py" : "build_exe.py";

        string scriptPath;
        try
        {
            scriptPath = ResolveScriptPath(scriptName);
        }
        catch (Exception ex)
        {
            return new BuildResult(false, null, ex.Message, -1);
        }

        var args = options.Engine == BuildEngine.Nuitka
            ? BuildNuitkaArguments(scriptPath, options)
            : BuildPyInstallerArguments(scriptPath, options);

        EmitLog($"调用引擎：{scriptName}");

        string? resultPath = null;
        string? errorMsg = null;

        void OnLine(string raw)
        {
            if (TryParseTagged(raw, "PROGRESS", out var pText) && int.TryParse(pText.Trim(), out var p))
            {
                EmitProgress(p);
                return;
            }
            if (TryParseTagged(raw, "RESULT", out var rText))
            {
                resultPath = rText.Trim();
                return;
            }
            if (TryParseTagged(raw, "ERROR", out var eText))
            {
                errorMsg = eText.Trim();
                EmitLog("错误：" + errorMsg);
                return;
            }
            if (TryParseTagged(raw, "LOG", out var lText))
            {
                EmitLog(lText);
                return;
            }

            EmitLog(raw);
        }

        var exitCode = await ProcessService.RunAsync(_pythonPath, args, options.ProjectPath, OnLine, ct)
            .ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            return new BuildResult(false, null, "已被用户取消。", exitCode);
        }

        var success = exitCode == 0 && resultPath is not null && File.Exists(resultPath);
        return new BuildResult(success, resultPath, success ? null : (errorMsg ?? $"打包失败（退出码 {exitCode}）。"), exitCode);
    }

    private static string BuildPyInstallerArguments(string scriptPath, BuildOptions o)
    {
        var sb = new StringBuilder();
        sb.Append(ProcessService.Quote(scriptPath));
        sb.Append(" --project ").Append(ProcessService.Quote(o.ProjectPath));

        AppendCommon(sb, o);

        if (o.UseUpx)
        {
            sb.Append(" --upx");
            if (!string.IsNullOrWhiteSpace(o.UpxDir))
                sb.Append(" --upx-dir ").Append(ProcessService.Quote(o.UpxDir));
        }
        foreach (var h in o.HiddenImports)
            sb.Append(" --hidden-import ").Append(ProcessService.Quote(h));

        // 版本信息（PyInstaller 通过版本文件嵌入 Windows 资源）
        if (!string.IsNullOrWhiteSpace(o.Version))
            sb.Append(" --version ").Append(ProcessService.Quote(o.Version));
        if (!string.IsNullOrWhiteSpace(o.ProductName))
            sb.Append(" --product-name ").Append(ProcessService.Quote(o.ProductName));
        if (!string.IsNullOrWhiteSpace(o.CompanyName))
            sb.Append(" --company-name ").Append(ProcessService.Quote(o.CompanyName));
        if (!string.IsNullOrWhiteSpace(o.FileDescription))
            sb.Append(" --file-description ").Append(ProcessService.Quote(o.FileDescription));
        if (!string.IsNullOrWhiteSpace(o.LegalCopyright))
            sb.Append(" --legal-copyright ").Append(ProcessService.Quote(o.LegalCopyright));

        return sb.ToString();
    }

    private static string BuildNuitkaArguments(string scriptPath, BuildOptions o)
    {
        var sb = new StringBuilder();
        sb.Append(ProcessService.Quote(scriptPath));
        sb.Append(" --project ").Append(ProcessService.Quote(o.ProjectPath));

        AppendCommon(sb, o);

        sb.Append(" --compiler ").Append(o.Compiler switch
        {
            NuitkaCompiler.MinGW64 => "mingw64",
            NuitkaCompiler.MSVC => "msvc",
            _ => "auto",
        });

        if (o.Lto) sb.Append(" --lto auto");
        if (o.Jobs > 0) sb.Append(" --jobs ").Append(o.Jobs);
        if (o.RemoveOutput) sb.Append(" --remove-output");
        if (!string.IsNullOrWhiteSpace(o.CompanyName))
            sb.Append(" --company-name ").Append(ProcessService.Quote(o.CompanyName));
        if (!string.IsNullOrWhiteSpace(o.ProductName))
            sb.Append(" --product-name ").Append(ProcessService.Quote(o.ProductName));
        if (!string.IsNullOrWhiteSpace(o.Version))
            sb.Append(" --file-version ").Append(ProcessService.Quote(o.Version));

        foreach (var h in o.HiddenImports)
            sb.Append(" --include-package ").Append(ProcessService.Quote(h));
        foreach (var p in o.Plugins)
            sb.Append(" --plugin ").Append(ProcessService.Quote(p));
        foreach (var n in o.NoFollow)
            sb.Append(" --nofollow ").Append(ProcessService.Quote(n));

        return sb.ToString();
    }

    /// <summary>两个引擎共用的参数。</summary>
    private static void AppendCommon(StringBuilder sb, BuildOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.EntryFile))
            sb.Append(" --entry ").Append(ProcessService.Quote(o.EntryFile));
        if (!string.IsNullOrWhiteSpace(o.AppName))
            sb.Append(" --name ").Append(ProcessService.Quote(o.AppName));
        if (!string.IsNullOrWhiteSpace(o.IconPath))
            sb.Append(" --icon ").Append(ProcessService.Quote(o.IconPath));

        if (o.OneFile) sb.Append(" --onefile");
        if (o.NoConsole) sb.Append(" --noconsole");

        foreach (var d in o.Data)
            sb.Append(" --data ").Append(ProcessService.Quote(d));
    }

    private static bool TryParseTagged(string line, string tag, out string content)
    {
        content = string.Empty;
        var prefix = $"[{tag}] ";
        var idx = line.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            return false;
        }
        content = line[(idx + prefix.Length)..];
        return true;
    }
}
