using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace ContextMenuManager.Core;

internal static class Native
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[]? phicon, uint[]? piconid, uint nIcons, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>解析 <c>@shell32.dll,-8506</c> 这类间接字符串；失败返回 null。</summary>
    public static string? LoadIndirectString(string source)
    {
        try
        {
            var sb = new StringBuilder(1024);
            return SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>通知资源管理器文件关联已变化，让它重新读取菜单注册。</summary>
    public static void NotifyAssocChanged()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero); } catch { /* 通知失败不影响结果 */ }
    }
}

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}

public sealed record FileMeta(string? Company, string? Product, string? Description);

public static partial class TextUtil
{
    private static readonly ConcurrentDictionary<string, FileMeta?> MetaCache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\s*\(&\w\)")]
    private static partial Regex AcceleratorSuffix();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex IdentifierPattern();

    /// <summary>看起来像内部标识符（没有空格的纯英文数字），而不是给人看的菜单文字。</summary>
    public static bool LooksLikeIdentifier(string? s) => !string.IsNullOrEmpty(s) && IdentifierPattern().IsMatch(s);

    [GeneratedRegex(@"^\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}$")]
    private static partial Regex GuidPattern();

    public static bool IsGuid(string? s) => s is not null && GuidPattern().IsMatch(s.Trim());

    /// <summary>规范成带花括号的大写形式；不是 GUID 时返回 null。</summary>
    public static string? NormalizeGuid(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Guid.TryParse(s.Trim(), out var g) ? g.ToString("B").ToUpperInvariant() : null;
    }

    /// <summary>去掉菜单文字里的快捷键标记：<c>打开(&amp;O)</c> → 打开，<c>Open Git Ba&amp;sh here</c> → Open Git Bash here。</summary>
    public static string CleanMenuText(string text)
    {
        var t = AcceleratorSuffix().Replace(text, "");
        t = t.Replace("&&", "\u0001").Replace("&", "").Replace('\u0001', '&');
        return t.Trim();
    }

    public static string? ResolveText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.StartsWith('@') ? Native.LoadIndirectString(raw) : raw;
        if (string.IsNullOrWhiteSpace(t)) return null;
        t = CleanMenuText(t);
        return t.Length == 0 ? null : t;
    }

    /// <summary>从命令行里取出可执行文件的完整路径；取不到返回 null。</summary>
    public static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var cmd = Environment.ExpandEnvironmentVariables(command).Trim();
        string candidate;
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            candidate = end > 1 ? cmd[1..end] : cmd.Trim('"');
            return ResolveExe(candidate);
        }
        // 没加引号的路径可能带空格：逐段拼接，直到找到存在的文件
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var acc = "";
        foreach (var part in parts)
        {
            acc = acc.Length == 0 ? part : acc + " " + part;
            var hit = ResolveExe(acc);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static readonly ConcurrentDictionary<char, bool> NetworkDrives = new();

    /// <summary>
    /// 这个路径能不能放心地去读文件信息。网络路径（UNC、映射的网络盘）不读：
    /// 对方不在线时每次访问都要等很久，会把整个扫描拖住。
    /// </summary>
    public static bool CanProbe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            return !NetworkDrives.GetOrAdd(char.ToUpperInvariant(path[0]), static letter =>
            {
                try { return new DriveInfo(letter + ":\\").DriveType == DriveType.Network; }
                catch { return true; }
            });
        }
        return true;
    }

    private static string? ResolveExe(string candidate)
    {
        candidate = candidate.Trim();
        if (candidate.Length == 0 || candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        if (Path.IsPathRooted(candidate) && !CanProbe(candidate)) return null;
        try
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                if (!Path.HasExtension(candidate) && File.Exists(candidate + ".exe")) return Path.GetFullPath(candidate + ".exe");
                return null;
            }
            if (candidate.Contains('\\') || candidate.Contains('/')) return null;
            var name = Path.HasExtension(candidate) ? candidate : candidate + ".exe";
            foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }
        catch
        {
            // 路径格式不合法，当作取不到
        }
        return null;
    }

    public static FileMeta? GetFileMeta(string? path)
    {
        if (string.IsNullOrEmpty(path) || !CanProbe(path)) return null;
        return MetaCache.GetOrAdd(path, static p =>
        {
            try
            {
                if (!File.Exists(p)) return null;
                var v = FileVersionInfo.GetVersionInfo(p);
                return new FileMeta(Clean(v.CompanyName), Clean(v.ProductName), Clean(v.FileDescription));
            }
            catch
            {
                return null;
            }
        });

        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>是否是 Windows 自带的文件：在系统目录（或 Defender 目录）下，并且公司是微软。显卡驱动之类的第三方文件不算。</summary>
    public static bool IsWindowsComponent(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var meta = GetFileMeta(path);
        if (meta?.Company is null || !meta.Company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return false;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles") })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            if (path.StartsWith(Path.Combine(pf, "Windows Defender") + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(Path.Combine(pf, "Windows NT") + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(Path.Combine(pf, "Windows Photo Viewer") + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(Path.Combine(pf, "Windows Media Player") + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>常见系统菜单项的中文名，以及任何情况下都不允许改动的核心项。</summary>
internal static class KnownItems
{
    /// <summary>按处理程序键名或 CLSID 查中文名。</summary>
    public static readonly Dictionary<string, string> HandlerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sharing"] = "授予访问权限",
        ["ModernSharing"] = "共享",
        ["EPP"] = "使用 Microsoft Defender 扫描",
        ["Open With"] = "打开方式",
        ["Open With EncryptionMenu"] = "文件所有权（加密）",
        ["EncryptionMenu"] = "文件所有权（加密）",
        ["SendTo"] = "发送到",
        ["New"] = "新建",
        ["CopyAsPathMenu"] = "复制文件地址",
        ["Library Location"] = "包含到库中",
        ["PintoStartScreen"] = "固定到“开始”屏幕",
        ["WorkFolders"] = "工作文件夹",
        ["Offline Files"] = "脱机文件",
        ["DesktopSlideshow"] = "下一个桌面背景",
        ["Compatibility"] = "兼容性疑难解答",
        ["OpenContainingFolderMenu"] = "打开文件所在的位置",
        ["EnhancedStorageShell"] = "增强存储设备",
        ["LibraryFolder"] = "库文件夹菜单",
        ["BriefcaseMenu"] = "公文包",
        ["{596AB062-B4D2-4215-9F74-E9109B0A8153}"] = "还原以前的版本",
        ["{90AA3A4E-1CBA-4233-B8BB-535773D48449}"] = "固定到任务栏",
        ["{A2A9545D-A0C2-42B4-9708-A0B2BADD77C8}"] = "固定到“开始”菜单",
        ["{474C98EE-CF3D-41F5-80E3-4AAB0AB04301}"] = "脱机文件",
        ["{FBEB8A05-BEEE-4442-804E-409D6C4515E9}"] = "刻录到光盘",
        ["{D6791A63-E7E2-4FEE-BF52-5DED8E86E9B8}"] = "便携设备菜单",
        ["{BD472F60-27FA-11CF-B8B4-444553540000}"] = "压缩文件夹（拖拽）",
        ["{D12E3394-DE4B-4777-93E9-DF0AC88F8584}"] = "压缩包（拖拽）",
        ["{00021401-0000-0000-C000-000000000046}"] = "快捷方式菜单",
    };

    /// <summary>没有显示文字的系统动词的中文名。</summary>
    public static readonly Dictionary<string, string> VerbNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["open"] = "打开",
        ["runas"] = "以管理员身份运行",
        ["runasuser"] = "以其他用户身份运行",
        ["explore"] = "在资源管理器中浏览",
        ["find"] = "搜索",
        ["print"] = "打印",
        ["edit"] = "编辑",
        ["openas"] = "打开方式",
        ["removeproperties"] = "删除属性",
        ["ModernProperties"] = "属性（新式）",
        ["pintohome"] = "固定到快速访问",
        ["pintohomefile"] = "添加到收藏夹",
        ["EditStickers"] = "添加或编辑贴纸",
        [".SpotlightNextImage"] = "Windows 聚焦：切换到下一张图片",
        [".SpotlightLearnMore"] = "Windows 聚焦：了解此图片",
        ["opennewwindow"] = "在新窗口中打开",
        ["opennewprocess"] = "在新进程中打开",
        ["opennewtab"] = "在新标签页中打开",
    };

    /// <summary>禁用后会影响双击打开、基本操作的动词，一律不许改。</summary>
    public static readonly HashSet<string> ProtectedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "runas", "runasuser", "explore", "openas", "opennewwindow", "opennewprocess", "opennewtab",
        "properties", "InvokeDefaultVerbInOtherProcess", "OpenWithSetDefaultOn",
    };

    /// <summary>核心系统处理程序（打开方式、新建、发送到、快捷方式）。</summary>
    public static readonly HashSet<string> ProtectedClsids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{09799AFB-AD67-11D1-ABCD-00C04FC30936}",
        "{D969A300-E7FF-11D0-A93B-00A0C90F2719}",
        "{7BA4C740-9E81-11CF-99D3-00AA004AE837}",
        "{00021401-0000-0000-C000-000000000046}",
    };

    /// <summary>常见打包应用的中文名，在清单里的名字解析不出来时用。</summary>
    public static readonly Dictionary<string, string> PackageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Paint"] = "画图",
        ["Microsoft.Windows.Photos"] = "照片",
        ["Microsoft.WindowsNotepad"] = "记事本",
        ["Microsoft.WindowsTerminal"] = "终端",
        ["Clipchamp.Clipchamp"] = "Clipchamp",
        ["Microsoft.ScreenSketch"] = "截图工具",
        ["Microsoft.ZuneMusic"] = "媒体播放器",
        ["Microsoft.ZuneVideo"] = "电影和电视",
        ["MicrosoftTeams"] = "Microsoft Teams",
        ["Microsoft.OutlookForWindows"] = "Outlook",
        ["Microsoft.PowerToys.SparseApp"] = "PowerToys",
    };

    /// <summary>常见软件的中文名：文件路径或产品名里出现关键字时，用中文名显示。</summary>
    public static readonly (string Keyword, string Name)[] AppAliases =
    [
        ("BaiduNetdisk", "百度网盘"), ("BaiduYunGuanjia", "百度网盘"), ("Quark", "夸克"),
        ("WeChat", "微信"), ("Weixin", "微信"), ("Thunder", "迅雷"), ("aDrive", "阿里云盘"), ("AliyunDrive", "阿里云盘"),
        ("CloudMusic", "网易云音乐"), ("DingTalk", "钉钉"), ("Feishu", "飞书"), ("WeMeet", "腾讯会议"),
        ("SunloginClient", "向日葵"), ("Huorong", "火绒"), ("360zip", "360 压缩"), ("HaoZip", "好压"),
        ("Kingsoft", "WPS Office"), ("WPS Office", "WPS Office"), ("Youdao", "有道"),
    ];

    public static string? AliasFor(string? filePath, string? product)
    {
        foreach (var (keyword, name) in AppAliases)
        {
            if ((filePath is not null && filePath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                || (product is not null && product.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
        return null;
    }

    /// <summary>Windows 自带的“新建”项。</summary>
    public static readonly HashSet<string> SystemShellNewExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".bmp", ".zip", ".rtf", ".lnk", ".library-ms", ".contact", ".jnt", ".bfc",
    };

    /// <summary>Windows 自带的“发送到”目标（特殊扩展名）。</summary>
    public static readonly Dictionary<string, string> SystemSendToExts = new(StringComparer.OrdinalIgnoreCase)
    {
        [".DeskLink"] = "桌面快捷方式",
        [".MAPIMail"] = "邮件收件人",
        [".ZFSendToTarget"] = "压缩(zipped)文件夹",
        [".mydocs"] = "文档",
    };
}
