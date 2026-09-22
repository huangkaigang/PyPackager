using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PyPackager.Models;

namespace PyPackager.Services;

/// <summary>
/// APK 打包前“依赖 / UI 兼容性”预检（best-effort，非保证）。
///
/// 目的：在真正跑 buildozer（本地要下几 GB SDK、云端要排队）之前，先判断项目用到的
/// UI 框架与第三方库能不能在 Android 上跑。buildozer/python-for-android 只支持一部分包——
/// 纯 Python 包多半可用，桌面 GUI（tkinter/PyQt/wx）、Windows 专有、重型科学计算（torch/cv2）
/// 基本不可用，含 C 扩展且无 p4a 配方的也会失败。
///
/// 做法：扫描 .py 的 import + requirements.txt + buildozer.spec 的 requirements，取顶层模块名，
/// 对照内置的“已知可阻断 / 已知可用 / 标准库”清单分类，未知的第三方包标为 Warning 让用户核对。
/// </summary>
public sealed class ApkPreflightService
{
    private static readonly string[] ExcludeDirs =
        { "bin", ".buildozer", ".venv", "venv", "env", "__pycache__", ".git", ".gradle", "build", "dist" };

    private const int MaxFiles = 800;

    private static readonly Regex ImportRx = new(@"^\s*import\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex FromImportRx = new(@"^\s*from\s+([A-Za-z_][\w.]*)\s+import", RegexOptions.Compiled);

    // 移动端 UI 框架：可用于 Android
    private static readonly HashSet<string> UiOk = new(StringComparer.OrdinalIgnoreCase)
        { "flet", "kivy", "kivymd", "toga", "jnius", "pyjnius", "plyer", "android" };

    // 桌面 GUI / 桌面专有：Android 上不可用（阻断）
    private static readonly Dictionary<string, string> UiBlocking = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tkinter"] = "Tkinter 桌面 GUI，Android 无 Tk 运行时，无法打包。请改用 Flet 或 Kivy。",
        ["_tkinter"] = "Tkinter 底层模块，Android 不可用。",
        ["customtkinter"] = "基于 Tkinter，Android 不可用。请改用 Flet/Kivy。",
        ["ttkbootstrap"] = "基于 Tkinter，Android 不可用。",
        ["pysimplegui"] = "基于 Tkinter，Android 不可用。",
        ["pyqt5"] = "Qt 桌面 GUI，无 Android 配方，不可用。请改用 Flet/Kivy。",
        ["pyqt6"] = "Qt 桌面 GUI，无 Android 配方，不可用。请改用 Flet/Kivy。",
        ["pyqt4"] = "Qt 桌面 GUI，Android 不可用。",
        ["pyside"] = "Qt(PySide) 桌面 GUI，Android 不可用。",
        ["pyside2"] = "Qt(PySide2) 桌面 GUI，Android 不可用。",
        ["pyside6"] = "Qt(PySide6) 桌面 GUI，Android 不可用。",
        ["wx"] = "wxPython 桌面 GUI，Android 不可用。",
        ["wxpython"] = "wxPython 桌面 GUI，Android 不可用。",
        ["gi"] = "PyGObject/GTK 桌面 GUI，Android 不可用。",
        ["gtk"] = "GTK 桌面 GUI，Android 不可用。",
        ["pygtk"] = "PyGTK 桌面 GUI，Android 不可用。",
        ["dearpygui"] = "DearPyGui 桌面 GUI，Android 支持不完整。",
    };

    // 已知在 Android 上不可用 / 极难的第三方库（阻断）
    private static readonly Dictionary<string, string> DepBlocking = new(StringComparer.OrdinalIgnoreCase)
    {
        ["torch"] = "PyTorch 体积巨大且无可用 Android 配方，不可打包。",
        ["torchvision"] = "依赖 PyTorch，Android 不可用。",
        ["tensorflow"] = "TensorFlow 无实用 Android 配方（应改用 TFLite 原生）。",
        ["keras"] = "依赖 TensorFlow，Android 不可用。",
        ["onnxruntime"] = "无 Android 配方，不可用。",
        ["cv2"] = "opencv-python 含大量 C++ 扩展，p4a 配方不稳定，通常不可用。",
        ["opencv-python"] = "同 cv2，Android 通常不可用。",
        ["scipy"] = "含大量 Fortran/C 扩展，Android 编译极难。",
        ["sklearn"] = "scikit-learn 依赖 scipy，Android 极难。",
        ["scikit-learn"] = "同 sklearn。",
        ["pyautogui"] = "桌面键鼠自动化，Android 无对应能力。",
        ["pynput"] = "桌面键鼠监听，Android 不可用。",
        ["keyboard"] = "桌面键盘钩子，Android 不可用。",
        ["mouse"] = "桌面鼠标钩子，Android 不可用。",
        ["pygetwindow"] = "桌面窗口管理，Android 不可用。",
        ["mss"] = "桌面截屏，Android 不可用。",
        ["screeninfo"] = "桌面显示器信息，Android 不可用。",
        ["selenium"] = "驱动桌面浏览器，Android 不可用。",
        ["playwright"] = "驱动桌面浏览器，Android 不可用。",
        ["pywin32"] = "Windows 专有 API，Android 不可用。",
        ["pypiwin32"] = "Windows 专有 API，Android 不可用。",
        ["win32api"] = "Windows 专有 API，Android 不可用。",
        ["winreg"] = "Windows 注册表，Android 不可用。",
        ["msvcrt"] = "Windows CRT，Android 不可用。",
        ["winsound"] = "Windows 声音 API，Android 不可用。",
        ["pyaudio"] = "依赖 PortAudio，Android 配方不稳定，通常不可用。",
        ["matplotlib"] = "含 C 扩展且后端多为桌面，Android 打包极难（除非纯 Agg 且自带配方）。",
        ["ipython"] = "交互式桌面环境，不适合作为 APK 依赖。",
        ["jupyter"] = "Notebook 环境，不适合作为 APK 依赖。",
        ["pyqtgraph"] = "基于 Qt，Android 不可用。",
    };

    // 已知常用、有 p4a 配方或纯 Python、通常可用（Ok）
    private static readonly HashSet<string> DepOk = new(StringComparer.OrdinalIgnoreCase)
    {
        "requests", "urllib3", "certifi", "idna", "chardet", "charset_normalizer", "charset-normalizer",
        "pillow", "pil", "openssl", "cryptography", "cffi", "pycparser", "six",
        "python-dateutil", "dateutil", "pytz", "numpy", "beautifulsoup4", "bs4", "soupsieve",
        "lxml", "pyyaml", "yaml", "jinja2", "markupsafe", "werkzeug", "flask", "itsdangerous",
        "httpx", "h11", "anyio", "sniffio", "colorama", "tqdm", "click", "packaging",
        "attrs", "bcrypt", "flet", "kivy", "kivymd", "plyer", "pyjnius", "jnius",
        "future", "futures", "enum34", "typing-extensions", "typing_extensions", "setuptools",
        "websockets", "websocket-client", "websocket", "protobuf", "google", "sqlalchemy",
        "pymongo", "redis", "xmltodict", "python-for-android",
    };

    // 分发名 → 导入名 的常见映射（用于 requirements.txt 分类）
    private static readonly Dictionary<string, string> DistToImport = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pillow"] = "pil",
        ["beautifulsoup4"] = "bs4",
        ["pyyaml"] = "yaml",
        ["python-dateutil"] = "dateutil",
        ["opencv-python"] = "cv2",
        ["scikit-learn"] = "sklearn",
        ["pywin32"] = "win32api",
        ["pypiwin32"] = "win32api",
        ["wxpython"] = "wx",
        ["pyqt5"] = "pyqt5",
        ["pyside6"] = "pyside6",
        ["typing-extensions"] = "typing_extensions",
        ["charset-normalizer"] = "charset_normalizer",
        ["websocket-client"] = "websocket",
    };

    // Python 3 标准库（本机 sys.stdlib_module_names 导出的公共模块）
    private static readonly HashSet<string> StdLib = new(StringComparer.Ordinal)
    {
        "abc","antigravity","argparse","array","ast","asyncio","atexit","base64","bdb","binascii",
        "bisect","builtins","bz2","cProfile","calendar","cmath","cmd","code","codecs","codeop",
        "collections","colorsys","compileall","concurrent","configparser","contextlib","contextvars",
        "copy","copyreg","csv","ctypes","curses","dataclasses","datetime","dbm","decimal","difflib",
        "dis","doctest","email","encodings","ensurepip","enum","errno","faulthandler","fcntl","filecmp",
        "fileinput","fnmatch","fractions","ftplib","functools","gc","genericpath","getopt","getpass",
        "gettext","glob","graphlib","grp","gzip","hashlib","heapq","hmac","html","http","idlelib",
        "imaplib","importlib","inspect","io","ipaddress","itertools","json","keyword","linecache",
        "locale","logging","lzma","mailbox","marshal","math","mimetypes","mmap","modulefinder",
        "multiprocessing","netrc","nt","ntpath","nturl2path","numbers","opcode","operator","optparse",
        "os","pathlib","pdb","pickle","pickletools","pkgutil","platform","plistlib","poplib","posix",
        "posixpath","pprint","profile","pstats","pty","pwd","py_compile","pyclbr","pydoc","pydoc_data",
        "pyexpat","queue","quopri","random","re","readline","reprlib","resource","rlcompleter","runpy",
        "sched","secrets","select","selectors","shelve","shlex","shutil","signal","site","smtplib",
        "socket","socketserver","sqlite3","sre_compile","sre_constants","sre_parse","ssl","stat",
        "statistics","string","stringprep","struct","subprocess","symtable","sys","sysconfig","syslog",
        "tabnanny","tarfile","tempfile","termios","textwrap","this","threading","time","timeit",
        "token","tokenize","tomllib","trace","traceback","tracemalloc","tty","turtle","turtledemo",
        "types","typing","unicodedata","unittest","urllib","uuid","venv","warnings","wave","weakref",
        "webbrowser","wsgiref","xml","xmlrpc","zipapp","zipfile","zipimport","zlib","zoneinfo",
        "__future__",
    };

    public ApkPreflightReport Analyze(string projectDir)
    {
        // 收集所有出现的顶层模块/包名，按“归一化名”去重（如 beautifulsoup4 与 bs4 视为同一个）
        var raw = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var localModules = CollectLocalModuleNames(projectDir);
        var files = EnumeratePyFiles(projectDir).ToList();

        foreach (var file in files)
        {
            foreach (var m in ExtractImports(file)) raw.Add(m);
        }
        foreach (var m in ParseRequirementsTxt(projectDir)) raw.Add(m);
        foreach (var m in ParseBuildozerSpec(projectDir)) raw.Add(m);

        var modules = new Dictionary<string, string>(StringComparer.Ordinal); // normalized -> display
        foreach (var r in raw)
        {
            var key = Normalize(r);
            if (key.Length == 0) continue;
            if (!modules.ContainsKey(key)) modules[key] = r;
        }

        // 分类
        var deps = new List<ApkCompatItem>();
        string? uiFramework = null;
        var uiLevel = ApkCompatLevel.Warning;
        var uiReason = "未检测到 Flet/Kivy 等移动端 UI 框架。APK 需要移动端 UI；纯命令行或桌面 GUI 无法做成可用的安卓应用。";

        foreach (var (name, display) in modules)
        {
            if (localModules.Contains(name)) continue;              // 项目自身代码
            if (UiBlocking.ContainsKey(name))
            {
                deps.Add(new ApkCompatItem(display, ApkCompatLevel.Blocking, UiBlocking[name]));
                continue;
            }
            if (UiOk.Contains(name))
            {
                uiFramework = PrettyUi(name);
                uiLevel = ApkCompatLevel.Ok;
                uiReason = $"检测到移动端 UI 框架 {uiFramework}，可用于 Android 打包。";
                deps.Add(new ApkCompatItem(display, ApkCompatLevel.Ok, "移动端 UI 框架，Android 可用。"));
                continue;
            }
            if (DepBlocking.TryGetValue(name, out var blockReason))
            {
                deps.Add(new ApkCompatItem(display, ApkCompatLevel.Blocking, blockReason));
                continue;
            }
            if (DepOk.Contains(name) || DepOk.Contains(display))
            {
                deps.Add(new ApkCompatItem(display, ApkCompatLevel.Ok, "已知有 p4a 配方或纯 Python，Android 通常可用。"));
                continue;
            }
            if (StdLib.Contains(name))
            {
                // 平台专有 stdlib 在 Android 上不存在
                if (name is "msvcrt" or "winreg" or "winsound" or "nt")
                    deps.Add(new ApkCompatItem(display, ApkCompatLevel.Blocking, "Windows 专有标准库，Android 不存在。"));
                // 其余 stdlib 视为可用，不单列，避免噪音
                continue;
            }
            // 未知第三方
            deps.Add(new ApkCompatItem(display, ApkCompatLevel.Warning,
                "未在已知清单中。纯 Python 包通常可用；若含 C 扩展且无 python-for-android 配方则会失败，请核对。"));
        }

        var overall = ApkCompatLevel.Ok;
        if (uiLevel == ApkCompatLevel.Blocking || deps.Any(d => d.Level == ApkCompatLevel.Blocking))
            overall = ApkCompatLevel.Blocking;
        else if (uiLevel == ApkCompatLevel.Warning || deps.Any(d => d.Level == ApkCompatLevel.Warning))
            overall = ApkCompatLevel.Warning;

        var summary = BuildSummary(uiFramework, uiLevel, uiReason, deps, overall, files.Count);
        return new ApkPreflightReport(uiFramework, uiLevel, uiReason,
            deps.OrderBy(d => d.Level).ToList(), files.Count, overall, summary);
    }

    private static string BuildSummary(string? ui, ApkCompatLevel uiLevel, string uiReason,
        List<ApkCompatItem> deps, ApkCompatLevel overall, int fileCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"扫描 {fileCount} 个 .py 文件。");
        sb.AppendLine($"UI 框架：{(ui ?? "未检测到")} —— {uiReason}");
        var blocking = deps.Count(d => d.Level == ApkCompatLevel.Blocking);
        var warning = deps.Count(d => d.Level == ApkCompatLevel.Warning);
        var ok = deps.Count(d => d.Level == ApkCompatLevel.Ok);
        sb.AppendLine($"依赖判定：可用 {ok}，需核对 {warning}，阻断 {blocking}。");
        sb.AppendLine(overall switch
        {
            ApkCompatLevel.Blocking => "结论：存在阻断项，当前项目【不适合】直接打包成 APK，请先按提示替换库/框架。",
            ApkCompatLevel.Warning => "结论：无明确阻断项，但有需人工核对的依赖，可尝试打包（含 C 扩展的包可能失败）。",
            _ => "结论：未发现问题，可以尝试打包成 APK。",
        });
        return sb.ToString().TrimEnd();
    }

    // ---------- 扫描辅助 ----------

    private static IEnumerable<string> EnumeratePyFiles(string projectDir)
    {
        var root = new DirectoryInfo(projectDir);
        if (!root.Exists) yield break;
        var count = 0;
        Stack<DirectoryInfo> stack = new();
        stack.Push(root);
        while (stack.Count > 0 && count < MaxFiles)
        {
            var dir = stack.Pop();
            foreach (var sub in SafeDirs(dir))
            {
                if (!ExcludeDirs.Contains(sub.Name, StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
            }
            foreach (var f in SafeFiles(dir))
            {
                if (f.Extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
                {
                    yield return f.FullName;
                    if (++count >= MaxFiles) yield break;
                }
            }
        }
    }

    private static IEnumerable<DirectoryInfo> SafeDirs(DirectoryInfo d)
    {
        try { return d.EnumerateDirectories(); } catch { return Array.Empty<DirectoryInfo>(); }
    }

    private static IEnumerable<FileInfo> SafeFiles(DirectoryInfo d)
    {
        try { return d.EnumerateFiles(); } catch { return Array.Empty<FileInfo>(); }
    }

    /// <summary>项目内的本地模块名（.py 文件名与含 __init__.py 的目录名），用于从第三方里剔除。</summary>
    private static HashSet<string> CollectLocalModuleNames(string projectDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in EnumeratePyFiles(projectDir))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (!name.Equals("__init__", StringComparison.Ordinal)) set.Add(Normalize(name));
            var dir = Path.GetFileName(Path.GetDirectoryName(f)!);
            if (File.Exists(Path.Combine(Path.GetDirectoryName(f)!, "__init__.py"))) set.Add(Normalize(dir));
        }
        return set;
    }

    private static IEnumerable<string> ExtractImports(string file)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); } catch { yield break; }
        foreach (var line in lines)
        {
            var fm = FromImportRx.Match(line);
            if (fm.Success)
            {
                var mod = fm.Groups[1].Value;
                if (!mod.StartsWith('.')) yield return Root(mod);
                continue;
            }
            var im = ImportRx.Match(line);
            if (im.Success)
            {
                // 处理 "import a, b as c, d.e"
                foreach (var part in im.Groups[1].Value.Split(','))
                {
                    var token = part.Trim();
                    if (token.Length == 0) continue;
                    var asIdx = token.IndexOf(" as ", StringComparison.Ordinal);
                    if (asIdx > 0) token = token[..asIdx].Trim();
                    if (token.StartsWith('.') || token.Length == 0 || !char.IsLetter(token[0]) && token[0] != '_') continue;
                    yield return Root(token);
                }
            }
        }
    }

    private static string Root(string dotted)
    {
        var i = dotted.IndexOf('.');
        return i > 0 ? dotted[..i] : dotted;
    }

    private static IEnumerable<string> ParseRequirementsTxt(string projectDir)
    {
        var path = Path.Combine(projectDir, "requirements.txt");
        if (!File.Exists(path)) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { yield break; }
        foreach (var line in lines)
        {
            var name = CleanRequirement(line);
            if (name.Length > 0) yield return name;
        }
    }

    private static IEnumerable<string> ParseBuildozerSpec(string projectDir)
    {
        var path = Path.Combine(projectDir, "buildozer.spec");
        if (!File.Exists(path)) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { yield break; }
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (!t.StartsWith("requirements", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = t.IndexOf('=');
            if (eq < 0) continue;
            foreach (var part in t[(eq + 1)..].Split(','))
            {
                var name = CleanRequirement(part);
                if (name.Length > 0) yield return name;
            }
        }
    }

    private static string CleanRequirement(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0 || s.StartsWith("#") || s.StartsWith("-")) return string.Empty;
        // 去掉 extras / 版本约束 / 环境标记
        var cut = s.IndexOfAny(new[] { '=', '<', '>', '!', '~', '[', ';', ' ', '(' });
        if (cut >= 0) s = s[..cut];
        return s.Trim();
    }

    private static string Normalize(string name)
    {
        var n = name.Trim();
        if (DistToImport.TryGetValue(n, out var mapped)) n = mapped;
        return n.Replace('-', '_').ToLowerInvariant();
    }

    private static string PrettyUi(string normalized) => normalized.ToLowerInvariant() switch
    {
        "flet" => "Flet",
        "kivy" => "Kivy",
        "kivymd" => "KivyMD",
        "toga" => "Toga (BeeWare)",
        _ => normalized,
    };
}
