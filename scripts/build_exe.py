#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build_exe.py —— PyPackager 的 Python 执行者。

由 C# 外壳通过命令行调用，负责把某个 Python 项目打包成 Windows EXE。
所有对外的进度信息都以结构化前缀输出到 stdout，供 C# 端实时解析：

    [LOG]      普通日志文本
    [PROGRESS] 0-100 的整数进度
    [RESULT]   打包成功后输出最终产物 EXE 的绝对路径
    [ERROR]    致命错误文本

设计原则：C# 只管流程与界面，PyInstaller 相关的脏活都收敛在这里，
未来升级 PyInstaller / 加入 Nuitka 只需改这个脚本。
"""

from __future__ import annotations

import argparse
import importlib.util
import os
import subprocess
import sys
from pathlib import Path


def emit(kind: str, message: str) -> None:
    """按结构化协议输出一行，并立即 flush，保证 C# 能实时读到。"""
    # 去掉内部换行，避免破坏 “一行一条协议” 的约定
    safe = str(message).replace("\r", " ").replace("\n", " ").strip()
    sys.stdout.write(f"[{kind}] {safe}\n")
    sys.stdout.flush()


def emit_progress(value: int) -> None:
    value = max(0, min(100, int(value)))
    emit("PROGRESS", value)


def ensure_utf8_stdio() -> None:
    """在 Windows 下强制 stdout/stderr 使用 UTF-8，避免中文日志乱码或崩溃。"""
    for stream_name in ("stdout", "stderr"):
        stream = getattr(sys, stream_name, None)
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except Exception:
                pass


def pyinstaller_installed() -> bool:
    return importlib.util.find_spec("PyInstaller") is not None


def install_pyinstaller() -> bool:
    emit("LOG", "未检测到 PyInstaller，正在通过 pip 自动安装……")
    cmd = [sys.executable, "-m", "pip", "install", "--upgrade", "pyinstaller"]
    try:
        proc = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except Exception as exc:  # noqa: BLE001
        emit("ERROR", f"调用 pip 失败：{exc}")
        return False

    for line in (proc.stdout or "").splitlines():
        if line.strip():
            emit("LOG", line.strip())

    if proc.returncode != 0:
        emit("ERROR", "PyInstaller 安装失败，请检查网络或 pip 源配置。")
        return False

    emit("LOG", "PyInstaller 安装完成。")
    return True


def find_entry(project: Path, requested: str | None) -> Path | None:
    """确定入口脚本。优先使用用户指定，其次猜测常见入口名。"""
    if requested:
        candidate = (project / requested).resolve() if not Path(requested).is_absolute() else Path(requested)
        if candidate.exists():
            return candidate
        emit("ERROR", f"指定的入口文件不存在：{candidate}")
        return None

    preferred = ["main.py", "app.py", "run.py", "start.py", "__main__.py"]
    for name in preferred:
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


def parse_version_tuple(version: str) -> tuple[int, int, int, int]:
    """把 '1.2.3' / '1.2.3.4' 解析成四元组，缺位补 0，非法则回退 (0,0,0,0)。"""
    parts = [p.strip() for p in str(version).split(".") if p.strip() != ""]
    nums: list[int] = []
    for p in parts[:4]:
        try:
            nums.append(int(p))
        except ValueError:
            nums.append(0)
    while len(nums) < 4:
        nums.append(0)
    return (nums[0], nums[1], nums[2], nums[3])


def generate_version_file(spec_dir: Path, args: argparse.Namespace, app_name: str) -> Path | None:
    """生成 PyInstaller 版本信息文件（Windows 资源里的“详细信息”）。失败则返回 None 并忽略。"""
    if not args.version:
        return None

    ver = parse_version_tuple(args.version)
    product_name = args.product_name or app_name
    company_name = args.company_name or ""
    file_desc = args.file_description or product_name
    legal = args.legal_copyright or ""

    content = f"""# 由 PyPackager 自动生成的 PyInstaller 版本信息文件
VSVersionInfo(
  ffi=FixedFileInfo(
    filevers={ver},
    prodvers={ver},
    mask=0x3f,
    flags=0x0,
    OS=0x40004,
    fileType=0x1,
    subtype=0x0,
    date=(0, 0)
  ),
  kids=[
    StringFileInfo(
      [
        StringTable(
          '040904B0',
          [
            StringStruct('CompanyName', {company_name!r}),
            StringStruct('FileDescription', {file_desc!r}),
            StringStruct('FileVersion', {args.version!r}),
            StringStruct('InternalName', {app_name!r}),
            StringStruct('LegalCopyright', {legal!r}),
            StringStruct('OriginalFilename', '{app_name}.exe'),
            StringStruct('ProductName', {product_name!r}),
            StringStruct('ProductVersion', {args.version!r})
          ]
        )
      ]
    ),
    VarFileInfo([VarStruct('Translation', [1033, 1200])])
  ]
)
"""
    try:
        spec_dir.mkdir(parents=True, exist_ok=True)
        vf = spec_dir / f"{app_name}_version_info.py"
        vf.write_text(content, encoding="utf-8")
        emit("LOG", f"已生成版本信息文件：{vf}（版本 {args.version}）")
        return vf
    except Exception as exc:  # noqa: BLE001
        emit("LOG", f"版本信息文件生成失败，已忽略：{exc}")
        return None


def resolve_data_entry(item: str, project: Path) -> str:
    """把 'src;dest' 的 src 解析为绝对路径（相对路径按项目目录解析），dest 原样保留。"""
    src, sep, dest = item.partition(";")
    src_path = Path(src)
    if not src_path.is_absolute():
        src_path = project / src_path
    resolved_src = str(src_path)
    return f"{resolved_src}{sep}{dest}" if sep else resolved_src


def build_pyinstaller_command(args: argparse.Namespace, entry: Path, version_file: Path | None = None) -> list[str]:
    cmd: list[str] = [sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean"]

    if args.onefile:
        cmd.append("--onefile")
    else:
        cmd.append("--onedir")

    if args.noconsole:
        cmd.append("--windowed")
    else:
        cmd.append("--console")

    if args.name:
        cmd += ["--name", args.name]

    if args.icon:
        icon_path = Path(args.icon)
        if not icon_path.is_absolute():
            icon_path = Path(args.project) / icon_path
        if icon_path.exists():
            cmd += ["--icon", str(icon_path)]
        else:
            emit("LOG", f"图标文件不存在，已忽略：{icon_path}")

    if version_file is not None:
        cmd += ["--version-file", str(version_file)]

    # 附加数据文件（可重复），格式 src;dest
    # 相对源路径按项目目录解析为绝对路径，避免 PyInstaller 以 spec 目录为基准找不到文件
    for item in args.data or []:
        cmd += ["--add-data", resolve_data_entry(item, Path(args.project).resolve())]

    # 隐藏导入（可重复）
    for mod in args.hidden_import or []:
        cmd += ["--hidden-import", mod]

    if args.upx:
        if args.upx_dir:
            cmd += ["--upx-dir", args.upx_dir]
    else:
        cmd.append("--noupx")

    cmd += ["--distpath", args.distpath]
    cmd += ["--workpath", args.workpath]
    cmd += ["--specpath", args.specpath]

    cmd.append(str(entry))
    return cmd


def run_build(cmd: list[str], project: Path, expected_exe: Path) -> bool:
    emit("LOG", "开始执行 PyInstaller 打包……")
    emit("LOG", "命令：" + " ".join(f'"{c}"' if " " in c else c for c in cmd))
    emit_progress(10)

    try:
        proc = subprocess.Popen(
            cmd,
            cwd=str(project),
            stdin=subprocess.DEVNULL,  # 杜绝交互提示阻塞
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
    except Exception as exc:  # noqa: BLE001
        emit("ERROR", f"启动 PyInstaller 进程失败：{exc}")
        return False

    # PyInstaller 不输出百分比，这里按 “正在处理模块” 的节奏推进一个伪进度，
    # 让用户有反馈，最终成功/失败再拉到 100 或保持。
    progress = 15
    assert proc.stdout is not None
    for raw in proc.stdout:
        line = raw.rstrip()
        if not line:
            continue
        emit("LOG", line)
        low = line.lower()
        if "building" in low or "analyzing" in low or "collecting" in low:
            progress = min(90, progress + 4)
            emit_progress(progress)

    proc.wait()
    if proc.returncode != 0:
        emit("ERROR", f"PyInstaller 退出码 {proc.returncode}，打包失败。")
        return False

    if not expected_exe.exists():
        emit("ERROR", f"打包流程结束但未找到产物：{expected_exe}")
        return False

    emit_progress(100)
    emit("RESULT", str(expected_exe.resolve()))
    return True


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="PyPackager EXE 打包引擎")
    parser.add_argument("--project", required=True, help="Python 项目目录")
    parser.add_argument("--entry", default=None, help="入口脚本文件名（相对项目目录），留空自动猜测")
    parser.add_argument("--name", default=None, help="生成的 EXE 名称（不含扩展名）")
    parser.add_argument("--icon", default=None, help="图标文件路径（.ico）")
    parser.add_argument("--onefile", action="store_true", help="打包为单文件 EXE")
    parser.add_argument("--noconsole", action="store_true", help="不显示控制台黑框（GUI 程序）")
    parser.add_argument("--upx", action="store_true", help="启用 UPX 压缩")
    parser.add_argument("--upx-dir", default=None, help="UPX 可执行文件所在目录")
    parser.add_argument("--data", action="append", help="附加数据文件，格式 src;dest，可重复")
    parser.add_argument("--hidden-import", action="append", help="隐藏导入模块名，可重复")
    parser.add_argument("--version", default=None, help="文件/产品版本号，如 1.0.0.0（生成 Windows 版本信息）")
    parser.add_argument("--product-name", default=None, help="产品名称（版本信息用）")
    parser.add_argument("--company-name", default=None, help="公司名称（版本信息用）")
    parser.add_argument("--file-description", default=None, help="文件说明（版本信息用）")
    parser.add_argument("--legal-copyright", default=None, help="版权信息（版本信息用）")
    parser.add_argument("--distpath", default=None, help="产物输出目录")
    parser.add_argument("--workpath", default=None, help="中间文件目录")
    parser.add_argument("--specpath", default=None, help="spec 文件输出目录")
    parser.add_argument("--skip-install", action="store_true", help="跳过 PyInstaller 自动安装")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    ensure_utf8_stdio()
    args = parse_args(argv)

    project = Path(args.project).resolve()
    if not project.is_dir():
        emit("ERROR", f"项目目录不存在：{project}")
        return 2

    emit("LOG", f"目标项目：{project}")
    emit_progress(2)

    # 环境准备
    if not args.skip_install and not pyinstaller_installed():
        if not install_pyinstaller():
            return 3
    emit_progress(8)

    entry = find_entry(project, args.entry)
    if entry is None:
        return 4
    emit("LOG", f"入口脚本：{entry}")

    app_name = args.name or entry.stem

    # 输出路径：默认放到项目下的 PyPackagerOut，避免污染用户目录
    dist = Path(args.distpath) if args.distpath else project / "PyPackagerOut" / "dist"
    work = Path(args.workpath) if args.workpath else project / "PyPackagerOut" / "build"
    spec = Path(args.specpath) if args.specpath else project / "PyPackagerOut"
    for p in (dist, work, spec):
        p.mkdir(parents=True, exist_ok=True)

    # 回写解析后的真实路径，避免把 None 拼进命令行
    args.distpath = str(dist)
    args.workpath = str(work)
    args.specpath = str(spec)

    expected_exe = dist / f"{app_name}.exe"

    version_file = generate_version_file(spec, args, app_name)
    cmd = build_pyinstaller_command(args, entry, version_file)
    ok = run_build(cmd, project, expected_exe)
    return 0 if ok else 1


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except SystemExit:
        raise
    except Exception as exc:  # noqa: BLE001
        emit("ERROR", f"未处理异常：{exc}")
        sys.exit(1)
