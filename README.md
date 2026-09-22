# PyPackager

> Python 一键打包工具：把 Python 项目打包成 **Windows EXE** 与 **Android APK** 的 Windows 桌面工具。

C# WPF（.NET 9）客户端 + Python 打包引擎 + 可选的云端 APK 编译服务（Docker），开箱即用。

## 特性

- **EXE 打包**：PyInstaller / Nuitka 双引擎；支持图标、版本号、数据文件、隐藏导入；可选目标系统（Win7/10/11）与架构（x86/x64），自动下载对应嵌入式 Python 运行时。
- **APK 打包**：本地 WSL2(Ubuntu) buildozer 为主，云端 Linux 服务器编译兜底；打包前自动做依赖 / UI 框架兼容性预检。
- **服务器一键部署**：填入 SSH 登录信息后自动识别系统版本（Debian/RHEL/SUSE 系，含 32/64 位判定）→ 安装并安全配置 Docker（自动避开网段冲突）→ 上传并构建 APK 编译服务 → 健康检查 → 地址回填。
- **环境自管理**：内部虚拟环境自动创建、PyInstaller/Nuitka 自动安装、pip 镜像可切换、托管 Python 运行时下载管理。
- **批量打包 / 打包历史**：多项目批量出包，历史记录可查。
- **凭据安全**：SSH 密码/口令经 Windows DPAPI 加密后存于 `%LOCALAPPDATA%\PyPackager`，不入库、不明文。

## 目录结构

```
├─ PyPackager.sln
├─ src/PyPackager/            # C# WPF 客户端（.NET 9, net9.0-windows）
│   ├─ Views/                 # 各功能页（EXE/APK/批量/历史/环境/服务器部署）
│   ├─ Services/              # 打包引擎调用、venv、运行时、SSH、云端客户端等
│   └─ Models/                # 参数/配置/探测结果模型
├─ scripts/
│   ├─ build_exe.py           # PyInstaller 引擎
│   ├─ build_nuitka.py        # Nuitka 引擎
│   └─ apk_server/            # 云端 APK 编译服务（FastAPI + buildozer，Docker 化）
└─ samples/hello/             # 用于验证打包流程的样例项目
```

## 环境要求

- Windows 10/11 x64
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)（构建客户端）
- Python 3.8+（用于创建内部虚拟环境；缺省时程序会引导安装）
- 可选：WSL2(Ubuntu) 用于本地 APK 编译；或一台 Linux 服务器用于云端 APK 编译

## 构建与运行

```bash
git clone <repo-url>
cd python代码生成exe和apk工具
dotnet run --project src/PyPackager
```

或发布单文件：

```bash
dotnet publish src/PyPackager -c Release -r win-x64 --self-contained false
```

首次运行后，在「环境管理」页点「初始化内部虚拟环境」即可自动装好 PyInstaller 与 Nuitka。

## 使用流程

1. **EXE**：选项目目录 → 选引擎（PyInstaller/Nuitka）与目标系统/架构 → 打包。
2. **APK（本地）**：「APK 打包」页点「一键开启本地打包环境」（自动装 WSL2 + JDK + SDK + buildozer）→ 打包。
3. **APK（云端）**：在「服务器部署」页填 SSH 信息 → 「一键部署 APK 编译服务」→ 部署成功后地址自动回填到「APK 打包」页 → 选「云端服务器打包」。

## 云端 APK 编译服务

参考实现见 `scripts/apk_server/`（FastAPI + buildozer，提供 turnkey Dockerfile / docker-compose）。契约：

```
GET  /api/health            健康检查
POST /api/build             上传项目 ZIP，返回 jobId
GET  /api/status/{jobId}    轮询编译进度/日志/产物地址
GET  /api/download/{jobId}  下载 APK
```

> ⚠️ **安全提示**：该服务**无鉴权**且会执行编译命令，仅建议部署在内网/受控网络，或置于带鉴权的反向代理之后；切勿裸暴露到公网。

## 支持的服务器系统（自动部署）

- Debian 系（apt）：Ubuntu 18.04+、Debian 10+、LinuxMint、Deepin、UOS、Raspbian
- RHEL 系（yum/dnf）：CentOS 7/8/9、RHEL 7/8/9、Rocky/Alma 8/9、Fedora、openEuler、麒麟、Anolis
- SUSE 系（zypper）：openSUSE Leap/Tumbleweed、SLES 12/15
- 位宽：64 位（x86_64/ARM64）完整支持；32 位（armhf 等）仅可安装/管理 Docker，APK 编译镜像仅 64 位

## 许可证

[MIT](LICENSE)
