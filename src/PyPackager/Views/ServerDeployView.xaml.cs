using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PyPackager.Models;
using PyPackager.Services;

namespace PyPackager.Views;

public partial class ServerDeployView : UserControl
{
    private CancellationTokenSource? _cts;
    private ServerOsInfo? _os;

    public ServerDeployView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadConfigToUi();
            PopulateSupportedList();
        };
    }

    /// <summary>渲染「支持的服务器系统」列表（含 32/64 位），连接前即可见。</summary>
    private void PopulateSupportedList()
    {
        SupportedList.Items.Clear();
        foreach (var line in SupportedServerCatalog.ToDisplayLines())
        {
            var isHeader = line.StartsWith("◆");
            SupportedList.Items.Add(new TextBlock
            {
                Text = line,
                FontSize = isHeader ? 12.5 : 11.5,
                FontWeight = isHeader ? FontWeights.Bold : FontWeights.Normal,
                Foreground = (Brush)FindResource(isHeader ? "TextBrush" : "SubtextBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(isHeader ? 0 : 0, isHeader ? 6 : 0, 0, 0),
            });
        }
    }

    // ---------- UI 辅助 ----------

    private void AppendLog(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private void SetProgress(int v, string? text = null) => Dispatcher.Invoke(() =>
    {
        Progress.Value = Math.Clamp(v, 0, 100);
        ProgressText.Text = text ?? (v > 0 ? $"{v}%" : string.Empty);
    });

    /// <summary>把成功/失败/异常结果常驻显示在页面横幅上（不依赖弹窗，弹窗关闭后仍可见）。</summary>
    private void SetResult(bool ok, string text) => Dispatcher.Invoke(() =>
    {
        ResultBanner.Visibility = Visibility.Visible;
        ResultBanner.Background = (Brush)FindResource(ok ? "GreenBrush" : "RedBrush");
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
        ResultText.FontWeight = FontWeights.Bold;
        ResultText.Text = (ok ? "✔ 成功：" : "✘ 失败：") + text;
    });

    private void SetBusy(bool busy) => Dispatcher.Invoke(() =>
    {
        TestBtn.IsEnabled = !busy;
        DeployBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = busy;
        HostBox.IsEnabled = !busy;
        PortBox.IsEnabled = !busy;
        UserBox.IsEnabled = !busy;
        ServicePortBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        AuthPassword.IsEnabled = !busy;
        AuthKey.IsEnabled = !busy;
    });

    private void Auth_Changed(object sender, RoutedEventArgs e)
    {
        if (PasswordPanel is null || KeyPanel is null) return;
        var useKey = AuthKey.IsChecked == true;
        PasswordPanel.Visibility = useKey ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = useKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadConfigToUi()
    {
        var cfg = SshConfig.Load();
        HostBox.Text = cfg.Host;
        PortBox.Text = cfg.Port.ToString();
        UserBox.Text = cfg.Username;
        ServicePortBox.Text = cfg.ServicePort.ToString();
        PasswordBox.Password = cfg.Password;
        KeyPathBox.Text = cfg.PrivateKeyPath;
        PassphraseBox.Password = cfg.PrivateKeyPassphrase;
        AuthKey.IsChecked = cfg.AuthType == SshAuthType.PrivateKey;
        AuthPassword.IsChecked = cfg.AuthType == SshAuthType.Password;
        Auth_Changed(this, new RoutedEventArgs());
    }

    private SshConfig? CollectConfig()
    {
        var host = HostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            AppServices.RaiseStatus("请填写服务器主机 / IP。");
            return null;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is <= 0 or > 65535) port = 22;
        if (!int.TryParse(ServicePortBox.Text.Trim(), out var svcPort) || svcPort is <= 0 or > 65535) svcPort = 8000;

        return new SshConfig
        {
            Host = host,
            Port = port,
            Username = string.IsNullOrWhiteSpace(UserBox.Text) ? "root" : UserBox.Text.Trim(),
            AuthType = AuthKey.IsChecked == true ? SshAuthType.PrivateKey : SshAuthType.Password,
            Password = PasswordBox.Password,
            PrivateKeyPath = KeyPathBox.Text.Trim(),
            PrivateKeyPassphrase = PassphraseBox.Password,
            ServicePort = svcPort,
            RemoteDir = "/opt/pypackager-apk",
        };
    }

    private void RenderOs(ServerOsInfo os)
    {
        _os = os;
        var docker = os.DockerInstalled ? "已安装" : "未安装（将自动安装）";
        var compose = os.ComposeAvailable ? "可用" : "将随 Docker 安装";
        var subnets = os.ExistingSubnets.Count == 0 ? "(无)" : string.Join(", ", os.ExistingSubnets);
        var verdict = os.CompileImageSupported
            ? "✔ 完整支持：可装 Docker 并编译 APK"
            : os.DockerArchSupported
                ? "⚠ 仅可安装/管理 Docker：32 位系统无法编译 APK（编译镜像仅 64 位）"
                : "✘ 不支持：Docker 官方无该架构构建";
        Dispatcher.Invoke(() =>
        {
            OsSummary.Text =
                $"系统：{os.PrettyName}  ({os.Id} {os.VersionId}, {os.Arch} / {os.BitnessLabel}, 内核 {os.Kernel})\n" +
                $"发行版家族：{os.Family}   Docker：{docker}   Compose：{compose}\n" +
                $"支持判定：{verdict}\n" +
                $"现有网段：{subnets}   → 将为其选择不冲突的 Docker 地址池";
        });
    }

    /// <summary>部署成功后把结果保存到本地（%LOCALAPPDATA%\PyPackager\last_deploy.json），下次启动可回显。</summary>
    private static void SaveDeployRecord(SshConfig cfg, ServerOsInfo os, string url)
    {
        try
        {
            AppPaths.EnsureRoot();
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                serverUrl = url,
                host = cfg.Host,
                port = cfg.Port,
                username = cfg.Username,
                servicePort = cfg.ServicePort,
                os = os.PrettyName,
                arch = os.Arch,
                bitness = os.Bitness,
                deployedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.LastDeployFile, json, new System.Text.UTF8Encoding(false));
        }
        catch { /* 记录失败不影响部署结果 */ }
    }

    // ---------- 操作 ----------

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 SSH 私钥文件",
            Filter = "私钥/所有文件|*|PEM|*.pem|密钥|id_*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
            KeyPathBox.Text = dlg.FileName;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        if (cfg is null) return;
        if (cfg.SaveIfRequested(SaveCredCheck.IsChecked == true)) { /* 保存成功 */ }

        SetBusy(true);
        AppendLog($"测试连接 {cfg.Username}@{cfg.Host}:{cfg.Port} ……");
        AppServices.RaiseStatus("正在测试 SSH 连接……");
        var (ok, msg) = await SshService.TestAsync(cfg);
        AppendLog(ok ? "[成功] " + msg : "[失败] " + msg);
        SetResult(ok, msg);
        AppServices.RaiseStatus(ok ? "SSH 连接正常。" : "SSH 连接失败。");
        SetBusy(false);

        if (!ok)
        {
            MessageBox.Show(msg, "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 连接成功后顺带探测系统，供部署前预览
        using var probe = new SshService();
        try
        {
            probe.Connect(cfg);
            var os = await AppServices.ServerProvision.DetectOsAsync(probe, AppendLog, CancellationToken.None);
            RenderOs(os);
        }
        catch (Exception ex)
        {
            AppendLog("[警告] 系统探测失败：" + ex.Message);
        }
        finally
        {
            probe.Disconnect();
        }
    }

    private async void Deploy_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        if (cfg is null) return;

        var confirm = MessageBox.Show(
            $"即将在服务器 {cfg.Username}@{cfg.Host} 上自动执行：\n\n" +
            "1. 安装 Docker Engine（按系统版本选 apt/yum/zypper 源）\n" +
            "2. 写入 /etc/docker/daemon.json 并重启 Docker（含网段冲突避让）\n" +
            "3. 上传并构建 APK 编译服务镜像（下载 Android SDK/NDK，约 5~15 分钟）\n" +
            $"4. 在端口 {cfg.ServicePort} 启动服务（无鉴权，仅建议内网/受控网络）\n\n" +
            "这会修改服务器系统配置并重启 Docker。确认继续？",
            "确认自动部署", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        cfg.SaveIfRequested(SaveCredCheck.IsChecked == true);

        LogBox.Clear();
        Dispatcher.Invoke(() => ResultBanner.Visibility = Visibility.Collapsed);
        SetBusy(true);
        SetProgress(2, "连接中…");
        _cts = new CancellationTokenSource();
        AppServices.RaiseStatus("正在自动部署 APK 编译服务……");

        using var ssh = new SshService();
        try
        {
            AppendLog($"连接 {cfg.Username}@{cfg.Host}:{cfg.Port} ……");
            await Task.Run(() => ssh.Connect(cfg), _cts.Token);
            AppendLog("[成功] SSH 已连接。");

            AppendLog("探测服务器系统与环境……");
            var os = await AppServices.ServerProvision.DetectOsAsync(ssh, AppendLog, _cts.Token);
            RenderOs(os);
            SetProgress(8);

            var result = await AppServices.ServerProvision.ProvisionAsync(
                ssh, cfg, os, AppendLog, new Progress<int>(v => SetProgress(v)), _cts.Token);

            if (result.Success && result.ServerUrl is not null)
            {
                SetProgress(100, "完成 ✓");
                AppServices.ApkCloud.SetServer(result.ServerUrl);
                SaveDeployRecord(cfg, os, result.ServerUrl);
                SetResult(true, $"APK 编译服务已就绪：{result.ServerUrl}（地址已保存到本地并回填到「APK 打包」页）");
                AppServices.RaiseStatus($"部署成功：{result.ServerUrl}（已回填到 APK 页云端后端）");
                AppendLog($"[成功] 服务地址 {result.ServerUrl} 已自动填入「APK 打包」页。");
                MessageBox.Show(
                    $"部署成功！\n\nAPK 编译服务地址：\n{result.ServerUrl}\n\n" +
                    "已自动回填到「APK 打包」页的云端服务器地址，去那里选「云端服务器打包」即可生成 APK。",
                    "部署完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                SetProgress(0, "失败 ✗");
                AppendLog($"[失败] 阶段「{result.Stage}」：{result.Error}");
                SetResult(false, $"阶段「{result.Stage}」：{result.Error}（完整日志见下方「部署日志」）");
                AppServices.RaiseStatus($"部署失败（{result.Stage}）：{result.Error}");
                MessageBox.Show($"部署在「{result.Stage}」阶段失败：\n\n{result.Error}",
                    "部署失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("[取消] 已中止部署。");
            AppServices.RaiseStatus("部署已取消。");
        }
        catch (Exception ex)
        {
            SetProgress(0, "失败 ✗");
            AppendLog("[错误] " + ex.Message);
            SetResult(false, "部署异常：" + ex.Message + "（完整日志见下方「部署日志」）");
            AppServices.RaiseStatus("部署异常：" + ex.Message);
            MessageBox.Show("部署异常：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ssh.Disconnect();
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AppendLog("[请求] 正在取消……");
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        if (cfg is null) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = cfg.CloudUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppServices.RaiseStatus("打开浏览器失败：" + ex.Message);
        }
    }
}

/// <summary>SshConfig 的界面辅助扩展：按需保存凭据。</summary>
internal static class SshConfigUiExtensions
{
    public static bool SaveIfRequested(this SshConfig cfg, bool save)
    {
        if (!save) return false;
        try { cfg.Save(); return true; }
        catch { return false; }
    }
}
