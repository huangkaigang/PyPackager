using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PyPackager.Models;
using PyPackager.Services;

namespace PyPackager.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        AppServices.History.Load();
        var records = AppServices.History.Records;
        HistoryList.ItemsSource = records;
        CountText.Text = $"共 {records.Count} 条记录";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Reload();
        AppServices.RaiseStatus("历史已刷新。");
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not BuildRecord record)
        {
            AppServices.RaiseStatus("请先在列表中选择一条记录。");
            return;
        }

        var target = record.OutputPath;
        if (!string.IsNullOrEmpty(target) && File.Exists(target))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetDirectoryName(target)!,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        else if (record.Success)
        {
            AppServices.RaiseStatus("产物文件已不存在，可能已被移动或删除。");
        }
        else
        {
            AppServices.RaiseStatus("该记录为失败任务，无产物可打开。");
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show("确定清空所有打包历史？此操作不可撤销。", "清空确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        AppServices.History.Clear();
        Reload();
        AppServices.RaiseStatus("历史已清空。");
    }
}
