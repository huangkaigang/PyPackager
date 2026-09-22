using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PyPackager.Models;
using PyPackager.Services;

namespace PyPackager.Views;

public partial class ExeBuildView : UserControl
{
    private CancellationTokenSource? _cts;
    private string? _lastOutputDir;
    // 最近日志环形缓冲：失败时把日志直接铺到页面横幅
    private readonly List<string> _recentLogs = new();
    private const int RecentLogCap = 80;

    public ExeBuildView()
    {
        InitializeComponent();
        UpdateTargetHint();
        Loaded += async (_, _) =>
        {
            RefreshInnoStatus();
            await Task.CompletedTask;
        };
    }

    // ---------- 日志 / 进度 / 状态 ----------

    private void AppendLog(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        _recentLogs.Add(line);
        if (_recentLogs.Count > RecentLogCap) _recentLogs.RemoveAt(0);
    });

    /// <summary>把成功/失败/异常结果常驻显示在页面横幅；失败时附带最近日志。</summary>
    private void SetResult(bool ok, string headline, bool includeRecentLog = false) => Dispatcher.Invoke(() =>
    {
        ResultBanner.Visibility = Visibility.Visible;
        ResultBanner.Background = (Brush)FindResource(ok ? "GreenBrush" : "RedBrush");
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
        var sb = new System.Text.StringBuilder();
        sb.Append(ok ? "✔ 成功：" : "✘ 失败：").Append(headline);
        if (!ok && includeRecentLog && _recentLogs.Count > 0)
        {
            sb.Append("\n──── 最近日志 ────\n");
            sb.Append(string.Join("\n", _recentLogs.TakeLast(30)));
        }
        ResultText.Text = sb.ToString();
        ResultText.ScrollToEnd();
    });

    private void ResetResult() => Dispatcher.Invoke(() =>
    {
        _recentLogs.Clear();
        ResultBanner.Visibility = Visibility.Collapsed;
        ResultText.Text = string.Empty;
    });

    private void SetProgress(int value, string? text = null) => Dispatcher.Invoke(() =>
    {
        Progress.Value = value;
        ProgressText.Text = text ?? $"{value}%";
    });

    private void SetStatus(string text) => AppServices.RaiseStatus(text);

    private void SetBusy(bool busy) => Dispatcher.Invoke(() =>
    {
        BuildBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = busy;
        ProjectPathBox.IsEnabled = !busy;
        EntryCombo.IsEnabled = !busy;
    });

    // ---------- 拖拽 ----------

    private void UserControl_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void UserControl_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var path = paths[0];
        if (Directory.Exists(path))
        {
            SetProject(path);
        }
        else if (File.Exists(path) && path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                SetProject(dir);
                Dispatcher.Invoke(() => EntryCombo.Text = Path.GetFileName(path));
            }
        }
        else
        {
            SetStatus("请拖入一个 Python 项目文件夹或 .py 文件。");
        }
    }

    private void SetProject(string dir)
    {
        Dispatcher.Invoke(() => ProjectPathBox.Text = dir);
        RefreshEntryList(dir);
        SetStatus($"已选择项目：{dir}");
    }

    // ---------- 选择 ----------

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Python 项目目录" };
        if (dialog.ShowDialog() == true) SetProject(dialog.FolderName);
    }

    private void RefreshEntry_Click(object sender, RoutedEventArgs e)
    {
        var dir = ProjectPathBox.Text.Trim();
        if (Directory.Exists(dir))
        {
            RefreshEntryList(dir);
            SetStatus("已刷新入口文件列表。");
        }
        else SetStatus("请先选择有效的项目目录。");
    }

    private void RefreshEntryList(string dir)
    {
        try
        {
            var files = Directory.EnumerateFiles(dir, "*.py", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Dispatcher.Invoke(() =>
            {
                EntryCombo.ItemsSource = files;
                var preferred = new[] { "main.py", "app.py", "run.py", "start.py", "__main__.py" };
                EntryCombo.Text = preferred.FirstOrDefault(p => files.Contains(p, StringComparer.OrdinalIgnoreCase))
                                  ?? files.FirstOrDefault() ?? string.Empty;
            });
        }
        catch (Exception ex) { AppendLog($"[警告] 读取目录失败：{ex.Message}"); }
    }

    private void BrowseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择图标文件", Filter = "图标文件 (*.ico)|*.ico|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() == true) IconBox.Text = dialog.FileName;
    }

    private void Engine_Changed(object sender, RoutedEventArgs e)
    {
        if (NuitkaPanel is null || PyInstallerPanel is null) return;
        var nuitka = EngineNuitka.IsChecked == true;
        NuitkaPanel.Visibility = nuitka ? Visibility.Visible : Visibility.Collapsed;
        PyInstallerPanel.Visibility = nuitka ? Visibility.Collapsed : Visibility.Visible;
        EngineHint.Text = nuitka
            ? "Nuitka：编译为 C 再编译机器码，运行更快、极难反编译，适合商业保护。首次编译会自动下载 C 编译器后端（Zig，约几十 MB，期间日志可能长时间无输出，请耐心等待）；后续编译走缓存，速度正常。"
            : "PyInstaller：成熟、快、无需 C 编译器，适合大多数场景。";
        BuildBtn.Content = nuitka ? "🚀 开始编译 EXE (Nuitka)" : "🚀 开始打包 EXE";
        UpdateTargetHint();
    }

    // ---------- 目标平台 ----------

    private WindowsTarget SelectedTarget => TargetCombo.SelectedIndex switch
    {
        1 => WindowsTarget.Win7,
        2 => WindowsTarget.Win8,
        3 => WindowsTarget.Win10,
        4 => WindowsTarget.Win11,
        _ => WindowsTarget.Auto,
    };

    private CpuArch SelectedArch => ArchCombo.SelectedIndex switch
    {
        1 => CpuArch.X86,
        2 => CpuArch.X64,
        _ => CpuArch.Auto,
    };

    private void Target_Changed(object sender, RoutedEventArgs e) => UpdateTargetHint();

    private void UpdateTargetHint()
    {
        if (TargetHint is null || TargetCombo is null || ArchCombo is null) return;
        var profile = TargetCatalog.Get(SelectedTarget);
        var nuitka = EngineNuitka.IsChecked == true;
        var sb = new System.Text.StringBuilder();

        if (profile.NeedsManagedRuntime || SelectedArch != CpuArch.Auto)
        {
            var version = profile.PythonVersion ?? TargetCatalog.Get(WindowsTarget.Win10).PythonVersion!;
            var arch = TargetCatalog.EffectiveArch(SelectedArch);
            sb.Append($"将使用托管 Python {version} · {TargetCatalog.ArchDisplay(arch)}（首次自动下载，约十几 MB）。");
        }
        else
        {
            sb.Append("不下载运行时，直接用本机系统 Python（缺工具时落到内部虚拟环境）。");
        }
        sb.Append(' ').Append(profile.Note);

        if (nuitka && !profile.NuitkaSupported)
        {
            sb.Insert(0, "⚠ 当前引擎(Nuitka)与该目标不兼容！");
            TargetHint.Foreground = (Brush)new BrushConverter().ConvertFromString("#F38BA8")!;
        }
        else
        {
            TargetHint.Foreground = (Brush)FindResource("MutedBrush");
        }
        TargetHint.Text = sb.ToString();
    }

    // ---------- 打包 ----------

    private BuildOptions CollectOptions()
    {
        var o = new BuildOptions
        {
            Engine = EngineNuitka.IsChecked == true ? BuildEngine.Nuitka : BuildEngine.PyInstaller,
            ProjectPath = ProjectPathBox.Text.Trim(),
            EntryFile = string.IsNullOrWhiteSpace(EntryCombo.Text) ? null : EntryCombo.Text.Trim(),
            AppName = string.IsNullOrWhiteSpace(AppNameBox.Text) ? null : AppNameBox.Text.Trim(),
            IconPath = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text.Trim(),
            OneFile = OneFileCheck.IsChecked == true,
            NoConsole = NoConsoleCheck.IsChecked == true,
            Target = SelectedTarget,
            Arch = SelectedArch,
        };

        // 共享：数据文件与隐藏导入
        foreach (var d in DataList.Items.OfType<string>()) o.Data.Add(d);
        foreach (var h in HiddenList.Items.OfType<string>()) o.HiddenImports.Add(h);

        if (o.Engine == BuildEngine.Nuitka)
        {
            o.Compiler = CompilerCombo.SelectedIndex switch
            {
                1 => NuitkaCompiler.MinGW64,
                2 => NuitkaCompiler.MSVC,
                _ => NuitkaCompiler.Auto,
            };
            o.Lto = LtoCheck.IsChecked == true;
            o.Jobs = int.TryParse(JobsBox.Text.Trim(), out var j) ? j : 0;
            o.RemoveOutput = RemoveOutputCheck.IsChecked == true;
            o.ProductName = string.IsNullOrWhiteSpace(ProductNameBox.Text) ? null : ProductNameBox.Text.Trim();
            o.Version = string.IsNullOrWhiteSpace(FileVersionBox.Text) ? null : FileVersionBox.Text.Trim();
        }
        else
        {
            o.UseUpx = UpxCheck.IsChecked == true;
            o.UpxDir = string.IsNullOrWhiteSpace(UpxDirBox.Text) ? null : UpxDirBox.Text.Trim();
            o.Version = string.IsNullOrWhiteSpace(FileVersionBox.Text) ? null : FileVersionBox.Text.Trim();
            o.ProductName = string.IsNullOrWhiteSpace(ProductNameBox.Text) ? null : ProductNameBox.Text.Trim();
        }
        return o;
    }

    // ---------- 数据文件 / 隐藏导入 编辑器 ----------

    private void AddData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择要随程序打包的数据文件", Multiselect = true, Filter = "所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        foreach (var file in dialog.FileNames)
        {
            // 目标目录默认为 "."（EXE 同级），用户可在列表中双击修改
            DataList.Items.Add($"{file};.");
        }
    }

    private void RemoveData_Click(object sender, RoutedEventArgs e)
    {
        while (DataList.SelectedItems.Count > 0) DataList.Items.RemoveAt(DataList.SelectedIndex);
    }

    private void DataList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataList.SelectedItem is not string current) return;
        var prompt = PromptDialog.Ask("编辑数据文件映射", "格式：源路径;目标目录（例如 assets;assets）", current);
        if (prompt is not null)
        {
            var idx = DataList.SelectedIndex;
            DataList.Items[idx] = prompt;
        }
    }

    private void AddHidden_Click(object sender, RoutedEventArgs e)
    {
        var prompt = PromptDialog.Ask("添加隐藏导入", "输入模块名（例如 PIL、numpy、pkg_resources）", string.Empty);
        if (!string.IsNullOrWhiteSpace(prompt)) HiddenList.Items.Add(prompt.Trim());
    }

    private void RemoveHidden_Click(object sender, RoutedEventArgs e)
    {
        while (HiddenList.SelectedItems.Count > 0) HiddenList.Items.RemoveAt(HiddenList.SelectedIndex);
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        var options = CollectOptions();
        if (string.IsNullOrWhiteSpace(options.ProjectPath) || !Directory.Exists(options.ProjectPath))
        {
            MessageBox.Show("请先选择一个有效的 Python 项目目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LogBox.Clear();
        ResetResult();
        _lastOutputDir = null;
        OpenOutBtn.IsEnabled = false;
        SetBusy(true);
        SetProgress(0, "准备中…");
        SetStatus("正在准备打包环境……");
        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        // 解析用于打包的 Python（按目标系统/架构选择：本机、内部 venv 或托管运行时）
        var resolution = await AppServices.ResolveBuildPythonAsync(options.Engine, options.Target, options.Arch, AppendLog, new Progress<int>(v => SetProgress(v / 4)), _cts.Token);
        if (!resolution.Success)
        {
            sw.Stop();
            SetBusy(false);
            SetProgress(0, "失败 ✗");
            AppendLog("[错误] " + resolution.Error);
            SetStatus("环境准备失败：" + resolution.Error);
            SetResult(false, "环境准备失败：" + resolution.Error, includeRecentLog: true);
            RecordHistory(options, false, null, resolution.Error, sw.ElapsedMilliseconds);
            return;
        }

        SetStatus("正在打包，请稍候……");
        var service = new BuildService(resolution.PythonPath);
        service.LogReceived += AppendLog;
        service.ProgressChanged += v => SetProgress(v);

        AppendLog($"—— 开始打包 EXE（引擎：{options.Engine}，Python：{resolution.PythonPath} · {resolution.SourceLabel}）——");
        BuildResult result;
        try
        {
            result = await service.BuildExeAsync(options, _cts.Token);
        }
        catch (Exception ex)
        {
            AppendLog("[错误] " + ex.Message);
            result = new BuildResult(false, null, ex.Message, -1);
        }
        finally
        {
            service.LogReceived -= AppendLog;
            service.ProgressChanged -= v => SetProgress(v);
        }

        sw.Stop();
        SetBusy(false);
        if (result.Success && result.OutputExe is not null)
        {
            SetProgress(100, "完成 ✓");
            _lastOutputDir = Path.GetDirectoryName(result.OutputExe);
            OpenOutBtn.IsEnabled = _lastOutputDir is not null;
            SetStatus($"打包成功（{sw.Elapsed.TotalSeconds:0.0}s）：{result.OutputExe}");
            AppendLog($"[成功] 产物已生成：{result.OutputExe}");
            RecordHistory(options, true, result.OutputExe, null, sw.ElapsedMilliseconds);
            SetResult(true, $"EXE 打包成功（{sw.Elapsed.TotalSeconds:0.0}s），已保存到本地：\n{result.OutputExe}");
            MessageBox.Show($"打包成功（{sw.Elapsed.TotalSeconds:0.0}s）！\n\n已保存到本地：\n{result.OutputExe}",
                "成功", MessageBoxButton.OK, MessageBoxImage.Information);

            // 勾选“打包成功后自动生成安装包”时，直接把输出目录编译成 setup
            if (EnableInstallerCheck.IsChecked == true && _lastOutputDir is not null)
            {
                await GenerateInstallerAsync(_lastOutputDir, Path.GetFileName(result.OutputExe));
            }
        }
        else
        {
            SetProgress(0, "失败 ✗");
            SetStatus("打包失败：" + (result.Error ?? "未知错误"));
            AppendLog("[失败] " + (result.Error ?? "未知错误"));
            SetResult(false, "打包失败：" + (result.Error ?? "未知错误"), includeRecentLog: true);
            RecordHistory(options, false, null, result.Error, sw.ElapsedMilliseconds);
        }
    }

    private void RecordHistory(BuildOptions o, bool success, string? output, string? error, long ms)
    {
        AppServices.History.Add(new BuildRecord
        {
            ProjectPath = o.ProjectPath,
            EntryFile = o.EntryFile,
            Engine = o.Engine.ToString(),
            AppName = o.AppName,
            Success = success,
            OutputPath = output,
            Error = error,
            DurationMs = ms,
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStatus("正在取消……");
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputDir is not null && Directory.Exists(_lastOutputDir))
        {
            Process.Start(new ProcessStartInfo { FileName = _lastOutputDir, UseShellExecute = true, Verb = "open" });
        }
    }

    // ---------- 打成安装包（Inno Setup） ----------

    /// <summary>探测本机 Inno Setup 并刷新提示文字与“下载安装”按钮可见性。</summary>
    private void RefreshInnoStatus()
    {
        var iscc = InstallerService.FindIscc();
        if (iscc is not null)
        {
            InnoStatusText.Foreground = (Brush)FindResource("GreenBrush");
            InnoStatusText.Text = $"✔ 已检测到 Inno Setup：{iscc}";
            InstallInnoBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            InnoStatusText.Foreground = (Brush)FindResource("YellowBrush");
            InnoStatusText.Text = $"⚠ 未检测到 Inno Setup（ISCC.exe）。生成安装包需要它，可点右侧按钮一键下载安装，或到 {InstallerService.DownloadUrl} 手动安装。";
            InstallInnoBtn.Visibility = Visibility.Visible;
        }
    }

    private async void InstallInno_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "即将从 Inno Setup 官网下载并静默安装（安装过程可能弹出 UAC 提权）。\n\n确认继续？",
            "下载并安装 Inno Setup", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirm != MessageBoxResult.Yes) return;

        InstallInnoBtn.IsEnabled = false;
        GenInstallerBtn.IsEnabled = false;
        AppendLog("[安装包] 开始获取 Inno Setup …");
        SetStatus("正在下载并安装 Inno Setup……");
        try
        {
            using var cts = new CancellationTokenSource();
            var ok = await InstallerService.EnsureInstalledAsync(AppendLog, new Progress<int>(v => SetProgress(v, $"下载中 {v}%")), cts.Token);
            RefreshInnoStatus();
            SetProgress(0, "就绪");
            if (ok)
            {
                SetResult(true, "Inno Setup 安装完成，现在可以生成安装包。");
                SetStatus("Inno Setup 安装完成。");
            }
            else
            {
                SetResult(false, "Inno Setup 安装未成功，请手动安装后重试。", includeRecentLog: true);
                SetStatus("Inno Setup 安装失败。");
            }
        }
        catch (Exception ex)
        {
            AppendLog("[错误] " + ex.Message);
            RefreshInnoStatus();
            SetProgress(0, "失败 ✗");
            SetResult(false, "下载/安装 Inno Setup 出错：" + ex.Message, includeRecentLog: true);
            SetStatus("Inno Setup 安装出错：" + ex.Message);
        }
        finally
        {
            InstallInnoBtn.IsEnabled = true;
            GenInstallerBtn.IsEnabled = true;
        }
    }

    private async void GenInstaller_Click(object sender, RoutedEventArgs e)
    {
        // 源目录：优先用最近一次打包的输出目录，否则让用户选一个含 EXE 的目录
        var sourceDir = _lastOutputDir;
        if (sourceDir is null || !Directory.Exists(sourceDir))
        {
            var dialog = new OpenFolderDialog { Title = "选择包含已打包 EXE 的输出目录" };
            if (dialog.ShowDialog() != true) return;
            sourceDir = dialog.FolderName;
        }

        // 主程序：目录里的第一个 .exe（排除 setup 自身）
        string? mainExe = null;
        try
        {
            mainExe = Directory.EnumerateFiles(sourceDir, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .FirstOrDefault(n => !string.IsNullOrEmpty(n) && !n.EndsWith("-Setup", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { AppendLog($"[警告] 读取目录失败：{ex.Message}"); }

        if (mainExe is null)
        {
            SetResult(false, $"目录中未找到可作为主程序的 EXE：{sourceDir}", includeRecentLog: true);
            return;
        }

        await GenerateInstallerAsync(sourceDir, mainExe);
    }

    /// <summary>把指定输出目录编译成 Inno Setup 安装包，并把结果常驻显示到页面。</summary>
    private async Task GenerateInstallerAsync(string sourceDir, string mainExe)
    {
        var appName = string.IsNullOrWhiteSpace(AppNameBox.Text)
            ? Path.GetFileNameWithoutExtension(mainExe)
            : AppNameBox.Text.Trim();
        var version = string.IsNullOrWhiteSpace(InstVersionBox.Text) ? "1.0.0" : InstVersionBox.Text.Trim();
        var publisher = string.IsNullOrWhiteSpace(InstPublisherBox.Text) ? "PyPackager" : InstPublisherBox.Text.Trim();
        var icon = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text.Trim();

        var opt = new InstallerOptions(
            AppName: appName,
            Version: version,
            Publisher: publisher,
            SourceDir: sourceDir,
            MainExe: mainExe,
            IconPath: icon,
            OutputDir: sourceDir,
            DesktopIcon: DesktopIconCheck.IsChecked == true);

        GenInstallerBtn.IsEnabled = false;
        AppendLog($"—— 开始生成安装包（{appName} {version}）——");
        SetStatus("正在生成安装包……");
        SetProgress(30, "编译安装包…");
        try
        {
            var res = await InstallerService.BuildAsync(opt, AppendLog);
            if (res.Success && res.InstallerPath is not null)
            {
                SetProgress(100, "安装包完成 ✓");
                SetResult(true, $"安装包已生成并保存到本地：\n{res.InstallerPath}");
                SetStatus($"安装包生成成功：{res.InstallerPath}");
                MessageBox.Show($"安装包生成成功！\n\n已保存到本地：\n{res.InstallerPath}",
                    "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                var dir = Path.GetDirectoryName(res.InstallerPath);
                if (dir is not null) _lastOutputDir = dir;
                OpenOutBtn.IsEnabled = true;
            }
            else
            {
                SetProgress(0, "失败 ✗");
                SetResult(false, res.Error ?? "安装包生成失败。", includeRecentLog: true);
                SetStatus("安装包生成失败：" + (res.Error ?? "未知错误"));
            }
        }
        catch (Exception ex)
        {
            AppendLog("[错误] " + ex.Message);
            SetProgress(0, "失败 ✗");
            SetResult(false, "生成安装包出错：" + ex.Message, includeRecentLog: true);
            SetStatus("生成安装包出错：" + ex.Message);
        }
        finally
        {
            GenInstallerBtn.IsEnabled = true;
        }
    }
}
