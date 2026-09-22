using System.IO;

namespace PyPackager.Services;

/// <summary>创建/初始化内部虚拟环境的结果。</summary>
public sealed record VenvResult(bool Success, string? PythonPath, string? Error);

/// <summary>
/// 管理“内部虚拟环境”。当系统 Python 缺少 PyInstaller/Nuitka 时，
/// 在 %LOCALAPPDATA%\PyPackager\venv 下基于系统 Python 创建一个隔离的 venv，
/// 并把所需工具装进去，从而不依赖也不污染用户的全局环境。
/// </summary>
public sealed class VenvService
{
    /// <summary>内部 venv 是否已存在。</summary>
    public bool Exists() => File.Exists(AppPaths.VenvPython);

    /// <summary>
    /// 确保内部 venv 存在且装有指定包。已存在则只补装缺失的包。
    /// </summary>
    /// <param name="basePython">用于创建 venv 的系统 Python 路径。</param>
    /// <param name="packages">需要安装的 pip 包名，如 pyinstaller / nuitka。</param>
    /// <param name="log">实时日志回调。</param>
    /// <param name="progress">进度回调 0-100。</param>
    public async Task<VenvResult> EnsureAsync(
        string basePython,
        IReadOnlyCollection<string> packages,
        Action<string> log,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(basePython))
        {
            return new VenvResult(false, null, "未找到系统 Python，无法创建内部虚拟环境。请先安装 Python 3.8+。");
        }

        try
        {
            AppPaths.EnsureRoot();

            if (!Exists())
            {
                log($"正在基于系统 Python 创建内部虚拟环境：{AppPaths.VenvDir}");
                progress?.Report(5);
                var createCode = await ProcessService.RunAsync(
                    basePython, $"-m venv {ProcessService.Quote(AppPaths.VenvDir)}",
                    null, line => log("  " + line), ct).ConfigureAwait(false);

                if (createCode != 0 || !Exists())
                {
                    return new VenvResult(false, null, $"创建虚拟环境失败（退出码 {createCode}）。");
                }
                log("虚拟环境创建成功。");
            }
            else
            {
                log($"内部虚拟环境已存在：{AppPaths.VenvDir}");
            }

            progress?.Report(20);

            var venvPython = AppPaths.VenvPython;

            // 升级 pip（失败不致命）
            log("升级 venv 内的 pip……");
            await ProcessService.RunAsync(venvPython, "-m pip install --upgrade pip",
                null, line => log("  " + line), ct).ConfigureAwait(false);
            progress?.Report(35);

            // 安装所需工具
            if (packages.Count > 0)
            {
                var pkgList = string.Join(" ", packages);
                log($"在内部虚拟环境中安装：{pkgList}（可能需要几分钟）……");
                var installCode = await ProcessService.RunAsync(
                    venvPython, $"-m pip install --upgrade {pkgList}",
                    null, line => log("  " + line), ct).ConfigureAwait(false);

                if (installCode != 0)
                {
                    return new VenvResult(false, null, $"安装 {pkgList} 失败（退出码 {installCode}）。请检查网络或 pip 源。");
                }
                log("工具安装完成。");
            }

            progress?.Report(100);
            return new VenvResult(true, venvPython, null);
        }
        catch (OperationCanceledException)
        {
            return new VenvResult(false, null, "已取消。");
        }
        catch (Exception ex)
        {
            return new VenvResult(false, null, ex.Message);
        }
    }

    /// <summary>删除内部虚拟环境（用于“重置”）。</summary>
    public bool Reset(Action<string> log)
    {
        try
        {
            if (Directory.Exists(AppPaths.VenvDir))
            {
                log($"正在删除内部虚拟环境：{AppPaths.VenvDir}");
                Directory.Delete(AppPaths.VenvDir, recursive: true);
                log("已删除。");
            }
            return true;
        }
        catch (Exception ex)
        {
            log($"删除失败：{ex.Message}");
            return false;
        }
    }
}
