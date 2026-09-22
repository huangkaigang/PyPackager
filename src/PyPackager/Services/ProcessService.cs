using System.Diagnostics;
using System.IO;
using System.Text;

namespace PyPackager.Services;

/// <summary>
/// 对 System.Diagnostics.Process 的轻量封装：在后台运行命令，
/// 通过 async/await 实时把 stdout/stderr 逐行回调出来，绝不阻塞 UI 线程。
/// </summary>
public static class ProcessService
{
    /// <summary>
    /// 运行一个进程并把输出逐行交给 <paramref name="onLine"/>。
    /// </summary>
    /// <param name="fileName">可执行文件（如 python）</param>
    /// <param name="arguments">参数字符串（调用方负责转义）</param>
    /// <param name="workingDir">工作目录</param>
    /// <param name="onLine">每产生一行输出时的回调（可能在后台线程触发）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="extraEnv">附加的环境变量（如让 wsl.exe 输出 UTF-8 的 WSL_UTF8=1）</param>
    /// <returns>进程退出码</returns>
    public static async Task<int> RunAsync(
        string fileName,
        string arguments,
        string? workingDir,
        Action<string> onLine,
        CancellationToken cancellationToken = default,
        IDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 让子进程内的 Python 也用 UTF-8 输出，双保险防中文乱码
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PYTHONUTF8"] = "1";

        if (extraEnv is not null)
        {
            foreach (var kv in extraEnv)
            {
                psi.EnvironmentVariables[kv.Key] = kv.Value;
            }
        }

        using var process = new Process { StartInfo = psi };

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                onLine(line);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                onLine(line);
            }
        };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            // 等待异步输出缓冲全部 flush 后再取退出码
            try
            {
                process.WaitForExit();
            }
            catch
            {
                // ignored
            }
            tcs.TrySetResult(process.ExitCode);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            onLine($"[ERROR] 无法启动进程 \"{fileName}\"：{ex.Message}");
            return -1;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                tcs.TrySetCanceled(cancellationToken);
            }
            catch
            {
                // ignored
            }
        }))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    /// <summary>把一个参数安全地包进引号，供拼接命令行使用。</summary>
    public static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }
        if (!value.Contains(' ') && !value.Contains('\t') && !value.Contains('"'))
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// 以管理员权限（触发 UAC）运行一段 PowerShell 脚本，等待其结束并返回退出码。
    /// 由于提权进程无法重定向标准流，脚本的全部输出会被写入 <paramref name="logFile"/>，
    /// 调用方在方法返回后自行读取该文件。用户若在 UAC 弹窗点了“否”，返回 -1。
    /// </summary>
    public static async Task<int> RunElevatedAsync(
        string powershellScript, string logFile, CancellationToken cancellationToken = default)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), $"pypk_elev_{Guid.NewGuid():N}.ps1");
        var escapedLog = logFile.Replace("'", "''");
        // 包装脚本：把内部脚本的所有流（含 Write-Host/错误）统一写入日志文件，调用方随后读取解析。
        var wrapper =
            "$ErrorActionPreference='Continue'\n" +
            "& {\n" +
            powershellScript + "\n" +
            "} *>&1 | Out-File -FilePath '" + escapedLog + "' -Encoding utf8\n";

        try
        {
            await File.WriteAllTextAsync(scriptFile, wrapper, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {Quote(scriptFile)}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                // 用户拒绝 UAC 会抛 Win32Exception (The operation was canceled by the user)
                await File.AppendAllTextAsync(logFile, "[ERROR] 提权启动失败（可能已取消 UAC）：" + ex.Message + Environment.NewLine,
                    cancellationToken).ConfigureAwait(false);
                return -1;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            try { if (File.Exists(scriptFile)) File.Delete(scriptFile); } catch { /* 忽略清理失败 */ }
        }
    }
}
