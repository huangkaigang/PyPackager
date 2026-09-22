using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PyPackager.Models;

namespace PyPackager.Services;

/// <summary>一个已下载/已就绪的托管 Python 运行时。</summary>
public sealed record ManagedRuntime(string PythonVersion, CpuArch Arch, string PythonPath, string RootDir, long SizeBytes);

/// <summary>运行时准备结果。</summary>
public sealed record RuntimeResult(bool Success, string? PythonPath, string? Error);

/// <summary>
/// 托管 Python 运行时管理器：为“多目标打包”按 (Python 版本 × CPU 架构) 自动下载、
/// 引导 pip、并安装打包引擎（PyInstaller/Nuitka）。
///
/// 运行时来源为 NuGet 的 python / pythonx86 包——它是完整 CPython（含 ensurepip），
/// 因此 pip 可离线引导，无需依赖常被墙的 get-pip.py。引擎包则从可配置的 pip 镜像安装。
///
/// 全部落在 %LOCALAPPDATA%\PyPackager\runtimes\py-&lt;版本&gt;-&lt;架构&gt;\ 下，不污染系统。
/// </summary>
public sealed class RuntimeManager
{
    public const string DefaultPipIndex = "https://pypi.tuna.tsinghua.edu.cn/simple";

    /// <summary>可选 pip 镜像（名称 → simple 索引 URL）。首个为默认。</summary>
    public static readonly (string Name, string Url)[] PipMirrors =
    {
        ("清华 TUNA", "https://pypi.tuna.tsinghua.edu.cn/simple"),
        ("华为云", "https://mirrors.huaweicloud.com/repository/pypi/simple"),
        ("阿里云", "https://mirrors.aliyun.com/pypi/simple"),
        ("官方 PyPI", "https://pypi.org/simple"),
    };

    private static readonly HttpClient Http = CreateClient();

    public string PipIndexUrl { get; private set; } = DefaultPipIndex;
    public string NuGetBase { get; private set; } = "https://www.nuget.org/api/v2/package";

    public RuntimeManager() => LoadConfig();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PyPackager/1.0 (+runtime-downloader)");
        return client;
    }

    // ---------- 配置持久化 ----------

    public void SetPipIndex(string url)
    {
        if (!string.IsNullOrWhiteSpace(url)) PipIndexUrl = url.Trim();
        SaveConfig();
    }

    public void SetNuGetBase(string url)
    {
        if (!string.IsNullOrWhiteSpace(url)) NuGetBase = url.Trim().TrimEnd('/');
        SaveConfig();
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(AppPaths.RuntimeConfigFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.RuntimeConfigFile));
            var root = doc.RootElement;
            if (root.TryGetProperty("pipIndexUrl", out var p) && p.GetString() is { Length: > 0 } pu) PipIndexUrl = pu;
            if (root.TryGetProperty("nugetBase", out var n) && n.GetString() is { Length: > 0 } nu) NuGetBase = nu;
        }
        catch { /* 配置损坏则用默认值 */ }
    }

    private void SaveConfig()
    {
        try
        {
            AppPaths.EnsureRoot();
            var json = JsonSerializer.Serialize(new { pipIndexUrl = PipIndexUrl, nugetBase = NuGetBase },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.RuntimeConfigFile, json, new UTF8Encoding(false));
        }
        catch { /* 忽略写入失败 */ }
    }

    // ---------- 路径 ----------

    public string RuntimeDir(string version, CpuArch arch) =>
        Path.Combine(AppPaths.RuntimesDir, $"py-{version}-{TargetCatalog.ArchToken(arch)}");

    public string PythonExe(string version, CpuArch arch) =>
        Path.Combine(RuntimeDir(version, arch), "tools", "python.exe");

    public bool IsInstalled(string version, CpuArch arch) => File.Exists(PythonExe(version, arch));

    public IReadOnlyList<ManagedRuntime> ListInstalled()
    {
        var list = new List<ManagedRuntime>();
        try
        {
            if (!Directory.Exists(AppPaths.RuntimesDir)) return list;
            foreach (var dir in Directory.GetDirectories(AppPaths.RuntimesDir))
            {
                var name = Path.GetFileName(dir);
                var parts = name.Split('-');
                if (parts.Length != 3 || parts[0] != "py") continue;
                var arch = parts[2].Equals("x86", StringComparison.OrdinalIgnoreCase) ? CpuArch.X86 : CpuArch.X64;
                var pyExe = Path.Combine(dir, "tools", "python.exe");
                if (!File.Exists(pyExe)) continue;
                list.Add(new ManagedRuntime(parts[1], arch, pyExe, dir, DirSize(dir)));
            }
        }
        catch { /* 扫描失败返回已收集部分 */ }
        return list.OrderByDescending(r => r.PythonVersion).ThenBy(r => r.Arch).ToList();
    }

    public void Delete(string version, CpuArch arch)
    {
        var dir = RuntimeDir(version, arch);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { /* 跳过不可读文件 */ }
            }
        }
        catch { /* 忽略 */ }
        return total;
    }

    // ---------- 准备运行时 ----------

    /// <summary>
    /// 确保某个 (版本×架构) 的运行时可用，并安装指定 pip 包（引擎）。
    /// 已存在则跳过下载，仅补装/校验 pip 包。
    /// </summary>
    public async Task<RuntimeResult> EnsureAsync(
        string version, CpuArch arch, IEnumerable<string> pipSpecs,
        Action<string> log, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var effective = TargetCatalog.EffectiveArch(arch);
        var pyExe = PythonExe(version, effective);

        if (!File.Exists(pyExe))
        {
            log($"未找到托管运行时 Python {version} ({TargetCatalog.ArchDisplay(effective)})，开始下载……");
            if (!await DownloadAndExtractAsync(version, effective, log, progress, ct).ConfigureAwait(false))
            {
                return new RuntimeResult(false, null, $"下载/解压 Python {version} ({TargetCatalog.ArchToken(effective)}) 失败。");
            }
            if (!File.Exists(pyExe))
            {
                return new RuntimeResult(false, null, "运行时解压后未找到 python.exe。");
            }
            log("运行时解压完成，正在离线引导 pip……");
            if (!await RunStepAsync(pyExe, "-m ensurepip --upgrade --default-pip", log, ct).ConfigureAwait(false))
            {
                return new RuntimeResult(false, null, "pip 引导（ensurepip）失败。");
            }
            log("pip 引导完成。");
        }
        else
        {
            log($"复用已下载的托管运行时：{pyExe}");
        }

        var specs = pipSpecs.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (specs.Count > 0)
        {
            log($"安装打包引擎：{string.Join(", ", specs)}（镜像：{PipIndexUrl}）");
            var args = "-m pip install --disable-pip-version-check --no-warn-script-location -i "
                       + ProcessService.Quote(PipIndexUrl) + " "
                       + string.Join(" ", specs.Select(ProcessService.Quote));
            if (!await RunStepAsync(pyExe, args, log, ct).ConfigureAwait(false))
            {
                return new RuntimeResult(false, null, "打包引擎安装失败，请检查网络或更换 pip 镜像。");
            }
            progress?.Report(95);
        }

        progress?.Report(100);
        log($"运行时就绪：{pyExe}");
        return new RuntimeResult(true, pyExe, null);
    }

    private async Task<bool> DownloadAndExtractAsync(
        string version, CpuArch arch, Action<string> log, IProgress<int>? progress, CancellationToken ct)
    {
        var pkgId = TargetCatalog.NuGetPackageId(arch);
        var url = $"{NuGetBase}/{pkgId}/{version}";
        var destDir = RuntimeDir(version, arch);

        AppPaths.EnsureRoot();
        Directory.CreateDirectory(AppPaths.RuntimesDir);

        var tmp = Path.Combine(AppPaths.RuntimesDir, $"_dl_{pkgId}_{version}.zip");
        log($"下载：{url}");

        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                log($"[错误] 下载失败，HTTP {(int)resp.StatusCode}。");
                return false;
            }

            var total = resp.Content.Headers.ContentLength ?? -1L;
            long done = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                var buffer = new byte[128 * 1024];
                var lastPct = -1;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                    {
                        var pct = (int)(done * 100 / total);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            // 下载占总进度的 0-60%
                            progress?.Report(pct * 60 / 100);
                        }
                    }
                }
            }

            log($"下载完成（{done / 1024 / 1024.0:0.0} MB），正在解压……");
            progress?.Report(62);

            if (Directory.Exists(destDir))
            {
                try { Directory.Delete(destDir, recursive: true); } catch { /* 稍后覆盖 */ }
            }
            Directory.CreateDirectory(destDir);
            ZipFile.ExtractToDirectory(tmp, destDir, overwriteFiles: true);
            progress?.Report(68);
            log("解压完成。");
            return true;
        }
        catch (OperationCanceledException)
        {
            log("下载已取消。");
            return false;
        }
        catch (Exception ex)
        {
            log($"[错误] 下载/解压异常：{ex.Message}");
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略清理失败 */ }
        }
    }

    /// <summary>运行一条 python 子命令，逐行回调日志，返回是否成功（退出码 0）。</summary>
    private static async Task<bool> RunStepAsync(
        string pyExe, string args, Action<string> log, CancellationToken ct)
    {
        // 把子步骤输出原样透传到日志（缩进以区别主流程）
        var exit = await ProcessService.RunAsync(pyExe, args, Path.GetDirectoryName(pyExe), line =>
        {
            if (!string.IsNullOrWhiteSpace(line)) log("  " + line.TrimEnd());
        }, ct).ConfigureAwait(false);
        return exit == 0;
    }
}
