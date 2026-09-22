using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PyPackager.Models;

/// <summary>批量打包列表中的一行，状态实时刷新。</summary>
public sealed class BatchItem : INotifyPropertyChanged
{
    private string _status = "待打包";
    private string _output = string.Empty;

    public string ProjectPath { get; set; } = string.Empty;

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string Output
    {
        get => _output;
        set { _output = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
