using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PyPackager.Services;

namespace PyPackager.Models;

/// <summary>SSH 认证方式。</summary>
public enum SshAuthType
{
    /// <summary>密码登录。</summary>
    Password,
    /// <summary>私钥文件登录（可选口令）。</summary>
    PrivateKey,
}

/// <summary>
/// 远程服务器 SSH 连接配置。密码/私钥口令在落盘时用 DPAPI（当前用户作用域）加密，
/// 明文只在内存中存在，避免 %LOCALAPPDATA% 下的配置文件泄露凭据。
/// </summary>
public sealed class SshConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    public SshAuthType AuthType { get; set; } = SshAuthType.Password;

    /// <summary>明文密码（仅内存）。落盘走 <see cref="Save"/> 加密。</summary>
    public string Password { get; set; } = string.Empty;

    public string PrivateKeyPath { get; set; } = string.Empty;
    /// <summary>私钥口令（明文，仅内存）。</summary>
    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    /// <summary>部署后 APK 编译服务对外端口。</summary>
    public int ServicePort { get; set; } = 8000;
    /// <summary>服务端文件在远程的部署目录。</summary>
    public string RemoteDir { get; set; } = "/opt/pypackager-apk";

    /// <summary>部署成功后，客户端「云端服务器地址」即 http://Host:ServicePort。</summary>
    public string CloudUrl => $"http://{Host}:{ServicePort}";

    public bool HasTarget => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Username);

    // ---------- 持久化（DPAPI 加密敏感字段）----------

    private sealed class Dto
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string Username { get; set; } = "root";
        public int AuthType { get; set; }
        public string? PasswordEnc { get; set; }
        public string PrivateKeyPath { get; set; } = "";
        public string? PassphraseEnc { get; set; }
        public int ServicePort { get; set; } = 8000;
        public string RemoteDir { get; set; } = "/opt/pypackager-apk";
    }

    public void Save()
    {
        AppPaths.EnsureRoot();
        var dto = new Dto
        {
            Host = Host,
            Port = Port,
            Username = Username,
            AuthType = (int)AuthType,
            PasswordEnc = Protect(Password),
            PrivateKeyPath = PrivateKeyPath,
            PassphraseEnc = Protect(PrivateKeyPassphrase),
            ServicePort = ServicePort,
            RemoteDir = RemoteDir,
        };
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.SshConfigFile, json, new UTF8Encoding(false));
    }

    public static SshConfig Load()
    {
        var cfg = new SshConfig();
        try
        {
            if (!File.Exists(AppPaths.SshConfigFile)) return cfg;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(AppPaths.SshConfigFile));
            if (dto is null) return cfg;
            cfg.Host = dto.Host;
            cfg.Port = dto.Port <= 0 ? 22 : dto.Port;
            cfg.Username = string.IsNullOrWhiteSpace(dto.Username) ? "root" : dto.Username;
            cfg.AuthType = (SshAuthType)dto.AuthType;
            cfg.Password = Unprotect(dto.PasswordEnc);
            cfg.PrivateKeyPath = dto.PrivateKeyPath;
            cfg.PrivateKeyPassphrase = Unprotect(dto.PassphraseEnc);
            cfg.ServicePort = dto.ServicePort <= 0 ? 8000 : dto.ServicePort;
            cfg.RemoteDir = string.IsNullOrWhiteSpace(dto.RemoteDir) ? "/opt/pypackager-apk" : dto.RemoteDir;
        }
        catch { /* 配置损坏则用默认值 */ }
        return cfg;
    }

    private static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        catch { return null; }
    }

    private static string Unprotect(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return string.Empty;
        try
        {
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return string.Empty; }
    }
}
