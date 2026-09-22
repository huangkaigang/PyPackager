using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PyPackager.Models;

namespace PyPackager.Services;

/// <summary>云端编译结果。</summary>
public sealed record CloudBuildResult(bool Success, string? ApkPath, string? Error);

/// <summary>
/// 云端 APK 打包客户端：把项目打成 ZIP 上传到可配置的 Linux 编译服务器，
/// 轮询编译进度，完成后把 APK 下载回本地。
///
/// 服务端契约（参考实现见 scripts/apk_server/）：
///   GET  /api/health                 → 200 { "ok": true }
///   POST /api/build                  (multipart: file, appName, packageName, version,
///                                      entry, permissions, framework, minApi, targetApi, archs)
///                                    → 202 { "jobId": "..." }
///   GET  /api/status/{jobId}         → { state: queued|building|done|error, progress, log?, apkUrl?, error? }
///   GET  /api/download/{jobId}       → APK 二进制
/// </summary>
public sealed class ApkCloudClient
{
    private static readonly HttpClient Http = CreateClient();

    private static readonly string[] ExcludeDirs =
        { "bin", ".buildozer", ".venv", "venv", "__pycache__", ".git", ".gradle" };

    public string ServerBaseUrl { get; private set; } = string.Empty;

    public ApkCloudClient() => LoadConfig();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PyPackager/1.0 (+apk-cloud)");
        return client;
    }

    // ---------- 配置 ----------

    public void SetServer(string url)
    {
        ServerBaseUrl = Normalize(url);
        SaveConfig();
    }

    private static string Normalize(string url) =>
        string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd('/');

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(AppPaths.ApkConfigFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ApkConfigFile));
            if (doc.RootElement.TryGetProperty("serverBaseUrl", out var s) && s.GetString() is { Length: > 0 } v)
                ServerBaseUrl = v;
        }
        catch { /* 配置损坏则留空 */ }
    }

    private void SaveConfig()
    {
        try
        {
            AppPaths.EnsureRoot();
            var json = JsonSerializer.Serialize(new { serverBaseUrl = ServerBaseUrl },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.ApkConfigFile, json, new UTF8Encoding(false));
        }
        catch { /* 忽略写入失败 */ }
    }

    // ---------- 连接测试 ----------

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ServerBaseUrl))
            return (false, "尚未配置云端服务器地址。");
        try
        {
            using var resp = await Http.GetAsync(ServerBaseUrl + "/api/health", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? (true, $"连接成功（HTTP {(int)resp.StatusCode}）：{ServerBaseUrl}")
                : (false, $"服务器返回 HTTP {(int)resp.StatusCode}。请确认地址正确且服务已启动。");
        }
        catch (Exception ex)
        {
            return (false, "连接失败：" + ex.Message);
        }
    }

    // ---------- 编译 ----------

    public async Task<CloudBuildResult> BuildApkAsync(
        string projectDir, ApkBuildParams p, Action<string> log,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ServerBaseUrl))
            return new CloudBuildResult(false, null, "尚未配置云端服务器地址。");
        if (!Directory.Exists(projectDir))
            return new CloudBuildResult(false, null, $"项目目录不存在：{projectDir}");
        if (!File.Exists(Path.Combine(projectDir, "buildozer.spec")))
            return new CloudBuildResult(false, null, "项目目录下没有 buildozer.spec，请先生成模板。");

        var tmpZip = Path.Combine(Path.GetTempPath(), $"pypk_apk_{Guid.NewGuid():N}.zip");
        try
        {
            log("正在打包项目为 ZIP（排除 bin/.buildozer 等目录）……");
            CreateProjectZip(projectDir, tmpZip);
            var sizeMb = new FileInfo(tmpZip).Length / 1024.0 / 1024.0;
            log($"ZIP 就绪：{sizeMb:0.0} MB，上传到 {ServerBaseUrl} ……");
            progress?.Report(5);

            var jobId = await UploadAsync(tmpZip, p, log, progress, ct).ConfigureAwait(false);
            if (jobId is null)
                return new CloudBuildResult(false, null, "上传失败，未获得任务 ID。");

            log($"任务已提交，jobId={jobId}，开始轮询编译进度……");
            var apkUrl = await PollAsync(jobId, log, progress, ct).ConfigureAwait(false);
            if (apkUrl is null)
                return new CloudBuildResult(false, null, "云端编译失败或超时，请看日志。");

            var outDir = Path.Combine(projectDir, "bin");
            Directory.CreateDirectory(outDir);
            var outApk = Path.Combine(outDir, $"{Sanitize(p.PackageName)}-{p.Version}.apk");
            log($"下载 APK 到：{outApk}");
            if (!await DownloadAsync(apkUrl, outApk, log, progress, ct).ConfigureAwait(false))
                return new CloudBuildResult(false, null, "APK 下载失败。");

            progress?.Report(100);
            log("云端编译完成 ✓");
            return new CloudBuildResult(true, outApk, null);
        }
        catch (OperationCanceledException)
        {
            return new CloudBuildResult(false, null, "已取消。");
        }
        catch (Exception ex)
        {
            return new CloudBuildResult(false, null, "云端编译异常：" + ex.Message);
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* 忽略 */ }
        }
    }

    private async Task<string?> UploadAsync(
        string zipPath, ApkBuildParams p, Action<string> log, IProgress<int>? progress, CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var zipBytes = await File.ReadAllBytesAsync(zipPath, ct).ConfigureAwait(false);
            var fileContent = new ByteArrayContent(zipBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(fileContent, "file", Path.GetFileName(zipPath));

            void Add(string key, string val) => form.Add(new StringContent(val), key);
            Add("appName", p.AppName);
            Add("packageName", p.PackageName);
            Add("version", p.Version);
            Add("entry", p.Entry);
            Add("permissions", p.Permissions);
            Add("framework", p.IsFlet ? "flet" : "kivy");
            Add("minApi", p.MinApi.ToString());
            Add("targetApi", p.TargetApi.ToString());
            Add("archs", p.Archs);

            using var resp = await Http.PostAsync(ServerBaseUrl + "/api/build", form, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                log($"[错误] 上传失败 HTTP {(int)resp.StatusCode}：{Truncate(body)}");
                return null;
            }
            progress?.Report(15);
            return ExtractString(body, "jobId");
        }
        catch (Exception ex)
        {
            log("[错误] 上传异常：" + ex.Message);
            return null;
        }
    }

    private async Task<string?> PollAsync(
        string jobId, Action<string> log, IProgress<int>? progress, CancellationToken ct)
    {
        var lastState = string.Empty;
        var lastPct = -1;
        // 最长轮询 ~40 分钟（与 HttpClient 超时一致），每 3 秒一次。
        for (var i = 0; i < 800; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(3000, ct).ConfigureAwait(false);

            string body;
            try
            {
                body = await Http.GetStringAsync(ServerBaseUrl + $"/api/status/{Uri.EscapeDataString(jobId)}", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log("[警告] 轮询失败，将重试：" + ex.Message);
                continue;
            }

            var state = ExtractString(body, "state") ?? "unknown";
            var pct = ExtractInt(body, "progress");
            if (state != lastState)
            {
                log($"[状态] {state}");
                lastState = state;
            }
            if (pct >= 0 && pct != lastPct)
            {
                lastPct = pct;
                // 编译进度映射到总进度的 15%-85%
                progress?.Report(15 + pct * 70 / 100);
            }

            var tail = ExtractString(body, "log");
            if (!string.IsNullOrWhiteSpace(tail)) log("  " + Truncate(tail, 400));

            if (state.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                var url = ExtractString(body, "apkUrl");
                if (string.IsNullOrWhiteSpace(url))
                {
                    log("[错误] 编译完成但未返回 apkUrl。");
                    return null;
                }
                // 支持相对 URL
                return url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? url
                    : ServerBaseUrl + (url.StartsWith("/") ? url : "/" + url);
            }
            if (state.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                log("[错误] 云端编译失败：" + (ExtractString(body, "error") ?? "未知错误"));
                return null;
            }
        }
        log("[错误] 轮询超时。");
        return null;
    }

    private async Task<bool> DownloadAsync(
        string url, string destFile, Action<string> log, IProgress<int>? progress, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                log($"[错误] 下载失败 HTTP {(int)resp.StatusCode}。");
                return false;
            }
            var total = resp.Content.Headers.ContentLength ?? -1L;
            long done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(destFile);
            var buffer = new byte[128 * 1024];
            var lastPct = -1;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    var pct = (int)(done * 100 / total);
                    if (pct != lastPct) { lastPct = pct; progress?.Report(85 + pct * 15 / 100); }
                }
            }
            log($"下载完成：{done / 1024.0 / 1024.0:0.0} MB");
            return true;
        }
        catch (Exception ex)
        {
            log("[错误] 下载异常：" + ex.Message);
            return false;
        }
    }

    // ---------- ZIP 与解析辅助 ----------

    private static void CreateProjectZip(string projectDir, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        var root = new DirectoryInfo(projectDir);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(projectDir, file.FullName).Replace('\\', '/');
            if (IsExcluded(rel)) continue;
            try { zip.CreateEntryFromFile(file.FullName, rel, CompressionLevel.Optimal); }
            catch { /* 跳过被占用/不可读文件 */ }
        }
    }

    private static bool IsExcluded(string relativePath)
    {
        var first = relativePath.Split('/')[0];
        if (ExcludeDirs.Contains(first, StringComparer.OrdinalIgnoreCase)) return true;
        return relativePath.Split('/').Any(seg =>
            seg.Equals("__pycache__", StringComparison.OrdinalIgnoreCase));
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.Length == 0 ? "app" : sb.ToString();
    }

    private static string? ExtractString(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch { return null; }
    }

    private static int ExtractInt(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(key, out var el)) return -1;
            return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : -1;
        }
        catch { return -1; }
    }

    private static string Truncate(string s, int max = 300) =>
        s.Length <= max ? s : s[..max] + "…";
}
