namespace PyPackager.Models;

/// <summary>某个依赖/UI 在 Android(buildozer) 下的兼容级别。</summary>
public enum ApkCompatLevel
{
    /// <summary>已知可用于 Android（移动端 UI 框架、纯 Python 或有 p4a 配方、标准库）。</summary>
    Ok,

    /// <summary>未知第三方包：纯 Python 多半可用，含 C 扩展且无配方则会失败，需人工核对。</summary>
    Warning,

    /// <summary>已知在 Android 上不可用/极难（桌面 GUI、Windows 专有、重型科学计算等）。</summary>
    Blocking,
}

/// <summary>单个依赖的兼容性判定。</summary>
public sealed record ApkCompatItem(string Name, ApkCompatLevel Level, string Reason);

/// <summary>APK 打包前预检报告。</summary>
public sealed record ApkPreflightReport(
    string? UiFramework,
    ApkCompatLevel UiLevel,
    string UiReason,
    IReadOnlyList<ApkCompatItem> Dependencies,
    int ScannedFileCount,
    ApkCompatLevel Overall,
    string Summary)
{
    /// <summary>没有阻断项即可尝试打包（Warning 仍需用户确认）。</summary>
    public bool CanBuild => Overall != ApkCompatLevel.Blocking;
    public bool HasBlocking => Overall == ApkCompatLevel.Blocking;
    public bool HasWarning => Dependencies.Any(d => d.Level == ApkCompatLevel.Warning);

    public IReadOnlyList<ApkCompatItem> Blocking =>
        Dependencies.Where(d => d.Level == ApkCompatLevel.Blocking).ToList();

    public IReadOnlyList<ApkCompatItem> Warnings =>
        Dependencies.Where(d => d.Level == ApkCompatLevel.Warning).ToList();
}
