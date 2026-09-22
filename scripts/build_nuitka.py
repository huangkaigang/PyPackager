#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build_nuitka.py —— PyPackager 的 Nuitka 执行者（第二阶段）。

与 build_exe.py（PyInstaller 引擎）并列，由 C# 外壳通过命令行调用，
负责把 Python 项目用 Nuitka 编译成 Windows EXE。

Nuitka 相比 PyInstaller 的优势：真正编译为 C 再编译成机器码，
运行更快、体积极小、且极难被反编译，适合商业软件保护。
代价：需要一个 C 编译器后端。本机若无，Nuitka 4.x 在 Windows 上默认
用 --assume-yes-for-downloads 自动下载 Zig（ziglang pip 包）作为后端；
首次下载较慢且无进度输出，缓存后续编译即恢复正常。

对外输出协议与 build_exe.py 完全一致，方便 C# 端复用解析逻辑：
    [LOG] / [PROGRESS] / [RESULT] / [ERROR]
"""

from __future__ import annotations

import argparse
import importlib.util
import subprocess
import sys
from pathlib import Path


def emit(kind: str, message: str) -> None:
    safe = str(message).replace("\r", " ").replace("\n", " ").strip()
    sys.stdout.write(f"[{kind}] {safe}\n")
    sys.stdout.flush()


def emit_progress(value: int) -> None:
    emit("PROGRESS", max(0, min(100, int(value))))


def ensure_utf8_stdio() -> None:
    for stream_name in ("stdout", "stderr"):
        stream = getattr(sys, stream_name, None)
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except Exception:
                pass


def nuitka_installed() -> bool:
    return importlib.util.find_spec("nuitka") is not None


def install_nuitka() -> bool:
    emit("LOG", "未检测到 Nuitka，正在通过 pip 自动安装……")
    cmd = [sys.executable, "-m", "pip", "install", "--upgrade", "nuitka"]
    try:
        proc = subprocess.run(
            cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace",
        )
    except Exception as exc:  # noqa: BLE001
        emit("ERROR", f"调用 pip 失败：{exc}")
        return False

    for line in (proc.stdout or "").splitlines():
        if line.strip():
            emit("LOG", line.strip())

    if proc.returncode != 0:
        emit("ERROR", "Nuitka 安装失败，请检查网络或 pip 源配置。")
        return False

    emit("LOG", "Nuitka 安装完成。")
    return True


def find_entry(project: Path, requested: str | None) -> Path | None:
    if requested:
        candidate = (project / requested).resolve() if not Path(requested).is_absolute() else Path(requested)
        if candidate.exists():
            return candidate
        emit("ERROR", f"指定的入口文件不存在：{candidate}")
        return None

    for name in ["main.py", "app.py", "run.py", "start.py", "__main__.py"]:
        candidate = project / name
        if candidate.exists():
            return candidate

    py_files = sorted(p for p in project.glob("*.py") if p.is_file())
    if len(py_files) == 1:
        return py_files[0]
    if py_files:
        names = ", ".join(p.name for p in py_files[:10])
        emit("ERROR", f"项目内有多个 .py 文件，无法自动确定入口，请手动指定。候选：{names}")
        return None

    emit("ERROR", "项目目录内没有找到任何 .py 文件。")
    return None


def build_nuitka_command(args: argparse.Namespace, entry: Path, app_name: str, dist: Path) -> list[str]:
    cmd: list[str] = [
        sys.executable, "-m", "nuitka",
        # 允许 Nuitka 在缺少编译器/依赖时自动下载（如 MinGW64、ccache）
        "--assume-yes-for-downloads",
    ]

    cmd.append("--onefile" if args.onefile else "--standalone")

    # 控制台：GUI 程序禁用黑框
    cmd.append(f"--windows-console-mode={'disable' if args.noconsole else 'force'}")

    cmd.append(f"--output-filename={app_name}.exe")
    cmd.append(f"--output-dir={dist}")

    if args.icon:
        icon_path = Path(args.icon)
        if not icon_path.is_absolute():
            icon_path = Path(args.project) / icon_path
        if icon_path.exists():
            cmd.append(f"--windows-icon-from-ico={icon_path}")
        else:
            emit("LOG", f"图标文件不存在，已忽略：{icon_path}")

    # 附加数据文件：Nuitka 用 src=dest（与 PyInstaller 的 src;dest 不同）
    # 相对源路径按项目目录解析为绝对路径，避免歧义
    project = Path(args.project).resolve()
    for item in args.data or []:
        sep = ";" if ";" in item else ("=" if "=" in item else "")
        src, _, dest = item.partition(sep) if sep else (item, "", "")
        src_path = Path(src)
        if not src_path.is_absolute():
            src_path = project / src_path
        normalized = f"{src_path}={dest}" if sep else str(src_path)
        cmd.append(f"--include-data-files={normalized}")

    # 插件
    for plugin in args.plugin or []:
        cmd.append(f"--enable-plugins={plugin}")

    # 隐藏导入：Nuitka 用 --include-package 强制纳入指定包/模块
    for mod in args.include_package or []:
        cmd.append(f"--include-package={mod}")

    # 不跟随的模块
    for mod in args.nofollow or []:
        cmd.append(f"--nofollow-import-to={mod}")

    # 编译器选择
    compiler = (args.compiler or "auto").lower()
    if compiler == "mingw64":
        cmd.append("--mingw64")
    elif compiler == "msvc":
        cmd.append("--msvc=latest" if not args.msvc_version else f"--msvc={args.msvc_version}")

    # LTO 与并行度
    if args.lto:
        cmd.append(f"--lto={args.lto}")
    if args.jobs and args.jobs > 0:
        cmd.append(f"--jobs={args.jobs}")

    # 版本/产品信息（可选）
    if args.company_name:
        cmd.append(f"--company-name={args.company_name}")
    if args.product_name:
        cmd.append(f"--product-name={args.product_name}")
    if args.file_version:
        cmd.append(f"--file-version={args.file_version}")
        cmd.append(f"--product-version={args.file_version}")

    # 打包完成后清理中间 build 目录，只保留产物
    if args.remove_output:
        cmd.append("--remove-output")

    cmd.append(str(entry))
    return cmd


def locate_output_exe(dist: Path, app_name: str, onefile: bool) -> Path | None:
    """Nuitka 产物路径：onefile 直接在 dist 下；standalone 在 dist/<name>.dist/ 下。"""
    candidates = []
    if onefile:
        candidates += [
            dist / f"{app_name}.exe",
            dist / f"{app_name}.dist" / f"{app_name}.exe",
        ]
    else:
        candidates += [
            dist / f"{app_name}.dist" / f"{app_name}.exe",
            dist / f"{app_name}.exe",
        ]
    for c in candidates:
        if c.exists():
            return c

    # 兜底：在 dist 下递归找与 app_name 同名的 exe
    for found in dist.rglob(f"{app_name}.exe"):
        return found
    # 再兜底：任意 exe（standalone 可能改了名）
    for found in dist.rglob("*.exe"):
        return found
    return None


def run_build(cmd: list[str], project: Path, dist: Path, app_name: str, onefile: bool) -> bool:
    emit("LOG", "开始执行 Nuitka 编译（首次运行会自动下载 C 编译器后端 Zig，约几十 MB，期间日志可能长时间无输出，属正常现象，请耐心等待）……")
    emit("LOG", "命令：" + " ".join(f'"{c}"' if " " in c else c for c in cmd))
    emit_progress(5)

    try:
        proc = subprocess.Popen(
            cmd, cwd=str(project),
            stdin=subprocess.DEVNULL,  # 关键：杜绝任何交互提示阻塞在无人应答的 stdin 上
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", bufsize=1,
        )
    except Exception as exc:  # noqa: BLE001
        emit("ERROR", f"启动 Nuitka 进程失败：{exc}")
        return False

    progress = 10
    assert proc.stdout is not None
    for raw in proc.stdout:
        line = raw.rstrip()
        if not line:
            continue
        emit("LOG", line)
        low = line.lower()
        # Nuitka 各阶段关键词，用来推进伪进度
        if any(k in low for k in ("downloading", "installing", "scons", "compiling", "linking", "creating", "building", "onefile")):
            progress = min(92, progress + 3)
            emit_progress(progress)

    proc.wait()
    if proc.returncode != 0:
        emit("ERROR", f"Nuitka 退出码 {proc.returncode}，编译失败。")
        return False

    exe = locate_output_exe(dist, app_name, onefile)
    if exe is None:
        emit("ERROR", f"编译流程结束但未在 {dist} 下找到产物 EXE。")
        return False

    emit_progress(100)
    emit("RESULT", str(exe.resolve()))
    return True


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="PyPackager Nuitka 打包引擎")
    parser.add_argument("--project", required=True)
    parser.add_argument("--entry", default=None)
    parser.add_argument("--name", default=None)
    parser.add_argument("--icon", default=None)
    parser.add_argument("--onefile", action="store_true")
    parser.add_argument("--noconsole", action="store_true")
    parser.add_argument("--data", action="append", help="附加数据文件，src=dest 或 src;dest，可重复")
    parser.add_argument("--plugin", action="append", help="启用的 Nuitka 插件名，可重复")
    parser.add_argument("--include-package", action="append", help="强制纳入的包/模块（隐藏导入），可重复")
    parser.add_argument("--nofollow", action="append", help="不跟随导入的模块名，可重复")
    parser.add_argument("--compiler", default="auto", help="auto|mingw64|msvc")
    parser.add_argument("--msvc-version", default=None)
    parser.add_argument("--lto", default=None, help="auto|on|off")
    parser.add_argument("--jobs", type=int, default=0, help="并行 C 编译任务数，0=默认")
    parser.add_argument("--company-name", default=None)
    parser.add_argument("--product-name", default=None)
    parser.add_argument("--file-version", default=None)
    parser.add_argument("--remove-output", action="store_true", help="编译后清理中间 build 目录")
    parser.add_argument("--distpath", default=None)
    parser.add_argument("--skip-install", action="store_true")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    ensure_utf8_stdio()
    args = parse_args(argv)

    project = Path(args.project).resolve()
    if not project.is_dir():
        emit("ERROR", f"项目目录不存在：{project}")
        return 2

    emit("LOG", f"目标项目：{project}")
    emit("LOG", "引擎：Nuitka")
    emit_progress(2)

    if not args.skip_install and not nuitka_installed():
        if not install_nuitka():
            return 3
    emit_progress(5)

    entry = find_entry(project, args.entry)
    if entry is None:
        return 4
    emit("LOG", f"入口脚本：{entry}")

    app_name = args.name or entry.stem
    dist = Path(args.distpath) if args.distpath else project / "PyPackagerOut" / "nuitka"
    dist.mkdir(parents=True, exist_ok=True)

    cmd = build_nuitka_command(args, entry, app_name, dist)
    ok = run_build(cmd, project, dist, app_name, args.onefile)
    return 0 if ok else 1


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except SystemExit:
        raise
    except Exception as exc:  # noqa: BLE001
        emit("ERROR", f"未处理异常：{exc}")
        sys.exit(1)
