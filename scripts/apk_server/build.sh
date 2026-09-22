#!/usr/bin/env bash
# PyPackager APK 服务端：一键构建 + 运行。
# 用法：
#   ./build.sh            # 构建镜像并用 docker run 启动（前台日志用 Ctrl+C 退出容器仍在后台）
#   ./build.sh compose    # 用 docker compose 构建并后台启动
set -euo pipefail

IMAGE="pypackager-apk-server:latest"
CONTAINER="pypackager-apk"
PORT="8000"
cd "$(dirname "$0")"

if [[ "${1:-}" == "compose" ]]; then
  echo "==> docker compose up -d --build"
  docker compose up -d --build
  echo "==> 已启动。健康检查： curl http://localhost:${PORT}/api/health"
  exit 0
fi

echo "==> 构建镜像 ${IMAGE}（首次会下载 Android SDK/NDK，较慢，镜像约 3~5GB）"
docker build -t "${IMAGE}" .

echo "==> 若已有同名容器则移除"
docker rm -f "${CONTAINER}" 2>/dev/null || true

echo "==> 运行容器 ${CONTAINER}（端口 ${PORT}，持久化 buildozer 缓存卷）"
docker run -d \
  --name "${CONTAINER}" \
  --restart unless-stopped \
  -p "${PORT}:8000" \
  -v pypackager_buildozer:/root/.buildozer \
  "${IMAGE}"

echo "==> 完成。查看日志： docker logs -f ${CONTAINER}"
echo "==> 健康检查：       curl http://localhost:${PORT}/api/health"
