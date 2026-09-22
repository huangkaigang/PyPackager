# PyPackager 云端 APK 编译服务 —— turnkey 镜像
# 基于 Ubuntu 22.04，预装：JDK17 + Android SDK(cmdline-tools/platform-tools/platforms/build-tools)
# + NDK 25b(25.2.9519653) + buildozer。镜像构建完即“开箱可编译”，首次编译无需再下载 SDK/NDK。
#
# 构建：  docker build -t pypackager-apk-server ./scripts/apk_server
# 运行：  docker run -d --name apk -p 8000:8000 \
#            -v pypackager_buildozer:/root/.buildozer \
#            pypackager-apk-server
#
# 体积提示：预置 SDK/NDK 后镜像约 3~5 GB，属正常；换来的是服务器部署即编译、首个任务不卡下载。

FROM ubuntu:22.04

# SDK/NDK 装到固定路径，并通过环境变量告诉 buildozer 直接用，别再自己下载到 ~/.buildozer。
ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    ANDROID_SDK_ROOT=/opt/android-sdk \
    ANDROIDSDK=/opt/android-sdk \
    ANDROID_HOME=/opt/android-sdk \
    ANDROIDNDK=/opt/android-sdk/ndk/25.2.9519653 \
    NDK_HOME=/opt/android-sdk/ndk/25.2.9519653 \
    CMDLINE_TOOLS_VER=11076708

# pip 源默认走清华镜像（国内服务器实测比 pypi.org 稳/快）；如需官方源，构建时覆盖：
#   docker build --build-arg PIP_INDEX_URL=https://pypi.org/simple --build-arg PIP_TRUSTED_HOST=pypi.org ...
ARG PIP_INDEX_URL=https://pypi.tuna.tsinghua.edu.cn/simple
ARG PIP_TRUSTED_HOST=pypi.tuna.tsinghua.edu.cn
ENV PIP_INDEX_URL=${PIP_INDEX_URL} \
    PIP_TRUSTED_HOST=${PIP_TRUSTED_HOST}

# 系统依赖 + JDK17（buildozer/python-for-android 编译所需）
RUN apt-get update && apt-get install -y --no-install-recommends \
        git zip unzip ca-certificates curl wget file \
        openjdk-17-jdk autoconf libtool libltdl-dev pkg-config \
        build-essential automake cmake \
        python3 python3-pip python3-venv python3-dev \
        libssl-dev libffi-dev zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

# Android SDK：cmdline-tools + platform-tools + platforms + build-tools + NDK 25b，并接受许可。
RUN mkdir -p /opt/android-sdk/cmdline-tools \
    && cd /opt/android-sdk/cmdline-tools \
    && curl -fL -o tools.zip \
        "https://dl.google.com/android/repository/commandlinetools-linux-${CMDLINE_TOOLS_VER}_latest.zip" \
    && unzip -q tools.zip \
    && mv cmdline-tools latest \
    && rm -f tools.zip \
    && yes | /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager --licenses > /dev/null || true \
    && /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager --install \
        "platform-tools" \
        "platforms;android-33" \
        "platforms;android-34" \
        "build-tools;33.0.1" \
        "build-tools;30.0.3" \
        "ndk;25.2.9519653" \
    && rm -rf /opt/android-sdk/.downloadIntermediates

WORKDIR /app

# Web 层依赖（requirements.txt）+ 编译层依赖（buildozer / cython）分开装，便于按需更新。
COPY requirements.txt /app/requirements.txt
RUN python3 -m pip install --no-cache-dir --upgrade pip setuptools wheel \
    && python3 -m pip install --no-cache-dir -r /app/requirements.txt \
    && python3 -m pip install --no-cache-dir "buildozer>=1.5.0" "cython==0.29.36"

COPY server.py /app/server.py

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8000/api/health || exit 1

# 单 worker：编译是 CPU/IO 密集的后台线程，多 worker 会争抢同一份缓存目录，不要加 --workers。
CMD ["python3", "-m", "uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8000"]
