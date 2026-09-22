using System.Windows;
using System.Windows.Controls;
using PyPackager.Services;
using PyPackager.Views;

namespace PyPackager;

public partial class MainWindow : Window
{
    // 视图懒加载并缓存，避免每次切换都重建、丢失已填内容
    private readonly Dictionary<string, UserControl> _views = new();

    public MainWindow()
    {
        InitializeComponent();
        AppServices.StatusChanged += OnStatusChanged;
        Loaded += (_, _) => ShowView("Exe");
    }

    private void OnStatusChanged(string message) => Dispatcher.Invoke(() => StatusBar.Text = message);

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            ShowView(tag);
        }
    }

    private void ShowView(string tag)
    {
        if (ContentHost is null) return;

        if (!_views.TryGetValue(tag, out var view))
        {
            view = tag switch
            {
                "Apk" => new ApkBuildView(),
                "Deploy" => new ServerDeployView(),
                "Batch" => new BatchBuildView(),
                "History" => new HistoryView(),
                "Env" => new EnvironmentView(),
                _ => new ExeBuildView(),
            };
            _views[tag] = view;
        }

        ContentHost.Content = view;

        // 切到历史页时刷新，保证看到最新记录
        if (view is HistoryView history)
        {
            history.Reload();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        AppServices.StatusChanged -= OnStatusChanged;
        base.OnClosed(e);
    }
}
