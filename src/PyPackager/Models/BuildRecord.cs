namespace PyPackager.Models;

/// <summary>一条打包历史记录。</summary>
public sealed class BuildRecord
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ProjectPath { get; set; } = string.Empty;
    public string? EntryFile { get; set; }
    public string Engine { get; set; } = "PyInstaller";
    public string? AppName { get; set; }
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }

    public string StatusText => Success ? "成功" : "失败";
    public string DurationText => $"{DurationMs / 1000.0:0.0}s";
    public string OutputOrError => Success ? (OutputPath ?? "") : (Error ?? "未知错误");
}
