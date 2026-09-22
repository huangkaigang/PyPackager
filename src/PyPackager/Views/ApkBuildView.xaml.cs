using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PyPackager.Models;
using PyPackager.Services;

namespace PyPackager.Views;

public partial class ApkBuildView : UserControl
{
    private string? _lastApkPath;
    // 最近日志环形缓冲：编译失败时把失败日志直接铺到页面横幅，便于不翻日志框也能看到
    private readonly List<string> _recentLogs = new();
    private const int RecentLogCap = 80;

    public ApkBuildView()
    {
        InitializeComponent();
        LocalRadio.Checked += (_, _) => UpdateBackendHint();
        CloudRadio.Checked += (_, _) => UpdateBackendHint();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ServerBox.Text = AppServices.ApkCloud.ServerBaseUrl;
        UpdateBackendHint();
        // 本地优先：进页面即静默体检，若本机已就绪则默认选本地。
        await DetectLocalAsync(autoSelectBackend: true);
    }

    private void AppendLog(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        _recentLogs.Add(line);
        if (_recentLogs.Count > RecentLogCap) _recentLogs.RemoveAt(0);
    });

    /// <summary>把成功/失败/异常结果常驻显示在页面横幅；失败时附带最近编译日志。</summary>
    private void SetResult(bool ok, string headline, bool includeRecentLog = false) => Dispatcher.Invoke(() =>
    {
        ResultBanner.Visibility = Visibility.Visible;
        ResultBanner.Background = (Brush)FindResource(ok ? "GreenBrush" : "RedBrush");
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
        var sb = new StringBuilder();
        sb.Append(ok ? "✔ 成功：" : "✘ 失败：").Append(headline);
        if (!ok && includeRecentLog && _recentLogs.Count > 0)
        {
            sb.Append("\n──── 最近编译日志 ────\n");
            sb.Append(string.Join("\n", _recentLogs.TakeLast(30)));
        }
        ResultText.Text = sb.ToString();
        ResultText.ScrollToEnd();
    });

    /// <summary>开始新一轮打包前清空结果横幅与日志缓冲。</summary>
    private void ResetResult() => Dispatcher.Invoke(() =>
    {
        _recentLogs.Clear();
        ResultBanner.Visibility = Visibility.Collapsed;
        ResultText.Text = string.Empty;
    });

    private void SetBusy(bool busy) => Dispatcher.Invoke(() =>
    {
        DetectLocalBtn.IsEnabled = !busy;
        OneClickBtn.IsEnabled = !busy;
        GenSpecBtn.IsEnabled = !busy;
        BuildApkBtn.IsEnabled = !busy;
        TestConnBtn.IsEnabled = !busy;
        PreflightBtn.IsEnabled = !busy;
        SetupProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) SetupProgress.Value = 0;
    });

    private void UpdateBackendHint()
    {
        if (BackendHint is null) return;
        BackendHint.Text = LocalRadio.IsChecked == true
            ? "本地：在你电脑的 WSL2(Ubuntu) 内执行 buildozer android debug。首次使用需点“一键开启本地打包环境”自动装好 JDK/SDK/buildozer（可能需要重启）。"
            : "云端：把项目打包上传到你的 Linux 服务器，用 buildozer 编译后返回 APK 下载链接。用户体验最好，无需本地配环境。";
    }

    // ---------- 本地环境体检 ----------

    private async void DetectLocal_Click(object sender, RoutedEventArgs e) =>
        await DetectLocalAsync(autoSelectBackend: false);

    private async Task DetectLocalAsync(bool autoSelectBackend)
    {
        SetBusy(true);
        Dispatcher.Invoke(() =>
        {
            VerdictText.Text = "正在体检本地环境……";
            VerdictText.Foreground = (Brush)FindResource("MutedBrush");
        });
        AppServices.RaiseStatus("正在体检本地 APK 打包环境……");

        var report = await AppServices.ApkDoctor.DetectAsync(AppendLog);
        AppServices.LastApkReport = report;
        RenderReport(report);

        // 本地为主：仅当本机根本无法本地编译（固件阻塞 / 系统不支持）时，才默认切到云端兜底。
        if (autoSelectBackend)
        {
            if (report.Verdict is ApkLocalVerdict.Blocked or ApkLocalVerdict.Unavailable)
                CloudRadio.IsChecked = true;
            else
                LocalRadio.IsChecked = true;
        }

        SetBusy(false);
        AppServices.RaiseStatus("本地环境体检完成：" + report.Headline);
    }

    private void RenderReport(ApkEnvReport r)
    {
        Dispatcher.Invoke(() =>
        {
            VerdictText.Text = VerdictLabel(r.Verdict) + " — " + r.Headline;
            VerdictText.Foreground = VerdictBrush(r.Verdict);

            ProbePanel.Children.Clear();
            foreach (var p in r.Probes) AddProbeRow(p);
            ProbeCard.Visibility = Visibility.Visible;

            GuidanceText.Text = r.Guidance;
            GuidanceText.Visibility = string.IsNullOrWhiteSpace(r.Guidance) ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private static string VerdictLabel(ApkLocalVerdict v) => v switch
    {
        ApkLocalVerdict.Ready => "✅ 已就绪",
        ApkLocalVerdict.Fixable => "🛠 可一键修复",
        ApkLocalVerdict.Blocked => "⛔ 需进 BIOS",
        _ => "❌ 不可用",
    };

    private Brush VerdictBrush(ApkLocalVerdict v) => (Brush)FindResource(v switch
    {
        ApkLocalVerdict.Ready => "GreenBrush",
        ApkLocalVerdict.Fixable => "YellowBrush",
        _ => "RedBrush",
    });

    private void AddProbeRow(ApkProbe p)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Fill = new SolidColorBrush(p.State switch
            {
                ApkProbeState.Ok => Color.FromRgb(0xA6, 0xE3, 0xA1),
                ApkProbeState.Warning => Color.FromRgb(0xF9, 0xE2, 0xAF),
                _ => Color.FromRgb(0xF3, 0x8B, 0xA8),
            }),
        };
        var name = new TextBlock
        {
            Text = p.Name + "：",
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 190,
        };
        var val = new TextBlock
        {
            Text = p.Detail,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        row.Children.Add(dot);
        row.Children.Add(name);
        row.Children.Add(val);
        ProbePanel.Children.Add(row);
    }

    // ---------- 一键开启本地环境 ----------

    private async void OneClick_Click(object sender, RoutedEventArgs e) => await RunOneClickAsync();

    private async Task RunOneClickAsync()
    {
        var confirm = MessageBox.Show(
            "“一键开启本地打包环境”会修改系统状态，全自动执行以下步骤：\n\n" +
            "  1) 提权启用 Windows“虚拟机平台 / WSL”功能（会弹 UAC，可能需要【重启电脑】）；\n" +
            "  2) 安装 Ubuntu 发行版（wsl --install）；\n" +
            "  3) 在 Ubuntu 内安装 JDK 17、buildozer 及编译依赖（联网，可能 10+ 分钟）。\n\n" +
            "注意：\n" +
            "  · 若固件里没开虚拟化(VT-x/AMD-V)，软件无法修复，需你手动进 BIOS 开启；\n" +
            "  · 中途若提示重启，请重启后回到本页【再次点击本按钮】继续（流程可续跑）。\n\n" +
            "是否继续？",
            "一键开启本地打包环境", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        LogBox.Clear();
        AppServices.RaiseStatus("正在一键开启本地 APK 环境……");
        var progress = new Progress<int>(v => Dispatcher.Invoke(() => SetupProgress.Value = v));

        var result = await AppServices.ApkSetup.SetupAsync(AppendLog, progress);

        SetBusy(false);
        AppendLog(result.Success ? "[完成] " + result.Message : "[未完成] " + result.Message);
        AppServices.RaiseStatus(result.Message.Split('\n')[0]);

        MessageBox.Show(result.Message,
            result.Success ? "一键开启" : "一键开启未完成",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

        // 重新体检刷新界面
        await DetectLocalAsync(autoSelectBackend: false);
    }

    // ---------- 云端 ----------

    private async void TestConn_Click(object sender, RoutedEventArgs e)
    {
        AppServices.ApkCloud.SetServer(ServerBox.Text);
        SetBusy(true);
        AppendLog($"测试连接：{AppServices.ApkCloud.ServerBaseUrl}");
        var (ok, msg) = await AppServices.ApkCloud.TestConnectionAsync();
        AppendLog((ok ? "[成功] " : "[失败] ") + msg);
        SetBusy(false);
        AppServices.RaiseStatus(msg);
        if (!ok)
        {
            MessageBox.Show(msg, "连接测试", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- 参数收集 / spec 生成 ----------

    private ApkBuildParams CollectParams()
    {
        var isFlet = FletRadio.IsChecked == true;
        return new ApkBuildParams(
            AppName: string.IsNullOrWhiteSpace(AppNameBox.Text) ? "MyApp" : AppNameBox.Text.Trim(),
            PackageName: string.IsNullOrWhiteSpace(PackageBox.Text) ? "com.example.myapp" : PackageBox.Text.Trim(),
            Version: string.IsNullOrWhiteSpace(VersionBox.Text) ? "0.1" : VersionBox.Text.Trim(),
            Entry: string.IsNullOrWhiteSpace(EntryBox.Text) ? "main.py" : EntryBox.Text.Trim(),
            Permissions: string.IsNullOrWhiteSpace(PermBox.Text) ? "INTERNET" : PermBox.Text.Trim(),
            IsFlet: isFlet,
            MinApi: MinSdkApi(),
            TargetApi: TargetSdkApi(),
            Archs: SelectedArchs());
    }

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Python 移动端项目目录" };
        if (dialog.ShowDialog() == true)
        {
            ProjectPathBox.Text = dialog.FolderName;
            AppServices.RaiseStatus($"已选择项目：{dialog.FolderName}");
        }
    }

    private void GenSpec_Click(object sender, RoutedEventArgs e)
    {
        var project = ProjectPathBox.Text.Trim();
        if (!Directory.Exists(project))
        {
            MessageBox.Show("请先选择有效的项目目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        WriteSpec(project, CollectParams(), announceSuccess: true);
    }

    /// <summary>把 buildozer.spec 写入项目目录；返回是否成功。</summary>
    private bool WriteSpec(string project, ApkBuildParams p, bool announceSuccess)
    {
        var requirements = p.IsFlet ? "python3,flet,kivy" : "python3,kivy";
        var spec = BuildSpecTemplate(p.AppName, p.PackageName, p.Version, p.Entry, p.Permissions,
            requirements, p.IsFlet, p.MinApi, p.TargetApi, p.Archs);
        try
        {
            var specPath = Path.Combine(project, "buildozer.spec");
            File.WriteAllText(specPath, spec, new UTF8Encoding(false));
            AppendLog($"[成功] 已生成 buildozer.spec：{specPath}");
            AppendLog($"        目标：minSdk={p.MinApi} targetSdk={p.TargetApi} ABIs={(string.IsNullOrEmpty(p.Archs) ? "(默认)" : p.Archs)}");
            if (announceSuccess)
            {
                if (p.IsFlet) AppendLog("提示：Flet 项目也可用 `flet build apk`，其内部同样依赖 buildozer 工具链。");
                AppServices.RaiseStatus("buildozer.spec 模板已生成。");
            }
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("[错误] 写入 buildozer.spec 失败：" + ex.Message);
            return false;
        }
    }

    private int MinSdkApi() => MinSdkCombo.SelectedIndex switch
    {
        1 => 23, 2 => 24, 3 => 26, 4 => 28, 5 => 29, 6 => 30, 7 => 31, 8 => 33,
        _ => 21,
    };

    private int TargetSdkApi() => TargetSdkCombo.SelectedIndex switch
    {
        0 => 30, 1 => 31, 3 => 34, 4 => 35,
        _ => 33,
    };

    private string SelectedArchs()
    {
        var list = new List<string>();
        if (AbiArmv7.IsChecked == true) list.Add("armeabi-v7a");
        if (AbiArm64.IsChecked == true) list.Add("arm64-v8a");
        if (AbiX86.IsChecked == true) list.Add("x86");
        if (AbiX8664.IsChecked == true) list.Add("x86_64");
        return string.Join(",", list);
    }

    private static string BuildSpecTemplate(string appName, string package, string version,
        string entry, string perms, string requirements, bool isFlet,
        int minApi, int targetApi, string archs)
    {
        var (pkgDomain, pkgName) = SplitPackage(package);
        var archsLine = string.IsNullOrWhiteSpace(archs) ? "arm64-v8a, armeabi-v7a" : archs.Replace(",", ", ");
        return $"""
            [app]
            title = {appName}
            package.name = {pkgName}
            package.domain = {pkgDomain}
            source.dir = .
            source.main = {entry}
            version = {version}
            requirements = {requirements}
            permissions = {perms}
            orientation = portrait
            fullscreen = 0
            android.api = {targetApi}
            android.minapi = {minApi}
            android.ndk = 25b
            android.archs = {archsLine}
            android.accept_sdk_license = True
            build.mode = debug

            [buildozer]
            log_level = 2
            warn_on_root = 1

            # 由 PyPackager 生成（框架：{(isFlet ? "Flet" : "Kivy")}）。
            # 目标：minSdk={minApi} targetSdk={targetApi} ABIs={archsLine}
            # 实际编译：本地 WSL2 / 云端服务器 执行 buildozer android debug
            """;
    }

    private static (string domain, string name) SplitPackage(string package)
    {
        var idx = package.LastIndexOf('.');
        if (idx <= 0 || idx == package.Length - 1) return ("org", package.Replace(".", ""));
        return (package[..idx], package[(idx + 1)..]);
    }

    // ---------- 打包 APK：本地优先，云端回退 ----------

    private async void BuildApk_Click(object sender, RoutedEventArgs e)
    {
        var project = ProjectPathBox.Text.Trim();
        if (!Directory.Exists(project))
        {
            MessageBox.Show("请先选择有效的项目目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var p = CollectParams();
        if (!File.Exists(Path.Combine(project, "buildozer.spec")) && !WriteSpec(project, p, announceSuccess: false))
            return;

        // 打包前门禁：确认项目用到的库 / UI 能否做成 APK
        var pre = await RunPreflightAsync(project, quiet: true);
        if (pre is null) return;                       // 项目无效或异常
        if (!PassPreflightGate(pre)) return;  // 用户取消 / 有阻断且未强制

        if (LocalRadio.IsChecked == true)
        {
            await BuildLocalAsync(project);
        }
        else
        {
            await BuildCloudAsync(project, p);
        }
    }

    private async Task BuildLocalAsync(string project)
    {
        // 确保本地就绪；未就绪则按可修复性决定：可修复→引导一键；不可修复→自动转云端兜底。
        var report = AppServices.LastApkReport ?? await AppServices.ApkDoctor.DetectAsync(AppendLog);
        if (!report.CanBuildLocally)
        {
            AppendLog($"[提示] 本地环境未就绪（{report.Headline}）。");

            if (report.Verdict is ApkLocalVerdict.Blocked or ApkLocalVerdict.Unavailable)
            {
                // 本机根本无法本地编译，直接走云端兜底
                AppendLog("[回退] 本地不可用，自动改用云端服务器打包。");
                await CloudFallbackAsync(project, report.Headline);
                return;
            }

            // Fixable：优先引导一键开启本地
            var ask = MessageBox.Show(
                "本地环境尚未就绪，但可一键修复。\n\n" +
                $"当前状态：{report.Headline}\n\n" +
                "是否现在运行「一键开启本地打包环境」？\n" +
                "（选“否”将改用云端服务器打包）",
                "本地环境未就绪", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Yes)
            {
                await RunOneClickAsync();
                return;
            }
            await CloudFallbackAsync(project, "你选择不修复本地环境");
            return;
        }

        SetBusy(true);
        LogBox.Clear();
        ResetResult();
        AppendLog($"使用本地 WSL2{(report.DistroName is null ? "" : $"（{report.DistroName}）")}编译 APK……");
        AppServices.RaiseStatus("正在本地编译 APK（首次会下载 Android SDK/NDK，耗时较长）……");

        var result = await AppServices.ApkLocal.BuildAsync(report.DistroName, project, AppendLog);

        SetBusy(false);
        FinishBuild(result.Success, result.ApkPath, result.Error, "本地 WSL2");
    }

    /// <summary>本地不可用时的云端兜底：未配服务器则提示，配了就编译。</summary>
    private async Task CloudFallbackAsync(string project, string reason)
    {
        CloudRadio.IsChecked = true;
        if (string.IsNullOrWhiteSpace(ServerBox.Text))
        {
            AppendLog("[提示] 未配置云端服务器地址，无法兜底。");
            MessageBox.Show(
                $"本地打包不可用（{reason}），且尚未配置云端服务器。\n\n" +
                "请在第 6 节填写云端服务器地址后重试；服务端参考实现见 scripts/apk_server/。",
                "无法打包", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await BuildCloudAsync(project, CollectParams());
    }

    private async Task BuildCloudAsync(string project, ApkBuildParams p)
    {
        AppServices.ApkCloud.SetServer(ServerBox.Text);
        if (string.IsNullOrWhiteSpace(AppServices.ApkCloud.ServerBaseUrl))
        {
            MessageBox.Show("请先在第 6 节填写云端服务器地址。\n\n服务端参考实现见 scripts/apk_server/，部署到任意 Linux 机器后填入其地址。",
                "未配置云端服务器", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        LogBox.Clear();
        ResetResult();
        AppendLog($"使用云端服务器编译 APK：{AppServices.ApkCloud.ServerBaseUrl}");
        AppServices.RaiseStatus("正在云端编译 APK……");
        var progress = new Progress<int>(v => Dispatcher.Invoke(() => SetupProgress.Value = v));

        var result = await AppServices.ApkCloud.BuildApkAsync(project, p, AppendLog, progress);

        SetBusy(false);
        FinishBuild(result.Success, result.ApkPath, result.Error, "云端服务器");
    }

    private void FinishBuild(bool ok, string? apkPath, string? error, string backendLabel)
    {
        if (ok && !string.IsNullOrEmpty(apkPath))
        {
            _lastApkPath = apkPath;
            OpenApkBtn.IsEnabled = true;
            AppendLog($"[成功] APK 已生成（{backendLabel}）：{apkPath}");
            AppServices.RaiseStatus("APK 打包完成：" + apkPath);
            SetResult(true, $"APK 已保存到本地：{apkPath}（{backendLabel}）");
            MessageBox.Show($"APK 打包完成！\n\n已保存到本地：\n{apkPath}", "成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            AppendLog($"[失败] {backendLabel} 打包失败：" + error);
            AppServices.RaiseStatus("APK 打包失败：" + error);
            SetResult(false, $"{backendLabel} 打包失败：{error}", includeRecentLog: true);
        }
    }

    // ---------- 打包前检查（依赖 / UI 兼容性）----------

    private async void Preflight_Click(object sender, RoutedEventArgs e)
    {
        var project = ProjectPathBox.Text.Trim();
        if (!Directory.Exists(project))
        {
            MessageBox.Show("请先选择有效的项目目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var report = await RunPreflightAsync(project, quiet: false);
        if (report is not null)
        {
            AppendLog("[打包前检查] " + report.Summary.Replace("\n", " | "));
            AppServices.RaiseStatus("打包前检查完成：" + PreflightLabel(report.Overall));
        }
    }

    private async Task<ApkPreflightReport?> RunPreflightAsync(string project, bool quiet)
    {
        try
        {
            SetBusy(true);
            Dispatcher.Invoke(() => PreflightVerdict.Text = "正在扫描项目依赖与 UI 框架……");
            var report = await Task.Run(() => AppServices.ApkPreflight.Analyze(project));
            RenderPreflight(report);
            if (quiet) AppendLog($"[打包前检查] {PreflightLabel(report.Overall)}：{report.Summary.Split('\n').Last()}");
            return report;
        }
        catch (Exception ex)
        {
            AppendLog("[错误] 打包前检查失败：" + ex.Message);
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderPreflight(ApkPreflightReport r)
    {
        Dispatcher.Invoke(() =>
        {
            PreflightVerdict.Text = PreflightLabel(r.Overall) + " — UI 框架：" + (r.UiFramework ?? "未检测到");
            PreflightVerdict.Foreground = LevelBrush(r.Overall);

            PreflightPanel.Children.Clear();
            AddPreflightRow("UI 框架", r.UiLevel, r.UiReason);
            foreach (var d in r.Dependencies) AddPreflightRow(d.Name, d.Level, d.Reason);
            PreflightCard.Visibility = Visibility.Visible;

            PreflightSummary.Text = r.Summary;
            PreflightSummary.Visibility = Visibility.Visible;
        });
    }

    /// <summary>打包门禁：Ok 直接放行；Warning 需确认；Blocking 默认拒绝（除非勾选强制）。</summary>
    private bool PassPreflightGate(ApkPreflightReport r)
    {
        if (r.Overall == ApkCompatLevel.Ok) return true;

        var force = ForceBuildCheck.IsChecked == true;

        if (r.Overall == ApkCompatLevel.Blocking)
        {
            var items = string.Join("\n", r.Blocking.Select(b => "  · " + b.Name + "：" + b.Reason));
            if (!force)
            {
                AppendLog("[阻断] 打包前检查发现不可打包的依赖，已中止。");
                MessageBox.Show(
                    "打包前检查发现【阻断项】，当前项目不适合直接打包成 APK：\n\n" + items +
                    "\n\n请替换为移动端方案（如 Flet/Kivy）后重试。\n" +
                    "如确要强行尝试，请勾选“即使有阻断项也允许尝试打包”。",
                    "无法打包", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            var okForce = MessageBox.Show(
                "已勾选强制打包，但存在阻断项，极可能失败：\n\n" + items + "\n\n仍要尝试吗？",
                "强制打包确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return okForce == MessageBoxResult.Yes;
        }

        // Warning
        if (force) return true;
        var warns = string.Join("\n", r.Warnings.Take(12).Select(w => "  · " + w.Name));
        var okWarn = MessageBox.Show(
            "打包前检查未发现阻断项，但有以下依赖需人工核对（含 C 扩展且无 p4a 配方的会失败）：\n\n" +
            warns + (r.Warnings.Count > 12 ? $"\n  …等共 {r.Warnings.Count} 项" : "") +
            "\n\n是否继续打包？",
            "存在待核对依赖", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return okWarn == MessageBoxResult.Yes;
    }

    private static string PreflightLabel(ApkCompatLevel level) => level switch
    {
        ApkCompatLevel.Ok => "✅ 可以尝试打包",
        ApkCompatLevel.Warning => "⚠️ 有待核对项",
        _ => "⛔ 存在阻断项",
    };

    private Brush LevelBrush(ApkCompatLevel level) => (Brush)FindResource(level switch
    {
        ApkCompatLevel.Ok => "GreenBrush",
        ApkCompatLevel.Warning => "YellowBrush",
        _ => "RedBrush",
    });

    private void AddPreflightRow(string name, ApkCompatLevel level, string reason)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Fill = new SolidColorBrush(level switch
            {
                ApkCompatLevel.Ok => Color.FromRgb(0xA6, 0xE3, 0xA1),
                ApkCompatLevel.Warning => Color.FromRgb(0xF9, 0xE2, 0xAF),
                _ => Color.FromRgb(0xF3, 0x8B, 0xA8),
            }),
        };
        var nameTb = new TextBlock
        {
            Text = name + "：",
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 150,
        };
        var reasonTb = new TextBlock
        {
            Text = reason,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        row.Children.Add(dot);
        row.Children.Add(nameTb);
        row.Children.Add(reasonTb);
        PreflightPanel.Children.Add(row);
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dir = ProjectPathBox.Text.Trim();
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true, Verb = "open" });
        else
            AppServices.RaiseStatus("请先选择有效的项目目录。");
    }

    private void OpenApk_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastApkPath) && File.Exists(_lastApkPath))
            Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(_lastApkPath)!, UseShellExecute = true, Verb = "open" });
        else
            AppServices.RaiseStatus("尚无已生成的 APK。");
    }
}
