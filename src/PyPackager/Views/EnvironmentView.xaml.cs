using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PyPackager.Models;
using PyPackager.Services;

namespace PyPackager.Views;

public partial class EnvironmentView : UserControl
{
    private CancellationTokenSource? _cts;
    private readonly List<ManagedRuntime> _runtimes = new();

    public EnvironmentView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            PopulateMirrors();
            RefreshRuntimeList();
            await DetectAsync();
        };
    }

    private void AppendLog(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private void SetProgress(int v, string? text = null) => Dispatcher.Invoke(() =>
    {
        Progress.Value = v;
        ProgressText.Text = text ?? (v > 0 ? $"{v}%" : string.Empty);
    });

    private void SetBusy(bool busy) => Dispatcher.Invoke(() =>
    {
        DetectBtn.IsEnabled = !busy;
        InitBtn.IsEnabled = !busy;
        ResetBtn.IsEnabled = !busy;
        DownloadRtBtn.IsEnabled = !busy;
        DeleteRtBtn.IsEnabled = !busy;
        ApplyMirrorBtn.IsEnabled = !busy;
    });

    private async Task DetectAsync()
    {
        AppServices.RaiseStatus("正在检测构建环境……");
        var report = await AppServices.Environment.DetectAsync();
        AppServices.LastEnvReport = report;
        RenderReport(report);
        AppServices.RaiseStatus("环境检测完成。");
    }

    private void RenderReport(EnvReport r)
    {
        LeftCol.Children.Clear();
        RightCol.Children.Clear();

        AddItem(LeftCol, "Python", r.PythonPath is null ? "未检测到" : $"{r.PythonVersion}", r.PythonPath is not null,
            r.PythonPath);
        AddItem(LeftCol, "pip", r.PipAvailable ? "可用" : "不可用", r.PipAvailable);
        AddItem(LeftCol, "PyInstaller", r.PyInstallerAvailable ? r.PyInstallerVersion ?? "已安装" : "未安装", r.PyInstallerAvailable);
        AddItem(LeftCol, "内部虚拟环境", AppServices.Venv.Exists() ? "已就绪" : "未创建", AppServices.Venv.Exists(),
            AppServices.Venv.Exists() ? AppPaths.VenvDir : null);

        AddItem(RightCol, "Nuitka", r.NuitkaAvailable ? r.NuitkaVersion ?? "已安装" : "未安装", r.NuitkaAvailable);
        AddItem(RightCol, "C 编译器", r.HasCCompiler ? r.CCompilerName ?? "已检测到" : "未检测到（Nuitka 会自动下载 MinGW64）", r.HasCCompiler);
        AddItem(RightCol, "WSL (APK 用)", r.WslAvailable ? "可用" : "不可用", r.WslAvailable);
        AddItem(RightCol, "EXE 打包能力",
            r.PythonPath is not null ? "可用（缺工具会自动装）" : "不可用（缺 Python）",
            r.PythonPath is not null);
    }

    private static void AddItem(StackPanel panel, string label, string value, bool ok, string? detail = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Fill = new SolidColorBrush(ok
                ? Color.FromRgb(0xA6, 0xE3, 0xA1)
                : Color.FromRgb(0xF9, 0xE2, 0xAF)),
        };

        var name = new TextBlock
        {
            Text = label + "：",
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 110,
        };

        var val = new TextBlock
        {
            Text = value,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = string.IsNullOrEmpty(detail) ? value : detail,
        };

        row.Children.Add(dot);
        row.Children.Add(name);
        row.Children.Add(val);
        panel.Children.Add(row);
    }

    private async void Detect_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private async void Init_Click(object sender, RoutedEventArgs e)
    {
        var report = AppServices.LastEnvReport ?? await AppServices.Environment.DetectAsync();
        if (report.PythonPath is null)
        {
            MessageBox.Show("未检测到系统 Python。\n\n内部虚拟环境也需要基于系统 Python 创建，请先安装 Python 3.8+ 并勾选“加入 PATH”，然后重新检测。",
                "无法初始化", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LogBox.Clear();
        SetBusy(true);
        SetProgress(0, "初始化中…");
        AppServices.RaiseStatus("正在初始化内部虚拟环境（首次需下载安装 PyInstaller + Nuitka，可能几分钟）……");
        _cts = new CancellationTokenSource();

        AppendLog($"系统 Python：{report.PythonPath}");
        var result = await AppServices.Venv.EnsureAsync(
            report.PythonPath,
            new[] { "pyinstaller", "nuitka" },
            AppendLog,
            new Progress<int>(v => SetProgress(v)),
            _cts.Token);

        SetBusy(false);
        if (result.Success)
        {
            SetProgress(100, "完成 ✓");
            AppendLog($"[成功] 内部虚拟环境就绪：{result.PythonPath}");
            AppServices.RaiseStatus("内部环境初始化完成，打包时会自动使用。");
            MessageBox.Show($"内部虚拟环境已就绪！\n\n{result.PythonPath}\n\n已安装 PyInstaller 与 Nuitka。",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            SetProgress(0, "失败 ✗");
            AppendLog("[失败] " + result.Error);
            AppServices.RaiseStatus("内部环境初始化失败：" + result.Error);
        }

        await DetectAsync();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!AppServices.Venv.Exists())
        {
            AppServices.RaiseStatus("内部虚拟环境尚未创建，无需重置。");
            return;
        }
        var confirm = MessageBox.Show("确定删除内部虚拟环境？下次打包会重新创建。", "重置确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        LogBox.Clear();
        AppServices.Venv.Reset(AppendLog);
        AppServices.RaiseStatus("内部虚拟环境已重置。");
        _ = DetectAsync();
    }

    private void OpenVenv_Click(object sender, RoutedEventArgs e)
    {
        var dir = AppPaths.VenvDir;
        if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true, Verb = "open" });
        }
        else
        {
            AppServices.RaiseStatus("内部虚拟环境尚未创建。");
        }
    }

    // ---------- 托管运行时管理 ----------

    private void PopulateMirrors()
    {
        MirrorCombo.Items.Clear();
        var current = AppServices.Runtimes.PipIndexUrl;
        var selectIndex = 0;
        for (var i = 0; i < RuntimeManager.PipMirrors.Length; i++)
        {
            var (name, url) = RuntimeManager.PipMirrors[i];
            MirrorCombo.Items.Add(new ComboBoxItem { Content = name, Tag = url });
            if (string.Equals(url, current, StringComparison.OrdinalIgnoreCase)) selectIndex = i;
        }
        MirrorCombo.SelectedIndex = selectIndex;
    }

    private void ApplyMirror_Click(object sender, RoutedEventArgs e)
    {
        if (MirrorCombo.SelectedItem is ComboBoxItem { Tag: string url })
        {
            AppServices.Runtimes.SetPipIndex(url);
            AppServices.RaiseStatus($"pip 镜像已切换为：{url}");
            AppendLog($"[配置] pip 镜像 → {url}");
        }
    }

    private void RefreshRuntimeList()
    {
        _runtimes.Clear();
        _runtimes.AddRange(AppServices.Runtimes.ListInstalled());
        RuntimeList.ItemsSource = _runtimes
            .Select(r => $"{r.PythonVersion,-8} {TargetCatalog.ArchDisplay(r.Arch),-12} · {r.SizeBytes / 1024.0 / 1024.0,0} MB · {r.PythonPath}")
            .ToList();
    }

    private async void DownloadRuntime_Click(object sender, RoutedEventArgs e)
    {
        var version = VersionCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            MessageBox.Show("请填写要下载的 Python 版本，例如 3.8.10。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var arch = RtArchCombo.SelectedIndex == 1 ? CpuArch.X64 : CpuArch.X86;

        LogBox.Clear();
        SetBusy(true);
        SetProgress(0, "下载中…");
        _cts = new CancellationTokenSource();
        AppServices.RaiseStatus($"正在准备托管运行时 Python {version} ({TargetCatalog.ArchDisplay(arch)})，首次需下载并安装 PyInstaller……");

        var result = await AppServices.Runtimes.EnsureAsync(
            version, arch, new[] { "pyinstaller" },
            AppendLog, new Progress<int>(v => SetProgress(v)), _cts.Token);

        SetBusy(false);
        if (result.Success)
        {
            SetProgress(100, "完成 ✓");
            AppendLog($"[成功] 运行时就绪：{result.PythonPath}");
            AppServices.RaiseStatus("托管运行时已就绪。");
        }
        else
        {
            SetProgress(0, "失败 ✗");
            AppendLog("[失败] " + result.Error);
            AppServices.RaiseStatus("运行时准备失败：" + result.Error);
        }
        RefreshRuntimeList();
    }

    private void DeleteRuntime_Click(object sender, RoutedEventArgs e)
    {
        var idx = RuntimeList.SelectedIndex;
        if (idx < 0 || idx >= _runtimes.Count)
        {
            AppServices.RaiseStatus("请先在列表中选中一个运行时。");
            return;
        }
        var r = _runtimes[idx];
        var confirm = MessageBox.Show(
            $"确定删除托管运行时 Python {r.PythonVersion} ({TargetCatalog.ArchDisplay(r.Arch)})？",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            AppServices.Runtimes.Delete(r.PythonVersion, r.Arch);
            AppendLog($"[删除] 已移除运行时 {r.PythonVersion} ({TargetCatalog.ArchToken(r.Arch)})");
            AppServices.RaiseStatus("运行时已删除。");
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 删除失败：{ex.Message}");
        }
        RefreshRuntimeList();
    }

    private void OpenRuntimes_Click(object sender, RoutedEventArgs e)
    {
        var dir = AppPaths.RuntimesDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true, Verb = "open" });
    }
}
