using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ContextMenuManager.Core;

/// <summary>把 exe/dll/快捷方式/图片里的图标转成 PNG data URI，给界面显示。</summary>
public static class IconService
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ImageExts = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];
    private static readonly string[] SkipExeWords = ["unins", "update", "crash", "setup", "install", "helper", "report"];
    private const int Size = 48;

    public static string? GetDataUri(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        return Cache.GetOrAdd(source, static s =>
        {
            try { return Load(s); }
            catch { return null; }
        });
    }

    private static string? Load(string source)
    {
        var (path, index) = SplitIndex(Environment.ExpandEnvironmentVariables(source.Trim()).Trim('"'));
        if (!Path.IsPathRooted(path))
        {
            var inSystem = Path.Combine(Environment.SystemDirectory, path);
            if (File.Exists(inSystem)) path = inSystem;
        }
        if (!TextUtil.CanProbe(path) || !File.Exists(path)) return null;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ImageExts.Contains(ext))
        {
            var info = new FileInfo(path);
            if (info.Length > 512 * 1024) return null;
            var mime = ext switch { ".png" => "image/png", ".gif" => "image/gif", ".bmp" => "image/bmp", _ => "image/jpeg" };
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }

        var uri = FromResource(path, index);
        if (uri is not null) return uri;

        if (ext == ".dll")
        {
            // 很多扩展 DLL 自己不带图标：到同目录和上一级目录里找主程序的图标
            var exe = FindSiblingExe(path);
            return exe is null ? null : FromResource(exe, 0);
        }

        using var assoc = Icon.ExtractAssociatedIcon(path);
        return assoc is null ? null : ToDataUri(assoc);
    }

    private static (string path, int index) SplitIndex(string s)
    {
        var comma = s.LastIndexOf(',');
        if (comma > 1 && int.TryParse(s[(comma + 1)..].Trim(), out var idx)) return (s[..comma].Trim().Trim('"'), idx);
        return (s, 0);
    }

    private static string? FromResource(string path, int index)
    {
        var handles = new IntPtr[1];
        var ids = new uint[1];
        var n = Native.PrivateExtractIcons(path, index, Size, Size, handles, ids, 1, 0);
        if (n == 0 || n == uint.MaxValue || handles[0] == IntPtr.Zero) return null;
        try
        {
            using var icon = Icon.FromHandle(handles[0]);
            return ToDataUri(icon);
        }
        finally
        {
            Native.DestroyIcon(handles[0]);
        }
    }

    private static string ToDataUri(Icon icon)
    {
        using var bmp = icon.ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private static string? FindSiblingExe(string dll)
    {
        var dir = Path.GetDirectoryName(dll);
        for (var level = 0; level < 2 && dir is not null; level++, dir = Path.GetDirectoryName(dir))
        {
            // 不在系统目录或盘符根目录里乱找
            if (dir.Length <= 3 || dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) break;
            string[] exes;
            try { exes = Directory.GetFiles(dir, "*.exe"); }
            catch { continue; }
            var best = exes
                .Where(e => !SkipExeWords.Any(w => Path.GetFileName(e).Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Take(40)
                .OrderByDescending(e => new FileInfo(e).Length)
                .FirstOrDefault();
            if (best is not null) return best;
        }
        return null;
    }
}

/// <summary>会影响桌面的动作：打开资源管理器窗口、重启资源管理器。只在图形界面里启用。</summary>
public static class ShellActions
{
    public static void Reveal(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (Directory.Exists(path))
            OpenFolder(path);
        else
            throw new FileNotFoundException("路径不存在。", path);
    }

    public static void OpenFolder(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("目录不存在：" + dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    /// <summary>只重启当前会话的资源管理器。</summary>
    public static void RestartExplorer()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session) continue;
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch
                {
                    // 结束不了就跳过，后面会检查是否还在运行
                }
            }
        }
        // Windows 通常会自动拉起外壳；等几秒还没有就手动启动
        for (var i = 0; i < 15; i++)
        {
            Thread.Sleep(200);
            if (ExplorerRunning(session)) return;
        }
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
    }

    private static bool ExplorerRunning(int session)
    {
        var list = Process.GetProcessesByName("explorer");
        try { return list.Any(p => { try { return p.SessionId == session; } catch { return false; } }); }
        finally { foreach (var p in list) p.Dispose(); }
    }
}

public sealed class AppSettings
{
    public bool AllowSystem { get; set; }

    public static AppSettings Load(string dataDir)
    {
        try
        {
            var file = Path.Combine(dataDir, "settings.json");
            if (File.Exists(file)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json.Pretty) ?? new AppSettings();
        }
        catch
        {
            // 设置文件损坏时用默认值
        }
        return new AppSettings();
    }

    public void Save(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "settings.json"), JsonSerializer.Serialize(this, Json.Pretty));
    }
}

/// <summary>界面和引擎之间的调用入口。WebView2 宿主和开发用的 HTTP 服务共用这一套。</summary>
public sealed class RpcHost
{
    private readonly Engine _engine;
    private readonly bool _shellActions;
    private readonly string _version;

    public RpcHost(Engine engine, bool shellActions, string version)
    {
        _engine = engine;
        _shellActions = shellActions;
        _version = version;
        engine.Env.AllowSystem = AppSettings.Load(engine.Env.DataDir).AllowSystem;
    }

    /// <summary>处理一次调用，返回 <c>{ok:true,result}</c> 或 <c>{ok:false,error}</c> 的 JSON。</summary>
    public string Handle(string method, JsonElement args)
    {
        try
        {
            var result = Dispatch(method, args);
            return JsonSerializer.Serialize(new { ok = true, result }, Json.Compact);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json.Compact);
        }
    }

    private object? Dispatch(string method, JsonElement args)
    {
        var env = _engine.Env;
        switch (method)
        {
            case "info":
                return new
                {
                    version = _version,
                    sandbox = env.Reg.IsSandbox,
                    readOnly = env.ReadOnly,
                    elevated = Elevation.IsElevated,
                    allowSystem = env.AllowSystem,
                    dataDir = env.DataDir,
                    shellActions = _shellActions,
                };

            case "scan":
                return new { items = _engine.Scan() };

            case "scanFileType":
                return new { items = _engine.ScanFileType(Str(args, "ext")) };

            case "apply":
            {
                var changes = args.GetProperty("changes").Deserialize<List<ChangeRequest>>(Json.Compact) ?? [];
                if (changes.Count == 0) throw new ArgumentException("没有要应用的改动。");
                return _engine.Apply(changes);
            }

            case "history":
                return new { batches = _engine.History.List() };

            case "undo":
            {
                List<string>? ids = args.TryGetProperty("itemIds", out var el) && el.ValueKind == JsonValueKind.Array
                    ? el.Deserialize<List<string>>(Json.Compact)
                    : null;
                return _engine.Undo(Str(args, "batchId"), ids);
            }

            case "setAllowSystem":
            {
                var value = args.GetProperty("value").GetBoolean();
                env.AllowSystem = value;
                if (!env.ReadOnly) new AppSettings { AllowSystem = value }.Save(env.DataDir);
                return new { allowSystem = value };
            }

            case "icons":
            {
                var map = new Dictionary<string, string?>();
                foreach (var s in args.GetProperty("sources").EnumerateArray())
                {
                    var src = s.GetString();
                    if (!string.IsNullOrEmpty(src)) map[src] = IconService.GetDataUri(src);
                }
                return new { icons = map };
            }

            case "reveal":
                RequireShell();
                ShellActions.Reveal(Str(args, "path"));
                return null;

            case "openHistoryDir":
            {
                RequireShell();
                var id = args.TryGetProperty("batchId", out var b) ? b.GetString() : null;
                var dir = string.IsNullOrEmpty(id) ? _engine.History.Root : _engine.History.DirOf(id);
                Directory.CreateDirectory(dir);
                ShellActions.OpenFolder(dir);
                return null;
            }

            case "restartExplorer":
                RequireShell();
                ShellActions.RestartExplorer();
                return null;

            default:
                throw new ArgumentException("未知的调用：" + method);
        }
    }

    private void RequireShell()
    {
        if (!_shellActions) throw new InvalidOperationException("预览模式下不能执行这个操作。");
    }

    private static string Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new ArgumentException("缺少参数：" + name);
}
