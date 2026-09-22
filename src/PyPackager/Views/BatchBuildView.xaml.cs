using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PyPackager.Models;
using PyPackager.Services;

namespace PyPackager.Views;

public partial class BatchBuildView : UserControl
{
    private readonly ObservableCollection<BatchItem> _items = new();
    private CancellationTokenSource? _cts;

    public BatchBuildView()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
        UpdateTargetHint();
    }

    private void AppendLog(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private void SetProgress(int v, string? text = null) => Dispatcher.Invoke(() =>
    {
        Progress.Value = v;
        ProgressText.Text = text ?? $"{v}%";
    });

    private void SetBusy(bool busy) => Dispatcher.Invoke(() =>
    {
        BuildBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = busy;
        AddBtn.IsEnabled = !busy;
        RemoveBtn.IsEnabled = !busy;
        ClearBtn.IsEnabled = !busy;
        IconBtn.IsEnabled = !busy;
        IconBox.IsEnabled = !busy;
        VersionBox.IsEnabled = !busy;
        EngineCombo.IsEnabled = !busy;
    });

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要加入批量列表的 Python 项目目录", Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            foreach (var folder in dialog.FolderNames)
            {
                if (!_items.Any(i => i.ProjectPath == folder))
                {
                    _items.Add(new BatchItem { ProjectPath = folder });
                }
            }
            AppServices.RaiseStatus($"批量列表共 {_items.Count} 个项目。");
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is BatchItem item) _items.Remove(item);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _items.Clear();

    private void BrowseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择统一图标文件", Filter = "图标文件 (*.ico)|*.ico|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() == true) IconBox.Text = dialog.FileName;
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
        if (TargetHint is null || TargetCombo is null || ArchCombo is null || EngineCombo is null) return;
        var profile = TargetCatalog.Get(SelectedTarget);
        var nuitka = EngineCombo.SelectedIndex == 1;
        var sb = new System.Text.StringBuilder();

        if (profile.NeedsManagedRuntime || SelectedArch != CpuArch.Auto)
        {
            var version = profile.PythonVersion ?? TargetCatalog.Get(WindowsTarget.Win10).PythonVersion!;
            sb.Append($"将使用托管 Python {version} · {TargetCatalog.ArchDisplay(TargetCatalog.EffectiveArch(SelectedArch))}（首次自动下载，整批共用）。");
        }
        else
        {
            sb.Append("不下载运行时，直接用本机系统 Python。");
        }
        sb.Append(' ').Append(profile.Note);

        if (nuitka && !profile.NuitkaSupported)
        {
            sb.Insert(0, "⚠ Nuitka 与该目标不兼容，请改用 PyInstaller！");
            TargetHint.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                .ConvertFromString("#F38BA8")!;
        }
        else
        {
            TargetHint.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        }
        TargetHint.Text = sb.ToString();
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            MessageBox.Show("请先添加至少一个项目。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LogBox.Clear();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var engine = EngineCombo.SelectedIndex == 1 ? BuildEngine.Nuitka : BuildEngine.PyInstaller;
        var oneFile = OneFileCheck.IsChecked == true;
        var noConsole = NoConsoleCheck.IsChecked == true;
        var icon = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text.Trim();
        var version = string.IsNullOrWhiteSpace(VersionBox.Text) ? null : VersionBox.Text.Trim();

        // 统一解析一次 python（避免每个项目重复建 venv）
        AppendLog($"—— 批量打包开始（引擎：{engine}，共 {_items.Count} 个）——");
        var resolution = await AppServices.ResolveBuildPythonAsync(engine, SelectedTarget, SelectedArch, AppendLog, null, _cts.Token);
        if (!resolution.Success)
        {
            AppendLog("[错误] 环境准备失败：" + resolution.Error);
            SetBusy(false);
            AppServices.RaiseStatus("批量打包中止：环境准备失败。");
            return;
        }

        var done = 0;
        var ok = 0;
        foreach (var item in _items.ToList())
        {
            if (_cts.IsCancellationRequested) break;

            Dispatcher.Invoke(() => { item.Status = "打包中…"; item.Output = string.Empty; });
            var options = new BuildOptions
            {
                Engine = engine,
                ProjectPath = item.ProjectPath,
                OneFile = oneFile,
                NoConsole = noConsole,
                IconPath = icon,
                Version = version,
            };

            var service = new BuildService(resolution.PythonPath);
            service.LogReceived += line => AppendLog($"[{Path.GetFileName(item.ProjectPath)}] {line}");
            var sw = Stopwatch.StartNew();
            BuildResult result;
            try
            {
                result = await service.BuildExeAsync(options, _cts.Token);
            }
            catch (Exception ex)
            {
                result = new BuildResult(false, null, ex.Message, -1);
            }
            sw.Stop();

            Dispatcher.Invoke(() =>
            {
                item.Status = result.Success ? "✓ 成功" : "✗ 失败";
                item.Output = result.Success ? result.OutputExe ?? "" : (result.Error ?? "未知错误");
            });

            AppServices.History.Add(new BuildRecord
            {
                ProjectPath = item.ProjectPath,
                Engine = engine.ToString(),
                Success = result.Success,
                OutputPath = result.OutputExe,
                Error = result.Error,
                DurationMs = sw.ElapsedMilliseconds,
            });

            done++;
            if (result.Success) ok++;
            SetProgress(done * 100 / _items.Count, $"{done}/{_items.Count}");
        }

        SetBusy(false);
        SetProgress(100, "完成");
        AppServices.RaiseStatus($"批量打包完成：成功 {ok} / {done}。");
        AppendLog($"—— 批量打包结束：成功 {ok}，失败 {done - ok} ——");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AppServices.RaiseStatus("正在取消批量打包……");
    }
}
