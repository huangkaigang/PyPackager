using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.Win32;

namespace PyPackager.Services;

/// <summary>
/// 打成安装包：把 EXE 输出目录用 Inno Setup 编译成单个 setup 安装程序。
/// 自动探测本机 Inno Setup（PATH / 常见目录 / 注册表），缺件时可一键下载静默安装。
/// </summary>
public sealed record InstallerOptions(
    string AppName,        // 安装包里显示的应用名
    string Version,        // 安装包版本号，如 1.0.0
    string Publisher,      // 发布者
    string SourceDir,      // 要打进安装包的目录（EXE 输出目录）
    string MainExe,        // 主程序文件名（相对 SourceDir）
    string? IconPath,      // .ico 安装包图标，可选
    string OutputDir,      // 安装包输出目录
    bool DesktopIcon);     // 安装时可选创建桌面快捷方式

public sealed record InstallerResult(bool Success, string? InstallerPath, string? Error);

public static class InstallerService
{
    /// <summary>Inno Setup 官方下载地址（重定向到最新稳定版安装器）。</summary>
    public const string DownloadUrl = "https://jrsoftware.org/download.php/is.exe";

    private static readonly string[] CommonPaths =
    {
        @"C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        @"C:\Program Files\Inno Setup 6\ISCC.exe",
        @"C:\Program Files (x86)\Inno Setup 5\ISCC.exe",
        @"C:\Program Files\Inno Setup 5\ISCC.exe",
    };

    /// <summary>探测 ISCC.exe 完整路径；未安装返回 null。</summary>
    public static string? FindIscc()
    {
        // 1) PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var cand = Path.Combine(dir.Trim(), "ISCC.exe");
                if (File.Exists(cand)) return cand;
            }
            catch { /* 忽略非法 PATH 条目 */ }
        }

        // 2) 常见安装目录
        foreach (var p in CommonPaths)
        {
            if (File.Exists(p)) return p;
        }

        // 3) 注册表 Uninstall 键（Inno Setup 6/5，含 WOW6432 与 HKCU）
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var wow in new[] { "", @"\Wow6432Node" })
            {
                foreach (var ver in new[] { " 6_is1", " 5_is1", "_is1" })
                {
                    try
                    {
                        using var key = root.OpenSubKey(
                            $@"SOFTWARE{wow}\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup{ver}");
                        if (key?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                        {
                            var cand = Path.Combine(loc.Trim(), "ISCC.exe");
                            if (File.Exists(cand)) return cand;
                        }
                    }
                    catch { /* 忽略不可读键 */ }
                }
            }
        }

        return null;
    }

    /// <summary>生成 Inno Setup 脚本（.iss）内容。</summary>
    public static string GenerateScript(InstallerOptions opt)
    {
        string Esc(string v) => v.Replace("\"", "\"\"");
        var baseName = Sanitize($"{opt.AppName}-Setup-{opt.Version}");
        var sb = new StringBuilder();
        sb.AppendLine("; 由 PyPackager 自动生成的 Inno Setup 脚本");
        sb.AppendLine("[Setup]");
        sb.AppendLine($"AppId={{{Guid.NewGuid():B}}}");
        sb.AppendLine($"AppName=\"{Esc(opt.AppName)}\"");
        sb.AppendLine($"AppVersion=\"{Esc(opt.Version)}\"");
        sb.AppendLine($"AppPublisher=\"{Esc(opt.Publisher)}\"");
        sb.AppendLine($"VersionInfoVersion=\"{Esc(NormalizeVersion(opt.Version))}\"");
        sb.AppendLine($"DefaultDirName={{autopf}}\\{Esc(opt.AppName)}");
        sb.AppendLine($"DefaultGroupName=\"{Esc(opt.AppName)}\"");
        sb.AppendLine($"OutputDir=\"{opt.OutputDir}\"");
        sb.AppendLine($"OutputBaseFilename=\"{baseName}\"");
        if (!string.IsNullOrWhiteSpace(opt.IconPath) && File.Exists(opt.IconPath))
            sb.AppendLine($"SetupIconFile=\"{opt.IconPath}\"");
        sb.AppendLine($"UninstallDisplayIcon=\"{{app}}\\{opt.MainExe}\"");
        sb.AppendLine("Compression=lzma2/max");
        sb.AppendLine("SolidCompression=yes");
        sb.AppendLine("WizardStyle=modern");
        sb.AppendLine("PrivilegesRequired=lowest");
        sb.AppendLine();
        sb.AppendLine("[Tasks]");
        sb.AppendLine("Name: \"desktopicon\"; Description: \"创建桌面快捷方式\"; GroupDescription: \"附加任务：\"; Flags: unchecked");
        sb.AppendLine();
        sb.AppendLine("[Files]");
        sb.AppendLine($"Source: \"{opt.SourceDir}\\*\"; DestDir: \"{{app}}\"; Flags: ignoreversion recursesubdirs createallsubdirs");
        sb.AppendLine();
        sb.AppendLine("[Icons]");
        sb.AppendLine($"Name: \"{{group}}\\{Esc(opt.AppName)}\"; Filename: \"{{app}}\\{opt.MainExe}\"");
        if (opt.DesktopIcon)
            sb.AppendLine($"Name: \"{{autodesktop}}\\{Esc(opt.AppName)}\"; Filename: \"{{app}}\\{opt.MainExe}\"; Tasks: desktopicon");
        sb.AppendLine();
        sb.AppendLine("[Run]");
        sb.AppendLine($"Filename: \"{{app}}\\{opt.MainExe}\"; Description: \"立即运行\"; Flags: nowait postprocess skipifsilent");
        return sb.ToString();
    }

    /// <summary>把 EXE 输出目录编译成安装包；未装 Inno Setup 时返回失败并提示。</summary>
    public static async Task<InstallerResult> BuildAsync(
        InstallerOptions opt, Action<string> log, CancellationToken ct = default)
    {
        var iscc = FindIscc();
        if (iscc is null)
        {
            return new InstallerResult(false, null,
                $"未检测到 Inno Setup（ISCC.exe）。请先点击“下载并安装 Inno Setup”，或自行到 {DownloadUrl} 安装后重试。");
        }

        if (!Directory.Exists(opt.SourceDir))
            return new InstallerResult(false, null, $"输出目录不存在：{opt.SourceDir}");

        Directory.CreateDirectory(opt.OutputDir);
        var issPath = Path.Combine(Path.GetTempPath(), $"pypk_inst_{Guid.NewGuid():N}.iss");
        await File.WriteAllTextAsync(issPath, GenerateScript(opt), new UTF8Encoding(false), ct).ConfigureAwait(false);

        log($"[安装包] 使用 {iscc} 编译 …");
        var code = await ProcessService.RunAsync(iscc, ProcessService.Quote(issPath), opt.SourceDir, log, ct)
            .ConfigureAwait(false);

        try { File.Delete(issPath); } catch { /* 忽略清理失败 */ }

        if (code != 0)
            return new InstallerResult(false, null, $"ISCC 编译失败（退出码 {code}），详见上方日志。");

        var installer = Path.Combine(opt.OutputDir, $"{Sanitize($"{opt.AppName}-Setup-{opt.Version}")}.exe");
        if (!File.Exists(installer))
            return new InstallerResult(false, null, "编译结束但未找到安装包文件，请检查输出目录。");

        log($"[安装包] 完成：{installer}");
        return new InstallerResult(true, installer, null);
    }

    /// <summary>缺件时下载官方安装器并静默安装；已安装直接返回 true。</summary>
    public static async Task<bool> EnsureInstalledAsync(
        Action<string> log, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (FindIscc() is not null) return true;

        log("[安装包] 下载 Inno Setup 官方安装器 …");
        var tmp = Path.Combine(Path.GetTempPath(), $"pypk_innosetup_{Guid.NewGuid():N}.exe");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (progress is not null && total > 0)
                    progress.Report((int)(done * 100 / total));
            }
        }

        log("[安装包] 静默安装 Inno Setup（可能弹出 UAC）…");
        var psi = new ProcessStartInfo
        {
            FileName = tmp,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return false;
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        try { File.Delete(tmp); } catch { /* 忽略清理失败 */ }

        var ok = FindIscc() is not null;
        log(ok ? "[安装包] Inno Setup 安装完成。" : "[安装包] 安装结束但未探测到 ISCC.exe，请手动安装。");
        return ok;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static string NormalizeVersion(string v)
    {
        var parts = v.Split('.').Where(p => int.TryParse(p, out _)).ToArray();
        while (parts.Length < 4) parts = parts.Append("0").ToArray();
        return string.Join(".", parts.Take(4));
    }
}
