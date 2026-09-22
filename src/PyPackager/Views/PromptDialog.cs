using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PyPackager.Views;

/// <summary>轻量输入对话框：用于让用户手动录入 / 编辑一条文本（隐藏导入模块名、数据文件映射等）。</summary>
public sealed class PromptDialog : Window
{
    private readonly TextBox _input;

    private PromptDialog(string title, string message, string defaultValue)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
        Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4));

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8)),
        });

        _input = new TextBox
        {
            Text = defaultValue,
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
        };
        root.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = new Button { Content = "确定", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        var cancel = new Button { Content = "取消", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    /// <summary>弹出对话框；用户确认返回输入文本，取消返回 null。</summary>
    public static string? Ask(string title, string message, string defaultValue)
    {
        var dlg = new PromptDialog(title, message, defaultValue) { Owner = Application.Current?.MainWindow };
        return dlg.ShowDialog() == true ? dlg._input.Text : null;
    }
}
