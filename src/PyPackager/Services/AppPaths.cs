using System.IO;

namespace PyPackager.Services;

/// <summary>
/// 应用数据路径中心。所有持久化内容（内部虚拟环境、历史记录）都放在
/// %LOCALAPPDATA%\PyPackager 下，避免写入 Program Files 的权限问题，也不污染用户项目。
/// </summary>
public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyPackager");

    public static string VenvDir => Path.Combine(Root, "venv");

    /// <summary>内部虚拟环境的 python.exe（Windows 下位于 Scripts 子目录）。</summary>
    public static string VenvPython => Path.Combine(VenvDir, "Scripts", "python.exe");

    /// <summary>托管 Python 运行时的根目录：每个 (版本×架构) 一个子目录，用于多目标打包。</summary>
    public static string RuntimesDir => Path.Combine(Root, "runtimes");

    /// <summary>运行时下载/配置的元数据文件（镜像源等）。</summary>
    public static string RuntimeConfigFile => Path.Combine(Root, "runtimes.json");

    /// <summary>APK 打包相关配置（云端服务器地址等）。</summary>
    public static string ApkConfigFile => Path.Combine(Root, "apk.json");

    /// <summary>远程服务器 SSH 连接配置（凭据经 DPAPI 加密）。</summary>
    public static string SshConfigFile => Path.Combine(Root, "ssh.json");

    /// <summary>最近一次服务器部署成功记录（服务地址/主机/系统/时间），供下次启动回显。</summary>
    public static string LastDeployFile => Path.Combine(Root, "last_deploy.json");

    public static string HistoryFile => Path.Combine(Root, "history.json");

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
    }
}
