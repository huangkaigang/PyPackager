using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using PyPackager.Models;

namespace PyPackager.Services;

/// <summary>
/// 远程服务器自动配置编排：连上 SSH 后，按系统版本自动装好 Docker、写入安全的网络配置、
/// 上传并构建 APK 编译服务，最后健康检查，产出可直接填入客户端的服务器地址。
///
/// 关键安全设计（吸取"docker 网桥子网与 LAN 冲突把 SSH 挤下线"的教训）：
/// 部署前探测服务器上所有网卡/路由子网，为 Docker 选一个**不冲突**的 default-address-pools，
/// 避免 docker0/compose 网桥抢占宿主机 LAN 路由导致失联。
/// </summary>
public sealed class ServerProvisionService
{
    // Docker 默认地址池是 172.17~172.31/16，极易与常见 LAN（172.16-31）撞车。
    // 这里改用一段很少被 LAN 使用的 10.20x/16 作候选，并逐一避让服务器已有子网。
    private static readonly string[] PoolCandidates =
    {
        "10.201.0.0/16", "10.202.0.0/16", "10.203.0.0/16", "10.204.0.0/16",
        "10.205.0.0/16", "10.206.0.0/16", "10.207.0.0/16", "10.208.0.0/16",
    };

    // 发行版 ID 单一数据源在 SupportedServerCatalog，界面展示与识别共用，避免漂移。
    private static string[] DebianIds => SupportedServerCatalog.DebianIds;
    private static string[] RhelIds => SupportedServerCatalog.RhelIds;
    private static string[] SuseIds => SupportedServerCatalog.SuseIds;

    // ---------- 1) 系统探测 ----------

    public async Task<ServerOsInfo> DetectOsAsync(SshService ssh, Action<string> log, CancellationToken ct)
    {
        const string probe = @"
echo '__OS__'
. /etc/os-release 2>/dev/null
echo ""ID=$ID""
echo ""VERSION_ID=$VERSION_ID""
echo ""PRETTY=$PRETTY_NAME""
echo ""ARCH=$(uname -m)""
echo ""BITS=$(getconf LONG_BIT 2>/dev/null || echo 0)""
echo ""KERNEL=$(uname -r)""
echo '__DOCKER__'
docker --version 2>/dev/null || echo 'NO_DOCKER'
(docker compose version 2>/dev/null || docker-compose --version 2>/dev/null) || echo 'NO_COMPOSE'
echo '__SUBNETS__'
ip -o -4 addr show 2>/dev/null | awk '{print $4}'
ip -o -4 route show 2>/dev/null | awk '{print $1}' | grep -E '^[0-9]+\.'
";
        var res = await ssh.RunScriptAsync(probe, log, ct).ConfigureAwait(false);
        if (!res.Success)
            throw new InvalidOperationException("系统探测失败：" + (res.Error ?? res.Output));

        return ParseOsInfo(res.Output);
    }

    internal static ServerOsInfo ParseOsInfo(string output)
    {
        string id = "", ver = "", pretty = "", arch = "", kernel = "";
        int bits = 0;
        bool docker = false, compose = false;
        var subnets = new List<string>();
        string section = "";

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line == "__OS__") { section = "os"; continue; }
            if (line == "__DOCKER__") { section = "docker"; continue; }
            if (line == "__SUBNETS__") { section = "sub"; continue; }

            switch (section)
            {
                case "os":
                    if (line.StartsWith("ID=")) id = line[3..].Trim('"');
                    else if (line.StartsWith("VERSION_ID=")) ver = line["VERSION_ID=".Length..].Trim('"');
                    else if (line.StartsWith("PRETTY=")) pretty = line["PRETTY=".Length..].Trim('"');
                    else if (line.StartsWith("ARCH=")) arch = line["ARCH=".Length..];
                    else if (line.StartsWith("BITS=")) int.TryParse(line["BITS=".Length..], out bits);
                    else if (line.StartsWith("KERNEL=")) kernel = line["KERNEL=".Length..];
                    break;
                case "docker":
                    if (line.Contains("Docker version") || line.Contains("docker-compose"))
                    {
                        if (line.StartsWith("Docker version")) docker = true;
                        else compose = true;
                    }
                    else if (line.Contains("compose")) compose = true;
                    break;
                case "sub":
                    if (line.Contains('/') && char.IsDigit(line[0])) subnets.Add(line);
                    break;
            }
        }

        // NO_DOCKER / NO_COMPOSE 标记兜底
        if (output.Contains("NO_DOCKER")) docker = false;
        if (output.Contains("NO_COMPOSE")) compose = false;
        if (id.Length == 0) id = "linux";

        // getconf 取不到位宽时，按架构名兜底推断 32/64 位
        if (bits is not (32 or 64))
        {
            bits = arch switch
            {
                "x86_64" or "amd64" or "aarch64" or "arm64" => 64,
                "i386" or "i686" or "i586" or "armv7l" or "armv7" or "armhf" or "armv6l" or "armv6" => 32,
                _ => 0,
            };
        }

        var family = DebianIds.Contains(id) ? LinuxFamily.Debian
            : RhelIds.Contains(id) ? LinuxFamily.Rhel
            : SuseIds.Contains(id) ? LinuxFamily.Suse
            : LinuxFamily.Unknown;

        return new ServerOsInfo(pretty.Length == 0 ? id : pretty, id, ver, arch, kernel, bits,
            family, docker, compose, subnets);
    }

    // ---------- 2) 安全地址池计算 ----------

    /// <summary>从候选池里挑一个与服务器现有子网都不冲突的 /16。</summary>
    internal static string PickSafePool(IReadOnlyList<string> existingSubnets)
    {
        var taken = existingSubnets
            .Select(TryParseCidr)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToList();

        foreach (var cand in PoolCandidates)
        {
            var c = TryParseCidr(cand);
            if (c is null) continue;
            if (!taken.Any(t => RangesOverlap(c.Value, t)))
                return cand;
        }
        return PoolCandidates[0]; // 理论上到不了这里
    }

    private static bool RangesOverlap((uint Start, uint End) a, (uint Start, uint End) b) =>
        a.Start <= b.End && b.Start <= a.End;

    private static (uint Start, uint End)? TryParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix)) return null;
        if (prefix < 0 || prefix > 32) return null;
        var ip = IpToUint(parts[0]);
        if (ip is null) return null;
        var mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        var start = ip.Value & mask;
        var end = start | ~mask;
        return (start, end);
    }

    private static uint? IpToUint(string ip)
    {
        var oct = ip.Split('.');
        if (oct.Length != 4) return null;
        uint result = 0;
        foreach (var o in oct)
        {
            if (!byte.TryParse(o, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)) return null;
            result = (result << 8) | b;
        }
        return result;
    }

    /// <summary>
    /// 收集本机（客户端）所有活动网卡的 IPv4 子网（CIDR）。Docker 网桥若落在客户端网段，
    /// 会把服务器发往客户端的回包吸进网桥导致 SSH 失联——所以选地址池时必须一并避开。
    /// </summary>
    internal static List<string> GetLocalSubnets()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var maskBytes = (ua.IPv4Mask?.GetAddressBytes()) ?? new byte[] { 255, 255, 255, 0 };
                    var prefix = 0;
                    foreach (var b in maskBytes)
                    {
                        var x = b;
                        while ((x & 0x80) != 0) { prefix++; x <<= 1; }
                    }
                    result.Add($"{ua.Address}/{prefix}");
                }
            }
        }
        catch { /* 取不到就用服务器侧子网兜底 */ }
        return result;
    }

    // ---------- 3) 主部署流程 ----------

    public async Task<ProvisionResult> ProvisionAsync(
        SshService ssh, SshConfig cfg, ServerOsInfo os,
        Action<string> log, IProgress<int>? progress, CancellationToken ct)
    {
        if (os.Family == LinuxFamily.Unknown)
            return ProvisionResult.Fail("系统识别",
                $"无法识别的发行版（ID={os.Id}），暂不支持自动装 Docker。支持范围见「支持的服务器系统」列表" +
                "（Debian/Ubuntu/Deepin/UOS、CentOS/RHEL/Rocky/Alma/openEuler/麒麟、openSUSE/SLES）。" +
                "也可手动安装 Docker 后重试。");
        if (!os.DockerArchSupported)
            return ProvisionResult.Fail("架构检查",
                $"不支持的 CPU 架构：{os.Arch}（{os.BitnessLabel}）。Docker 官方仅提供 x86_64 / ARM64 / ARM32(armhf) 构建。");
        if (!os.CompileImageSupported)
            return ProvisionResult.Fail("架构检查",
                $"服务器为 32 位（{os.Arch}）。32 位系统可以安装并管理 Docker，但 APK 编译镜像" +
                "（ubuntu:22.04 + Android SDK/NDK）仅提供 64 位，无法在 32 位服务器上编译 APK。请改用 64 位服务器。");

        var localSubnets = GetLocalSubnets();
        var avoid = new List<string>(os.ExistingSubnets);
        avoid.AddRange(localSubnets);
        var pool = PickSafePool(avoid);
        log($"[网络] 服务器现有子网：{(os.ExistingSubnets.Count == 0 ? "(无)" : string.Join(", ", os.ExistingSubnets))}");
        log($"[网络] 本机(客户端)子网：{(localSubnets.Count == 0 ? "(无)" : string.Join(", ", localSubnets))}");
        log($"[网络] 为 Docker 选定不冲突的地址池：{pool}（size=24）—— 同时避开服务器与客户端网段，防止网桥抢占路由导致 SSH 失联。");

        // 3a) 安装 Docker（若缺失）
        if (!os.DockerInstalled)
        {
            log("[1/5] 安装 Docker Engine（按发行版自动选择 apt / yum / zypper 源）……");
            progress?.Report(10);
            var install = os.Family switch
            {
                LinuxFamily.Debian => DebianInstallScript(),
                LinuxFamily.Suse => SuseInstallScript(),
                _ => RhelInstallScript(os.Id),
            };
            var r = await ssh.RunScriptAsync(install, log, ct).ConfigureAwait(false);
            if (!r.Success)
                return ProvisionResult.Fail("安装 Docker", $"Docker 安装失败（退出码 {r.ExitCode}）：{r.Error ?? Tail(r.Output)}");
        }
        else
        {
            log("[1/5] 检测到 Docker 已安装，跳过安装。");
            progress?.Report(15);
        }

        // 3b) 写 daemon.json（registry 镜像 + 安全地址池）并重启
        log("[2/5] 写入 /etc/docker/daemon.json（镜像加速 + 安全地址池）并重启 Docker……");
        progress?.Report(25);
        var daemon = DaemonScript(pool);
        var rd = await ssh.RunScriptAsync(daemon, log, ct).ConfigureAwait(false);
        if (!rd.Success)
            return ProvisionResult.Fail("配置 Docker", $"Docker 配置/启动失败（退出码 {rd.ExitCode}）：{rd.Error ?? Tail(rd.Output)}");

        // 3c) 上传服务端文件
        log("[3/5] 上传 APK 编译服务端文件……");
        progress?.Report(40);
        var localDir = LocateServerAssets(out var missing);
        if (localDir is null)
            return ProvisionResult.Fail("上传文件", $"未找到随程序分发的 apk_server 资源目录（应在 {missing}）。");
        var files = new[] { "Dockerfile", "server.py", "requirements.txt", "docker-compose.yml", ".dockerignore" }
            .Select(f => (LocalPath: Path.Combine(localDir, f), FileName: f))
            .Where(t => File.Exists(t.LocalPath))
            .ToList();
        if (files.Count == 0)
            return ProvisionResult.Fail("上传文件", $"apk_server 目录为空：{localDir}");
        try
        {
            var n = await ssh.UploadFilesAsync(cfg.RemoteDir, files, log, ct).ConfigureAwait(false);
            log($"[上传] 完成，共 {n} 个文件 → {cfg.RemoteDir}");
        }
        catch (Exception ex)
        {
            return ProvisionResult.Fail("上传文件", "SFTP 上传失败：" + ex.Message);
        }

        // 3d) 构建并启动
        log("[4/5] 构建镜像并启动服务（首次构建会下载 Android SDK/NDK，约 5~15 分钟，请耐心等待）……");
        progress?.Report(50);
        var build = BuildScript(cfg.RemoteDir, cfg.ServicePort);
        var doneSteps = 0;
        var rb = await ssh.RunScriptAsync(build, l =>
        {
            log(l);
            if (l.Contains("DONE") && progress is not null)
                progress.Report(Math.Min(90, 50 + ++doneSteps));
        }, ct, idleTimeoutSec: 3600).ConfigureAwait(false);
        if (!rb.Success)
            return ProvisionResult.Fail("构建启动", $"镜像构建/启动失败（退出码 {rb.ExitCode}）：{rb.Error ?? Tail(rb.Output)}");

        // 3e) 健康检查
        log("[5/5] 健康检查……");
        progress?.Report(92);
        var health = await ssh.RunCommandAsync(
            $"for i in 1 2 3 4 5 6; do curl -fsS http://localhost:{cfg.ServicePort}/api/health && break; sleep 3; done",
            log, ct).ConfigureAwait(false);
        if (!health.Success || !health.Output.Contains("\"ok\""))
            return ProvisionResult.Fail("健康检查",
                $"服务未在 {cfg.ServicePort} 端口就绪。日志：{Tail(health.Output)}（可用 docker logs 排查）");

        progress?.Report(100);
        var url = cfg.CloudUrl;
        log($"[完成] APK 编译服务已就绪：{url}");
        return ProvisionResult.Ok(url);
    }

    private static string? LocateServerAssets(out string expected)
    {
        expected = Path.Combine(AppContext.BaseDirectory, "apk_server");
        if (Directory.Exists(expected) && File.Exists(Path.Combine(expected, "Dockerfile")))
            return expected;
        // 开发态回退：源码树的 scripts/apk_server
        var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "apk_server"));
        if (Directory.Exists(dev) && File.Exists(Path.Combine(dev, "Dockerfile")))
            return dev;
        return null;
    }

    private static string Tail(string s, int n = 500) =>
        s.Length <= n ? s : "…" + s[^n..];

    // ---------- 远程脚本 ----------

    private static string DebianInstallScript() => @"
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y ca-certificates curl gnupg
install -m 0755 -d /etc/apt/keyrings
. /etc/os-release
DISTRO=$ID
[ ""$DISTRO"" = ""linuxmint"" ] && DISTRO=ubuntu
[ -z ""$VERSION_CODENAME"" ] && VERSION_CODENAME=$(lsb_release -cs 2>/dev/null || echo jammy)
curl -fsSL https://mirrors.aliyun.com/docker-ce/linux/$DISTRO/gpg | gpg --dearmor --yes -o /etc/apt/keyrings/docker.gpg
chmod a+r /etc/apt/keyrings/docker.gpg
echo ""deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://mirrors.aliyun.com/docker-ce/linux/$DISTRO $VERSION_CODENAME stable"" > /etc/apt/sources.list.d/docker.list
apt-get update -y
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
docker --version
";

    private static string RhelInstallScript(string id) => @"
set -e
PKG=yum
command -v dnf >/dev/null 2>&1 && PKG=dnf
$PKG install -y yum-utils device-mapper-persistent-data lvm2
yum-config-manager --add-repo https://mirrors.aliyun.com/docker-ce/linux/centos/docker-ce.repo
$PKG install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
docker --version
";

    private static string SuseInstallScript() => @"
set -e
zypper --non-interactive install ca-certificates curl
zypper --non-interactive addrepo --refresh https://mirrors.aliyun.com/docker-ce/linux/sles/docker-ce.repo || \
  zypper --non-interactive addrepo --refresh https://download.docker.com/linux/sles/docker-ce.repo
zypper --non-interactive refresh
zypper --non-interactive install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin || \
  zypper --non-interactive install docker docker-compose
docker --version
";

    private static string DaemonScript(string pool) => $@"
set -e
mkdir -p /etc/docker
cat > /etc/docker/daemon.json <<'JSON'
{{
  ""registry-mirrors"": [""https://docker.m.daocloud.io"", ""https://dockerproxy.com""],
  ""default-address-pools"": [{{ ""base"": ""{pool}"", ""size"": 24 }}],
  ""log-driver"": ""json-file"",
  ""log-opts"": {{ ""max-size"": ""20m"", ""max-file"": ""3"" }}
}}
JSON
echo '--- /etc/docker/daemon.json ---'
cat /etc/docker/daemon.json
systemctl enable docker >/dev/null 2>&1 || true
systemctl restart docker
sleep 3
systemctl is-active docker
docker info --format 'Docker {{.ServerVersion}} | pools ok' 2>/dev/null || docker --version
";

    private static string BuildScript(string remoteDir, int servicePort) => $@"
set -e
cd {remoteDir}
# compose v2 优先；无插件则回退到 docker-compose
if docker compose version >/dev/null 2>&1; then DC='docker compose';
elif command -v docker-compose >/dev/null 2>&1; then DC='docker-compose';
else echo 'NO_COMPOSE_TOOL'; exit 3; fi
echo ""USING=$DC""
SERVICE_PORT={servicePort} $DC up -d --build
echo '--- ps ---'
$DC ps
";
}
