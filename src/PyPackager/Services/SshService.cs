using System.IO;
using System.Text;
using PyPackager.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PyPackager.Services;

/// <summary>一次远程命令执行的结果。</summary>
public sealed record SshResult(int ExitCode, string Output, string? Error = null)
{
    public bool Success => ExitCode == 0 && Error is null;
}

/// <summary>
/// SSH 远程执行封装（基于 SSH.NET）。持有一条长连接，供 ServerProvisionService 连续执行
/// 探测/安装/构建等脚本，并把输出逐行回调给界面。
///
/// 脚本执行用 base64 传输：把整段 bash 脚本编码后在远程解码落盘再执行，彻底规避
/// Windows→Linux 的引号/转义问题；退出码用哨兵行回传（因为末尾还有清理命令会覆盖 $?）。
/// </summary>
public sealed class SshService : IDisposable
{
    private const string RcMarker = "__PYPK_RC__=";

    private SshClient? _ssh;
    private SshConfig? _cfg;

    public bool IsConnected => _ssh?.IsConnected == true;
    public SshConfig? Config => _cfg;

    /// <summary>建立连接。失败抛 <see cref="SshException"/> 或 <see cref="SshConnectionException"/>。</summary>
    public void Connect(SshConfig cfg)
    {
        Disconnect();
        _cfg = cfg;
        _ssh = BuildClient(cfg);
        _ssh.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
        _ssh.Connect();
    }

    private static SshClient BuildClient(SshConfig cfg)
    {
        if (cfg.AuthType == SshAuthType.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(cfg.PrivateKeyPath) || !File.Exists(cfg.PrivateKeyPath))
                throw new SshException($"私钥文件不存在：{cfg.PrivateKeyPath}");
            var key = string.IsNullOrEmpty(cfg.PrivateKeyPassphrase)
                ? new PrivateKeyFile(cfg.PrivateKeyPath)
                : new PrivateKeyFile(cfg.PrivateKeyPath, cfg.PrivateKeyPassphrase);
            return new SshClient(cfg.Host, cfg.Port, cfg.Username, key);
        }

        if (string.IsNullOrEmpty(cfg.Password))
            throw new SshException("未提供密码。");
        return new SshClient(cfg.Host, cfg.Port, cfg.Username, cfg.Password);
    }

    /// <summary>仅测试能否连通并认证成功，随即断开。返回 (成功, 消息)。</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(SshConfig cfg, CancellationToken ct = default)
    {
        using var probe = new SshService();
        try
        {
            await Task.Run(() => probe.Connect(cfg), ct).ConfigureAwait(false);
            var res = await probe.RunScriptAsync("echo __PYPK_OK__; uname -srmo; (. /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\")",
                _ => { }, ct).ConfigureAwait(false);
            return res.Success && res.Output.Contains("__PYPK_OK__")
                ? (true, "连接成功。远程：" + FirstMeaningfulLine(res.Output))
                : (false, "已连接但执行探测命令失败：" + (res.Error ?? res.Output));
        }
        catch (Exception ex)
        {
            return (false, "连接失败：" + ex.Message);
        }
        finally
        {
            probe.Disconnect();
        }
    }

    private static string FirstMeaningfulLine(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && !line.Contains("__PYPK_OK__")) return line;
        }
        return "(无)";
    }

    /// <summary>
    /// 在远程执行一段 bash 脚本，逐行回调输出，返回退出码（经哨兵行解析）。
    /// 所有输出（含 stderr）统一 2&gt;&amp;1 合流，便于界面完整展示。
    /// </summary>
    public async Task<SshResult> RunScriptAsync(
        string script, Action<string> onLine, CancellationToken ct, int idleTimeoutSec = 3600)
    {
        if (_ssh is null || !_ssh.IsConnected)
            return new SshResult(-1, "", "SSH 未连接。");

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var tmp = $"/tmp/pypk_run_{Guid.NewGuid():N}.sh";
        // 落盘脚本 → 执行（合流 stderr）→ 用哨兵回传真实退出码 → 清理
        var remote =
            $"echo {b64} | base64 --decode > {tmp} && " +
            $"bash {tmp} 2>&1; echo {RcMarker}$?; rm -f {tmp}";

        return await ExecuteAsync(remote, onLine, ct, idleTimeoutSec).ConfigureAwait(false);
    }

    /// <summary>执行单条远程命令（不经脚本落盘），逐行回调，返回退出码。</summary>
    public Task<SshResult> RunCommandAsync(
        string command, Action<string> onLine, CancellationToken ct, int idleTimeoutSec = 3600)
    {
        if (_ssh is null || !_ssh.IsConnected)
            return Task.FromResult(new SshResult(-1, "", "SSH 未连接。"));
        return ExecuteAsync($"{command} 2>&1; echo {RcMarker}$?", onLine, ct, idleTimeoutSec);
    }

    private async Task<SshResult> ExecuteAsync(
        string remoteCommand, Action<string> onLine, CancellationToken ct, int idleTimeoutSec)
    {
        var output = new StringBuilder();
        var exitCode = -1;
        var sawRc = false;

        try
        {
            await Task.Run(() =>
            {
                var cmd = _ssh!.CreateCommand(remoteCommand);
                var async = cmd.BeginExecute();
                using var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8);
                var idle = DateTime.UtcNow;
                string? line;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // 无阻塞探测：有数据才读，兼顾取消与空闲超时
                    if (reader.EndOfStream)
                    {
                        if (async.IsCompleted) break;
                        if ((DateTime.UtcNow - idle).TotalSeconds > idleTimeoutSec)
                            throw new SshException($"远程命令空闲超时（>{idleTimeoutSec}s）。");
                        Thread.Sleep(150);
                        continue;
                    }
                    line = reader.ReadLine();
                    if (line is null) { if (async.IsCompleted) break; continue; }
                    idle = DateTime.UtcNow;

                    if (line.StartsWith(RcMarker, StringComparison.Ordinal))
                    {
                        var rc = line.Substring(RcMarker.Length).Trim();
                        int.TryParse(rc, out exitCode);
                        sawRc = true;
                        continue; // 哨兵行不展示给用户
                    }
                    output.AppendLine(line);
                    onLine(line);
                }
                cmd.EndExecute(async);
                if (!sawRc) exitCode = cmd.ExitStatus.GetValueOrDefault(-1);
            }, ct).ConfigureAwait(false);

            return new SshResult(exitCode, output.ToString());
        }
        catch (OperationCanceledException)
        {
            return new SshResult(-1, output.ToString(), "已取消。");
        }
        catch (Exception ex)
        {
            return new SshResult(exitCode, output.ToString(), ex.Message);
        }
    }

    /// <summary>通过 SFTP 上传若干本地文件到远程目录（自动建目录）。返回上传成功的文件数。</summary>
    public async Task<int> UploadFilesAsync(
        string remoteDir, IEnumerable<(string LocalPath, string FileName)> files,
        Action<string> onLine, CancellationToken ct)
    {
        if (_cfg is null) throw new SshException("未连接，无法上传。");
        return await Task.Run(() =>
        {
            using var sftp = BuildSftp(_cfg);
            sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
            sftp.Connect();
            EnsureRemoteDir(sftp, remoteDir);
            var n = 0;
            foreach (var (local, name) in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(local)) { onLine($"[跳过] 本地文件不存在：{local}"); continue; }
                var remotePath = remoteDir.TrimEnd('/') + "/" + name;
                using var fs = File.OpenRead(local);
                sftp.UploadFile(fs, remotePath, true);
                onLine($"[上传] {name} → {remotePath}");
                n++;
            }
            return n;
        }, ct).ConfigureAwait(false);
    }

    private static SftpClient BuildSftp(SshConfig cfg) =>
        cfg.AuthType == SshAuthType.PrivateKey
            ? (string.IsNullOrEmpty(cfg.PrivateKeyPassphrase)
                ? new SftpClient(cfg.Host, cfg.Port, cfg.Username, new PrivateKeyFile(cfg.PrivateKeyPath))
                : new SftpClient(cfg.Host, cfg.Port, cfg.Username, new PrivateKeyFile(cfg.PrivateKeyPath, cfg.PrivateKeyPassphrase)))
            : new SftpClient(cfg.Host, cfg.Port, cfg.Username, cfg.Password);

    private static void EnsureRemoteDir(SftpClient sftp, string dir)
    {
        dir = dir.TrimEnd('/');
        if (dir.Length == 0 || sftp.Exists(dir)) return;
        // 逐级创建
        var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cur = "";
        foreach (var p in parts)
        {
            cur += "/" + p;
            if (!sftp.Exists(cur)) sftp.CreateDirectory(cur);
        }
    }

    public void Disconnect()
    {
        try
        {
            if (_ssh?.IsConnected == true) _ssh.Disconnect();
        }
        catch { /* 忽略断开异常 */ }
        finally
        {
            _ssh?.Dispose();
            _ssh = null;
        }
    }

    public void Dispose() => Disconnect();
}
