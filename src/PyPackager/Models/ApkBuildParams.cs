namespace PyPackager.Models;

/// <summary>APK 打包的可配置参数（供云端 API 与本地 spec 生成共享）。</summary>
public sealed record ApkBuildParams(
    string AppName,
    string PackageName,
    string Version,
    string Entry,
    string Permissions,
    bool IsFlet,
    int MinApi,
    int TargetApi,
    string Archs);
