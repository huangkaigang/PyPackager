#!/usr/bin/env python3
"""PyPackager 云端 APK 编译参考服务端。

这是一个**可直接部署**的服务端实现：PyPackager 客户端「云端服务器打包」后端所约定的 HTTP
契约。推荐用同目录的 Dockerfile 构建镜像（已预装 JDK17 + Android SDK/NDK + buildozer），
放到任意 Linux 服务器 / Docker 主机上运行即可。

契约（与 src/PyPackager/Services/ApkCloudClient.cs 对应）：
  GET  /api/health            -> 200 {"ok": true}
  POST /api/build             (multipart: file=<项目zip>, appName, packageName, version,
                               entry, permissions, framework, minApi, targetApi, archs)
                              -> 202 {"jobId": "..."}
  GET  /api/status/{jobId}    -> {"state": "queued|building|done|error",
                                  "progress": 0-100, "log": "...", "apkUrl": "...", "error": "..."}
  GET  /api/download/{jobId}  -> APK 二进制

说明：
  · 编译是重活，同一时刻只跑一个 buildozer（全局构建锁），其余任务排队(state=queued)，
    避免并发争抢同一份 ~/.buildozer 缓存导致损坏。
  · 本服务**无鉴权**，会在服务器上执行编译命令。请只在受信任网络使用，或前置反向代理鉴权。
"""
from __future__ import annotations

import glob
import os
import shutil
import subprocess
import threading
import uuid
import zipfile
from pathlib import Path

from fastapi import FastAPI, File, Form, UploadFile
from fastapi.responses import FileResponse, JSONResponse

# 工作根目录：每个任务一个子目录。可用环境变量 APK_WORKDIR 覆盖。
WORKDIR = Path(os.environ.get("APK_WORKDIR", "/tmp/pypackager_jobs"))
WORKDIR.mkdir(parents=True, exist_ok=True)

app = FastAPI(title="PyPackager APK Build Server", version="1.1")

# 简单内存任务表（重启即丢失，参考实现足够）。
JOBS: dict[str, dict] = {}
_LOCK = threading.Lock()        # 保护 JOBS 读写
_BUILD_LOCK = threading.Lock()  # 全局构建串行化：同一时刻只跑一个 buildozer


def _set(job_id: str, **fields) -> None:
    with _LOCK:
        JOBS.setdefault(job_id, {}).update(fields)


def _get(job_id: str) -> dict | None:
    with _LOCK:
        job = JOBS.get(job_id)
        return dict(job) if job else None


def _safe_extract(zip_path: Path, dest: Path) -> None:
    """解压 zip，并拒绝任何试图写到 dest 之外的条目（防 zip-slip）。"""
    dest = dest.resolve()
    with zipfile.ZipFile(zip_path) as zf:
        for member in zf.infolist():
            target = (dest / member.filename).resolve()
            if target != dest and not str(target).startswith(str(dest) + os.sep):
                raise ValueError(f"非法压缩包路径(疑似 zip-slip)：{member.filename}")
        zf.extractall(dest)


@app.get("/api/health")
def health() -> JSONResponse:
    return JSONResponse({"ok": True})


@app.post("/api/build", status_code=202)
async def build(
    file: UploadFile = File(...),
    appName: str = Form("MyApp"),
    packageName: str = Form("com.example.myapp"),
    version: str = Form("0.1"),
    entry: str = Form("main.py"),
    permissions: str = Form("INTERNET"),
    framework: str = Form("kivy"),
    minApi: str = Form("21"),
    targetApi: str = Form("33"),
    archs: str = Form("arm64-v8a, armeabi-v7a"),
) -> JSONResponse:
    job_id = uuid.uuid4().hex[:12]
    job_dir = WORKDIR / job_id
    proj_dir = job_dir / "project"
    proj_dir.mkdir(parents=True, exist_ok=True)

    zip_path = job_dir / "project.zip"
    with zip_path.open("wb") as f:
        shutil.copyfileobj(file.file, f)

    try:
        _safe_extract(zip_path, proj_dir)
    except Exception as exc:  # noqa: BLE001
        _set(job_id, state="error", progress=0, error=f"解压项目失败：{exc}", project=str(proj_dir))
        return JSONResponse({"jobId": job_id}, status_code=202)

    _set(job_id, state="queued", progress=0, log="", error="",
         project=str(proj_dir), apk="", appName=appName)

    threading.Thread(
        target=_run_build,
        args=(job_id, proj_dir, framework, minApi, targetApi, archs),
        daemon=True,
    ).start()

    return JSONResponse({"jobId": job_id}, status_code=202)


def _run_build(job_id: str, proj_dir: Path, framework: str,
               minApi: str, targetApi: str, archs: str) -> None:
    """在后台线程里排队并执行 buildozer android debug，把进度写回任务表。"""
    # 排队：拿到构建锁前保持 queued
    _set(job_id, log="排队中，等待空闲的构建槽……")
    with _BUILD_LOCK:
        try:
            _set(job_id, state="building", progress=5, log="准备 buildozer 环境……")

            # 若客户端已上传 buildozer.spec 则直接复用；否则用表单参数生成一个最小 spec。
            spec = proj_dir / "buildozer.spec"
            if not spec.exists():
                spec.write_text(_minimal_spec(framework, minApi, targetApi, archs),
                                encoding="utf-8")

            env = dict(os.environ)
            # 镜像里已预置 ANDROIDSDK/ANDROIDNDK；这里仅在缺失时兜底指向 buildozer 默认缓存。
            env.setdefault("ANDROIDSDK", env.get("ANDROID_HOME",
                           str(Path.home() / ".buildozer/android/platform/android-sdk")))
            env.setdefault("ANDROID_HOME", env["ANDROIDSDK"])

            _set(job_id, progress=15, log="执行：buildozer android debug")
            proc = subprocess.Popen(
                ["buildozer", "android", "debug"],
                cwd=str(proj_dir),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                env=env,
                bufsize=1,
            )

            assert proc.stdout is not None
            # buildozer 无结构化进度，用输出行数粗略推进 15%->90%。
            lines = 0
            last_tail = ""
            for line in proc.stdout:
                lines += 1
                last_tail = line.strip()[:400]
                pct = min(90, 15 + lines // 20)
                _set(job_id, progress=pct, log=last_tail)
            proc.wait()

            if proc.returncode != 0:
                _set(job_id, state="error", progress=0,
                     error=f"buildozer 退出码 {proc.returncode}：{last_tail}")
                return

            apks = sorted(glob.glob(str(proj_dir / "bin" / "*.apk")),
                          key=os.path.getmtime, reverse=True)
            if not apks:
                _set(job_id, state="error", error="编译结束但未找到 bin/*.apk 产物")
                return

            _set(job_id, state="done", progress=100, apk=apks[0],
                 apkUrl=f"/api/download/{job_id}", log="编译完成")
        except Exception as exc:  # noqa: BLE001 - 参考实现，兜底记录
            _set(job_id, state="error", progress=0, error=f"服务端异常：{exc}")


def _minimal_spec(framework: str, minApi: str, targetApi: str, archs: str) -> str:
    reqs = "python3,flet,kivy" if framework == "flet" else "python3,kivy"
    return f"""[app]
title = MyApp
package.name = myapp
package.domain = org.example
source.dir = .
source.main = main.py
version = 0.1
requirements = {reqs}
permissions = INTERNET
orientation = portrait
android.api = {targetApi}
android.minapi = {minApi}
android.archs = {archs}
android.accept_sdk_license = True
build.mode = debug

[buildozer]
log_level = 2
warn_on_root = 1
"""


@app.get("/api/status/{job_id}")
def status(job_id: str) -> JSONResponse:
    job = _get(job_id)
    if job is None:
        return JSONResponse({"error": "unknown jobId"}, status_code=404)
    return JSONResponse({
        "state": job.get("state", "queued"),
        "progress": job.get("progress", 0),
        "log": job.get("log", ""),
        "apkUrl": job.get("apkUrl", ""),
        "error": job.get("error", ""),
    })


@app.get("/api/download/{job_id}")
def download(job_id: str):
    job = _get(job_id)
    if job is None or not job.get("apk") or not Path(job["apk"]).exists():
        return JSONResponse({"error": "apk not ready"}, status_code=404)
    return FileResponse(job["apk"], media_type="application/vnd.android.package-archive",
                        filename=Path(job["apk"]).name)
