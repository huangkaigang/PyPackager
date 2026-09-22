# PyPackager 云端 APK 编译服务（turnkey 镜像）

这是 PyPackager 客户端「云端服务器打包」后端所对接的**可直接部署服务端**。镜像里已预装
JDK17 + Android SDK/NDK 25b + buildozer，**构建完即可编译**，首次任务无需再联网下载 SDK/NDK。
把它放到任意 Linux / Docker 主机上跑起来，客户端填上服务器地址即可上传项目、云端编译、下载 APK。

> ⚠️ 本参考实现**不含鉴权**，且会在服务器上执行编译命令。请只在受信任网络中使用，或在其前面
> 加一层反向代理鉴权（nginx basic auth / API Key / mTLS）。切勿裸奔暴露到公网。

## 目录内容

- `server.py` —— FastAPI 服务端，实现上传→排队→编译→查询进度→下载 APK 的完整契约。
- `Dockerfile` —— turnkey 镜像：预装 JDK17 + Android SDK(cmdline-tools/platform-tools/
  platforms;android-33,34/build-tools;33.0.1,30.0.3) + NDK 25.2.9519653 + buildozer，带 HEALTHCHECK。
- `requirements.txt` —— Web 层依赖（fastapi/uvicorn/python-multipart）；buildozer/cython 由 Dockerfile 装。
- `docker-compose.yml` —— 一键起服务 + 持久化 buildozer 缓存卷 + healthcheck。
- `build.sh` —— 构建并运行的便捷脚本（`./build.sh` 或 `./build.sh compose`）。
- `.dockerignore` —— 精简构建上下文。

## 快速开始（服务器上）

把整个 `scripts/apk_server/` 目录拷到服务器，任选一种方式：

```bash
cd apk_server

# 方式 A：脚本一键构建 + 运行（后台）
chmod +x build.sh && ./build.sh

# 方式 B：docker compose（推荐，带重启策略/healthcheck/缓存卷）
docker compose up -d --build

# 方式 C：纯 docker 命令
docker build -t pypackager-apk-server .
docker run -d --name pypackager-apk --restart unless-stopped \
  -p 8000:8000 -v pypackager_buildozer:/root/.buildozer \
  pypackager-apk-server
```

> 首次 `docker build` 会从 Google 源下载 Android SDK/NDK，镜像约 **3~5 GB**、构建较慢，属正常。
> 命名卷 `pypackager_buildozer` 持久化 buildozer 的全局包缓存，重建容器后仍可复用、加速编译。

服务地址即 `http://<服务器IP>:8000`，填到客户端 APK 页的「云端服务器地址」。

## 验证（curl 冒烟）

```bash
# 1) 健康检查
curl http://localhost:8000/api/health          # -> {"ok":true}

# 2) 提交一个编译任务（把 project.zip 换成你的项目压缩包）
curl -X POST http://localhost:8000/api/build \
  -F "file=@project.zip" -F "appName=Demo" -F "packageName=com.example.demo" \
  -F "version=0.1" -F "entry=main.py" -F "framework=kivy" \
  -F "minApi=21" -F "targetApi=33" -F "archs=arm64-v8a, armeabi-v7a"
# -> {"jobId":"xxxxxxxxxxxx"}

# 3) 轮询进度
curl http://localhost:8000/api/status/<jobId>   # -> {state, progress, log, apkUrl, error}

# 4) 下载产物（state=done 后）
curl -o app.apk http://localhost:8000/api/download/<jobId>
```

## HTTP 契约

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/health` | 健康检查，返回 `{"ok": true}` |
| POST | `/api/build` | multipart：`file`(项目zip) + `appName/packageName/version/entry/permissions/framework/minApi/targetApi/archs`；返回 `202 {"jobId": "..."}` |
| GET | `/api/status/{jobId}` | 返回 `{state, progress, log, apkUrl, error}`，`state ∈ queued/building/done/error` |
| GET | `/api/download/{jobId}` | 下载编译好的 APK 二进制 |

客户端对应实现见 `src/PyPackager/Services/ApkCloudClient.cs`。若你要接入自己的编译集群，
只要实现同样这四个端点即可，无需改动客户端。

## 编译说明

- 同一时刻只跑一个 buildozer（服务端全局构建锁）；并发任务保持 `state=queued` 排队，避免争抢
  同一份 `~/.buildozer` 缓存导致损坏。
- 服务端优先使用客户端上传的 `buildozer.spec`（客户端「生成模板」按钮产出，已含 minSdk/targetSdk/ABI）。
  若项目里没有 spec，则用表单参数生成一个最小可用 spec 兜底。
- 镜像已把 SDK/NDK 装到 `/opt/android-sdk` 并通过 `ANDROIDSDK/ANDROIDNDK/ANDROID_HOME` 环境变量
  告知 buildozer 直接使用；buildozer 仍会按需下载各 Python 依赖包（kivy/flet 等）到缓存卷，首次编译
  这部分耗时可能 10~30 分钟，请确保服务器能访问 PyPI / Maven 等下载源。
- 上传的 zip 解压有 zip-slip 防护（拒绝写到项目目录之外的条目）。

## 裸机 Linux（不用 Docker，Ubuntu 22.04）

```bash
sudo apt-get update
sudo apt-get install -y git zip unzip openjdk-17-jdk autoconf libtool libltdl-dev \
    pkg-config build-essential automake cmake python3-pip python3-venv python3-dev \
    libssl-dev libffi-dev zlib1g-dev

cd scripts/apk_server
python3 -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt "buildozer>=1.5.0" "cython==0.29.36"

uvicorn server:app --host 0.0.0.0 --port 8000
```

> 裸机方式需自行安装 Android SDK/NDK 并设置 `ANDROIDSDK/ANDROIDNDK` 环境变量，否则 buildozer
> 首次编译会自行下载。生产部署推荐直接用上面的 Docker 方式。
