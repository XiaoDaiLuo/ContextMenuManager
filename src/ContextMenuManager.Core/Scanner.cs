using System.Xml.Linq;
using Microsoft.Win32;

namespace ContextMenuManager.Core;

/// <summary>运行环境：注册表映射、数据目录、发送到目录、打包应用来源，以及安全开关。</summary>
public sealed class AppEnv
{
    public required IRegistryEnv Reg { get; init; }
    public required string DataDir { get; init; }
    public required string SendToDir { get; init; }
    public required IPackageSource Packages { get; init; }
    /// <summary>允许改动 Windows 自带项（核心项仍然锁定）。</summary>
    public bool AllowSystem { get; set; }
    /// <summary>只读模式：只能扫描，不能应用改动。</summary>
    public bool ReadOnly { get; set; }
    public IElevator? Elevator { get; set; }
    /// <summary>导出 .reg 备份用的函数（物理路径、目标文件）→ 是否成功。测试里可以替换成会失败的版本。</summary>
    public Func<string, string, bool> RegExporter { get; set; } = RegExport.Export;

    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContextMenuManager");

    public static AppEnv CreateReal(string? dataDir = null) => new()
    {
        Reg = new RealRegistryEnv(),
        DataDir = dataDir ?? DefaultDataDir,
        SendToDir = Environment.GetFolderPath(Environment.SpecialFolder.SendTo),
        Packages = new RegistryPackageSource(),
        Elevator = new ProcessElevator(),
    };

    public static AppEnv CreateSandbox(string name, string dataDir, IPackageSource? packages = null) => new()
    {
        Reg = new SandboxRegistryEnv(name),
        DataDir = dataDir,
        SendToDir = Path.Combine(dataDir, "sendto"),
        Packages = packages ?? new StaticPackageSource([]),
    };
}

/// <summary>
/// 一个已安装的应用包。普通包的清单就在包目录里；稀疏包（VS Code、Notepad++、百度网盘这类
/// “传统软件 + 一个小应用包”的形式）的包目录指向软件安装位置，清单则在 WindowsApps 下，
/// 所以清单位置可以单独指定。
/// </summary>
public sealed record PackageInfo(string FullName, string RootFolder, string? ManifestFile = null)
{
    public string Name => FullName.Split('_')[0];
    public string FamilyName
    {
        get
        {
            var parts = FullName.Split('_');
            return parts.Length >= 2 ? parts[0] + "_" + parts[^1] : FullName;
        }
    }
    public string ManifestPath => ManifestFile ?? Path.Combine(RootFolder, "AppxManifest.xml");
}

public interface IPackageSource
{
    IEnumerable<PackageInfo> GetPackages();
}

/// <summary>从当前用户的包仓库注册表里列出已安装的打包应用。</summary>
public sealed class RegistryPackageSource : IPackageSource
{
    private const string Repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public IEnumerable<PackageInfo> GetPackages()
    {
        using var repo = Registry.CurrentUser.OpenSubKey(Repo);
        if (repo is null) yield break;
        foreach (var name in repo.GetSubKeyNames())
        {
            string? root;
            try
            {
                using var k = repo.OpenSubKey(name);
                root = k?.GetValue("PackageRootFolder") as string;
            }
            catch
            {
                continue;
            }
            if (string.IsNullOrEmpty(root)) continue;
            string? manifest = null;
            try
            {
                if (!File.Exists(Path.Combine(root, "AppxManifest.xml")))
                {
                    var programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    var staged = Path.Combine(programFiles, "WindowsApps", name, "AppxManifest.xml");
                    if (File.Exists(staged)) manifest = staged;
                }
            }
            catch
            {
                // 路径不可访问时按普通包处理，后面读不到清单会自动跳过
            }
            yield return new PackageInfo(name, root, manifest);
        }
    }
}

public sealed class StaticPackageSource(IEnumerable<PackageInfo> packages) : IPackageSource
{
    private readonly List<PackageInfo> _packages = packages.ToList();
    public IEnumerable<PackageInfo> GetPackages() => _packages;
}

/// <summary>扫描各处右键菜单注册，只读。</summary>
public sealed class Scanner(AppEnv env)
{
    private sealed record ClassDef(string Cls, string Label, string[] Scenes, bool DragDrop);

    private static readonly ClassDef[] ClassDefs =
    [
        new("*", "所有文件", ["file"], false),
        new("AllFilesystemObjects", "文件和文件夹", ["file", "folder"], true),
        new("Directory", "文件夹", ["folder"], true),
        new("Folder", "所有文件夹对象（含磁盘、库）", ["folder"], true),
        new(@"Directory\Background", "文件夹空白处", ["background"], false),
        new("DesktopBackground", "桌面空白处", ["background"], false),
        new("Drive", "磁盘", ["drive"], true),
        new("lnkfile", "快捷方式", ["other"], false),
        new("exefile", "程序文件（.exe）", ["other"], false),
        new("LibraryFolder", "库", ["other"], false),
        new(@"LibraryFolder\Background", "库空白处", ["other"], false),
        new(@"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "此电脑", ["other"], false),
        new(@"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}", "回收站", ["other"], false),
    ];

    private static readonly string[] HiveClassRoots = [RegPath.HklmClasses, RegPath.HkcuClasses];
    private static readonly string[] HideMarkers = ["ProgrammaticAccessOnly", "LegacyDisable", "HideBasedOnVelocityId"];
    private static readonly string[] ShellNewValueNames = ["NullFile", "FileName", "Data", "Command", "Handler", "Directory"];

    public const string VerbPrefix = "verb|";
    public const string ShellExPrefix = "shellex|";
    public const string DragDropPrefix = "dragdrop|";
    public const string ShellNewPrefix = "shellnew|";
    public const string PackagedPrefix = "pkg|";
    public const string SendToPrefix = "sendto|";

    private readonly IRegistryEnv _reg = env.Reg;

    public static IReadOnlyList<string> HiddenMarkerNames => HideMarkers;

    /// <summary>扫描全部固定场景（不含按扩展名查询的文件类型）。</summary>
    public List<MenuItem> ScanAll()
    {
        var items = new List<MenuItem>();
        foreach (var def in ClassDefs)
            foreach (var root in HiveClassRoots)
                ScanClassRoot(items, root, def);
        items.AddRange(ScanPackaged());
        items.AddRange(ScanSendTo());
        items.AddRange(ScanShellNew());
        return items;
    }

    /// <summary>按扩展名扫描：扩展名本身、它关联的 ProgID、SystemFileAssociations。</summary>
    public List<MenuItem> ScanFileType(string ext)
    {
        ext = ext.Trim();
        if (ext.Length == 0) return [];
        if (!ext.StartsWith('.')) ext = "." + ext;
        if (ext.Length < 2 || ext.IndexOfAny(['\\', '/', '*', '?', '"', ' ']) >= 0) return [];
        var classes = new List<string> { ext, @"SystemFileAssociations\" + ext };
        foreach (var root in HiveClassRoots)
        {
            var progId = _reg.ReadString(RegPath.Combine(root, ext));
            if (!string.IsNullOrWhiteSpace(progId)) classes.Add(progId);
            var perceived = _reg.ReadString(RegPath.Combine(root, ext), "PerceivedType");
            if (!string.IsNullOrWhiteSpace(perceived)) classes.Add(@"SystemFileAssociations\" + perceived);
        }
        var userChoice = _reg.ReadString(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice", "ProgId");
        if (!string.IsNullOrWhiteSpace(userChoice)) classes.Add(userChoice);

        var items = new List<MenuItem>();
        foreach (var cls in classes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (cls.Contains('\\') && !cls.StartsWith(@"SystemFileAssociations\", StringComparison.OrdinalIgnoreCase)) continue;
            var def = FileTypeDef(cls);
            foreach (var root in HiveClassRoots) ScanClassRoot(items, root, def);
        }
        return items;
    }

    private static ClassDef FileTypeDef(string cls) => new(cls, cls, ["filetype"], false);

    private static ClassDef DefFor(string cls) =>
        ClassDefs.FirstOrDefault(d => string.Equals(d.Cls, cls, StringComparison.OrdinalIgnoreCase)) ?? FileTypeDef(cls);

    private void ScanClassRoot(List<MenuItem> items, string hiveRoot, ClassDef def)
    {
        var clsPath = RegPath.Combine(hiveRoot, def.Cls);

        foreach (var name in SubKeyNames(RegPath.Combine(clsPath, "shell")))
        {
            var item = ReadVerb(hiveRoot, def, name);
            if (item is not null) items.Add(item);
        }

        AddHandlers(items, hiveRoot, def, ItemKind.ShellEx, "ContextMenuHandlers");
        if (def.DragDrop) AddHandlers(items, hiveRoot, def, ItemKind.DragDrop, "DragDropHandlers");
    }

    private void AddHandlers(List<MenuItem> items, string hiveRoot, ClassDef def, ItemKind kind, string container)
    {
        var shellex = RegPath.Combine(RegPath.Combine(hiveRoot, def.Cls), "shellex");
        var names = new List<string>();
        foreach (var n in SubKeyNames(RegPath.Combine(shellex, container))) names.Add(n);
        foreach (var n in SubKeyNames(RegPath.Combine(shellex, "-" + container)))
            if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        foreach (var name in names)
        {
            var item = ReadHandler(hiveRoot, def, kind, container, name);
            if (item is not null) items.Add(item);
        }
    }

    private List<string> SubKeyNames(string path)
    {
        try
        {
            using var k = _reg.Open(path);
            return k is null ? [] : [.. k.GetSubKeyNames()];
        }
        catch
        {
            return [];
        }
    }

    // ---------- 静态项 ----------

    private MenuItem? ReadVerb(string hiveRoot, ClassDef def, string verbName)
    {
        var path = RegPath.Combine(RegPath.Combine(RegPath.Combine(hiveRoot, def.Cls), "shell"), verbName);
        try
        {
            using var key = _reg.Open(path);
            if (key is null) return null;

            var raw = key.GetValue("MUIVerb") as string;
            if (string.IsNullOrWhiteSpace(raw)) raw = key.GetValue("") as string;
            var indirect = raw is not null && raw.StartsWith('@');
            var hasOwnText = !string.IsNullOrWhiteSpace(raw) && !indirect;
            var title = TextUtil.ResolveText(raw);

            var item = new MenuItem
            {
                Id = VerbPrefix + path,
                Kind = ItemKind.Verb,
                Scenes = [.. def.Scenes],
                Location = def.Cls,
                LocationLabel = def.Label,
                KeyName = verbName,
                Hive = RegPath.Hive(hiveRoot),
                RegistryPath = path,
                NeedsAdmin = _reg.RequiresAdmin(path),
                CanDelete = true,
            };

            var names = key.GetValueNames();
            item.Enabled = !names.Any(n => HideMarkers.Contains(n, StringComparer.OrdinalIgnoreCase));
            if (names.Contains("Extended", StringComparer.OrdinalIgnoreCase)) item.Flags.Add("extended");
            var subKeys = key.GetSubKeyNames();
            if (names.Contains("SubCommands", StringComparer.OrdinalIgnoreCase)
                || names.Contains("ExtendedSubCommandsKey", StringComparer.OrdinalIgnoreCase)
                || subKeys.Contains("shell", StringComparer.OrdinalIgnoreCase))
                item.Flags.Add("submenu");

            string? clsid = TextUtil.NormalizeGuid(key.GetValue("ExplorerCommandHandler") as string);
            using (var cmdKey = key.OpenSubKey("command"))
            {
                if (cmdKey is not null)
                {
                    item.Command = cmdKey.GetValue("") as string;
                    clsid ??= TextUtil.NormalizeGuid(cmdKey.GetValue("DelegateExecute") as string);
                }
            }
            if (clsid is not null) { item.Clsid = clsid; item.Flags.Add("explorerCommand"); }

            item.FilePath = TextUtil.ExtractExePath(item.Command);
            ClsidInfo? ci = null;
            if (item.FilePath is null && clsid is not null)
            {
                ci = ResolveClsid(clsid);
                item.FilePath = ci?.ServerPath;
            }
            var iconValue = key.GetValue("Icon") as string;
            item.IconSource = !string.IsNullOrWhiteSpace(iconValue) ? Environment.ExpandEnvironmentVariables(iconValue) : item.FilePath;

            ApplyFileMeta(item);

            var windowsFile = TextUtil.IsWindowsComponent(item.FilePath);
            item.IsSystem = item.FilePath is not null
                ? windowsFile && !hasOwnText
                : item.Hive == "HKLM" && !hasOwnText;
            item.IsProtected = KnownItems.ProtectedVerbs.Contains(verbName);
            if (item.IsProtected) item.IsSystem = true;

            // 由 COM 命令动态生成文字的项，注册表里常常只有一个内部名字；这时用软件名更好认
            if (clsid is not null && !item.IsSystem && TextUtil.LooksLikeIdentifier(title) && item.AppName is not null)
                title = item.AppName;
            item.Title = title
                         ?? (KnownItems.VerbNames.TryGetValue(verbName, out var known) ? known : null)
                         ?? ci?.Name
                         ?? verbName;
            return item;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    // ---------- 扩展（ContextMenuHandlers / DragDropHandlers）----------

    private MenuItem? ReadHandler(string hiveRoot, ClassDef def, ItemKind kind, string container, string name)
    {
        var shellex = RegPath.Combine(RegPath.Combine(hiveRoot, def.Cls), "shellex");
        var active = RegPath.Combine(RegPath.Combine(shellex, container), name);
        var mirror = RegPath.Combine(RegPath.Combine(shellex, "-" + container), name);
        try
        {
            var activeExists = _reg.KeyExists(active);
            var mirrorExists = _reg.KeyExists(mirror);
            if (!activeExists && !mirrorExists) return null;
            var current = activeExists ? active : mirror;

            var defaultValue = _reg.ReadString(current);
            var clsid = TextUtil.NormalizeGuid(name) ?? TextUtil.NormalizeGuid(defaultValue);
            var ci = clsid is null ? null : ResolveClsid(clsid);

            var item = new MenuItem
            {
                Id = (kind == ItemKind.DragDrop ? DragDropPrefix : ShellExPrefix) + active,
                Kind = kind,
                Scenes = kind == ItemKind.DragDrop ? ["dragdrop"] : [.. def.Scenes],
                Location = def.Cls,
                LocationLabel = def.Label,
                KeyName = name,
                Hive = RegPath.Hive(hiveRoot),
                RegistryPath = current,
                Clsid = clsid,
                FilePath = ci?.ServerPath,
                IconSource = ci?.ServerPath,
                Enabled = activeExists,
                NeedsAdmin = _reg.RequiresAdmin(active),
                CanDelete = true,
            };
            if (activeExists && mirrorExists) item.Flags.Add("staleMirror");
            ApplyFileMeta(item);

            if (clsid is not null && IsBlockedIn(RegPath.HklmBlocked, clsid, out _)) item.Flags.Add("machineBlocked");
            if (clsid is not null && IsBlockedIn(RegPath.HkcuBlocked, clsid, out _)) item.Flags.Add("userBlocked");

            item.IsProtected = clsid is not null && KnownItems.ProtectedClsids.Contains(clsid);
            if (item.FilePath is not null)
            {
                item.IsSystem = TextUtil.IsWindowsComponent(item.FilePath);
            }
            else
            {
                item.Flags.Add("unresolved");
                item.Note = "没有找到对应的组件文件，可能是已卸载软件留下的残留，也可能由打包应用提供。";
            }
            if (item.IsProtected) item.IsSystem = true;

            var trimmed = name.Trim();
            string? known = null;
            if (KnownItems.HandlerNames.TryGetValue(trimmed, out var byName)) known = byName;
            else if (clsid is not null && KnownItems.HandlerNames.TryGetValue(clsid, out var byClsid)) known = byClsid;
            item.Title = item.IsSystem && known is not null
                ? known
                : !TextUtil.IsGuid(trimmed) ? trimmed
                : !string.IsNullOrWhiteSpace(ci?.Name) ? ci!.Name!
                : known ?? trimmed;
            if (item.AppName is null && !string.IsNullOrWhiteSpace(ci?.Name) && ci!.Name != item.Title) item.AppName = ci.Name;
            return item;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private sealed record ClsidInfo(string? Name, string? ServerPath);

    private ClsidInfo? ResolveClsid(string clsid)
    {
        foreach (var root in new[] { RegPath.HkcuClasses, RegPath.HklmClasses })
        {
            var path = RegPath.Combine(RegPath.Combine(root, "CLSID"), clsid);
            try
            {
                using var k = _reg.Open(path);
                if (k is null) continue;
                var name = k.GetValue("") as string;
                string? server = null;
                using (var inproc = k.OpenSubKey("InprocServer32"))
                {
                    var v = inproc?.GetValue("") as string;
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        v = Environment.ExpandEnvironmentVariables(v).Trim('"');
                        if (string.Equals(Path.GetFileName(v), "mscoree.dll", StringComparison.OrdinalIgnoreCase)
                            && inproc!.GetValue("CodeBase") is string codeBase
                            && Uri.TryCreate(codeBase, UriKind.Absolute, out var uri) && uri.IsFile)
                            v = uri.LocalPath;
                        server = Path.IsPathRooted(v) ? v : Path.Combine(Environment.SystemDirectory, v);
                    }
                }
                if (server is null)
                {
                    using var local = k.OpenSubKey("LocalServer32");
                    server = TextUtil.ExtractExePath(local?.GetValue("") as string);
                }
                if (server is not null && (!TextUtil.CanProbe(server) || !File.Exists(server))) server = null;
                return new ClsidInfo(string.IsNullOrWhiteSpace(name) ? null : name, server);
            }
            catch
            {
                // 读不到就当作没注册
            }
        }
        return null;
    }

    private static void ApplyFileMeta(MenuItem item)
    {
        var meta = TextUtil.GetFileMeta(item.FilePath);
        if (meta is null) return;
        item.AppName = KnownItems.AliasFor(item.FilePath, meta.Product) ?? meta.Product ?? meta.Description;
        item.Publisher = meta.Company;
    }

    private bool IsBlockedIn(string blockedKey, string clsid, out string? valueName) =>
        BlockedList.Find(_reg, blockedKey, clsid, out valueName);

    // ---------- Win11 新式菜单（打包应用）----------

    public List<MenuItem> ScanPackaged()
    {
        var byId = new Dictionary<string, MenuItem>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<PackageInfo> packages;
        try { packages = env.Packages.GetPackages().ToList(); }
        catch { return []; }

        foreach (var pkg in packages)
        {
            string text;
            try
            {
                if (!File.Exists(pkg.ManifestPath)) continue;
                text = File.ReadAllText(pkg.ManifestPath);
            }
            catch
            {
                continue;
            }
            if (!text.Contains("fileExplorerContextMenus", StringComparison.OrdinalIgnoreCase)) continue;

            XDocument doc;
            try { doc = XDocument.Parse(text); }
            catch { continue; }

            var props = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            var displayName = ResolvePackageString(pkg, props?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value);
            var publisher = ResolvePackageString(pkg, props?.Elements().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value);
            var logo = ResolveLogo(pkg, props?.Elements().FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value);
            if (displayName is null)
                displayName = KnownItems.PackageNames.TryGetValue(pkg.Name, out var known) ? known : pkg.Name.Split('.')[^1];
            else if (KnownItems.PackageNames.TryGetValue(pkg.Name, out var knownZh) && !ContainsCjk(displayName))
                displayName = knownZh;

            foreach (var itemType in doc.Descendants().Where(e => e.Name.LocalName == "ItemType"))
            {
                if (itemType.Parent?.Name.LocalName != "FileExplorerContextMenus") continue;
                var type = itemType.Attribute("Type")?.Value ?? "";
                foreach (var verb in itemType.Elements().Where(e => e.Name.LocalName == "Verb"))
                {
                    var clsid = TextUtil.NormalizeGuid(verb.Attribute("Clsid")?.Value);
                    if (clsid is null) continue;
                    var verbId = verb.Attribute("Id")?.Value ?? "";
                    var id = PackagedPrefix + pkg.FamilyName + "|" + clsid;
                    if (!byId.TryGetValue(id, out var item))
                    {
                        item = new MenuItem
                        {
                            Id = id,
                            Kind = ItemKind.Packaged,
                            Scenes = ["win11"],
                            Location = pkg.FamilyName,
                            LocationLabel = "Win11 新式菜单",
                            Title = displayName,
                            KeyName = verbId,
                            AppName = displayName,
                            Publisher = publisher,
                            FilePath = pkg.RootFolder,
                            IconSource = logo,
                            Clsid = clsid,
                            Hive = "HKCU",
                            RegistryPath = RegPath.HkcuBlocked,
                            FileTypes = [],
                            CanDelete = false,
                            NeedsAdmin = false,
                        };
                        byId[id] = item;
                    }
                    var types = item.FileTypes ??= [];
                    if (!types.Contains(type, StringComparer.OrdinalIgnoreCase)) types.Add(type);
                    if (verbId.Length > 0 && !item.KeyName.Split(", ").Contains(verbId)) item.KeyName += ", " + verbId;
                }
            }
        }

        foreach (var item in byId.Values)
        {
            var user = IsBlockedIn(RegPath.HkcuBlocked, item.Clsid!, out _);
            var machine = IsBlockedIn(RegPath.HklmBlocked, item.Clsid!, out _);
            item.Enabled = !user && !machine;
            if (machine)
            {
                item.Flags.Add("machineBlocked");
                item.NeedsAdmin = _reg.RequiresAdmin(RegPath.HklmBlocked);
            }
            if (user) item.Flags.Add("userBlocked");
            item.Note = "由应用包声明，只能禁用，不能删除；卸载应用后会自动消失。";
        }
        return [.. byId.Values.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static bool ContainsCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    private static string? ResolvePackageString(PackageInfo pkg, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        const string prefix = "ms-resource:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return value.Trim();
        var res = value[prefix.Length..];
        string[] candidates = res.StartsWith("//")
            ? [$"@{{{pkg.FullName}?ms-resource:{res}}}"]
            : res.StartsWith('/')
                ? [$"@{{{pkg.FullName}?ms-resource://{pkg.Name}{res}}}"]
                : [$"@{{{pkg.FullName}?ms-resource://{pkg.Name}/Resources/{res}}}", $"@{{{pkg.FullName}?ms-resource://{pkg.Name}/{res}}}"];
        foreach (var c in candidates)
        {
            var s = Native.LoadIndirectString(c);
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        return null;
    }

    private static string? ResolveLogo(PackageInfo pkg, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        try
        {
            // 图标可能跟清单放在一起，也可能在软件安装目录里
            foreach (var baseDir in new[] { Path.GetDirectoryName(pkg.ManifestPath), pkg.RootFolder }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                var full = Path.Combine(baseDir, relative);
                var dir = Path.GetDirectoryName(full);
                if (dir is null || !Directory.Exists(dir)) continue;
                var baseName = Path.GetFileNameWithoutExtension(full);
                var ext = Path.GetExtension(full);
                var files = Directory.GetFiles(dir, baseName + "*" + ext);
                if (files.Length == 0) continue;
                foreach (var tag in new[] { ".scale-200", ".scale-150", ".scale-125", ".scale-100" })
                {
                    var hit = files.FirstOrDefault(f => Path.GetFileName(f).Contains(tag, StringComparison.OrdinalIgnoreCase)
                                                        && !Path.GetFileName(f).Contains("contrast", StringComparison.OrdinalIgnoreCase));
                    if (hit is not null) return hit;
                }
                return File.Exists(full) ? full : files[0];
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 发送到 ----------

    public List<MenuItem> ScanSendTo()
    {
        var items = new List<MenuItem>();
        try
        {
            if (!Directory.Exists(env.SendToDir)) return items;
            foreach (var file in Directory.GetFiles(env.SendToDir))
            {
                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(file);
                var attr = File.GetAttributes(file);
                var item = new MenuItem
                {
                    Id = SendToPrefix + fileName,
                    Kind = ItemKind.SendTo,
                    Scenes = ["sendto"],
                    Location = "SendTo",
                    LocationLabel = "发送到",
                    KeyName = fileName,
                    Title = Path.GetFileNameWithoutExtension(file),
                    RegistryPath = file,
                    FilePath = file,
                    IconSource = file,
                    Enabled = (attr & FileAttributes.Hidden) == 0,
                    CanDelete = false,
                };
                if (KnownItems.SystemSendToExts.TryGetValue(ext, out var zh))
                {
                    item.IsSystem = true;
                    item.Title = zh;
                    item.AppName = "Windows";
                }
                else if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var target = ResolveShortcut(file);
                    if (target is not null)
                    {
                        item.Command = target;
                        item.IconSource = target;
                        var meta = TextUtil.GetFileMeta(target);
                        item.AppName = meta?.Product ?? meta?.Description;
                        item.Publisher = meta?.Company;
                        item.IsSystem = TextUtil.IsWindowsComponent(target);
                    }
                }
                items.Add(item);
            }
        }
        catch
        {
            // 目录读不了就返回已有结果
        }
        return [.. items.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static string? ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic sc = shell.CreateShortcut(lnk);
                string target = sc.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            return null;
        }
    }

    // ---------- 新建 ----------

    public List<MenuItem> ScanShellNew()
    {
        var items = new List<MenuItem>();
        foreach (var root in HiveClassRoots)
        {
            foreach (var ext in SubKeyNames(root))
            {
                if (!ext.StartsWith('.')) continue;
                var extPath = RegPath.Combine(root, ext);
                AddShellNew(items, root, ext, extPath);
                foreach (var sub in SubKeyNames(extPath))
                {
                    if (sub.Equals("ShellNew", StringComparison.OrdinalIgnoreCase) || sub.Equals("-ShellNew", StringComparison.OrdinalIgnoreCase)) continue;
                    AddShellNew(items, root, ext, RegPath.Combine(extPath, sub));
                }
            }
        }
        return [.. items.OrderBy(i => i.IsSystem).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    private void AddShellNew(List<MenuItem> items, string hiveRoot, string ext, string parent)
    {
        var item = ReadShellNew(hiveRoot, ext, parent);
        if (item is not null) items.Add(item);
    }

    private MenuItem? ReadShellNew(string hiveRoot, string ext, string parent)
    {
        var active = RegPath.Combine(parent, "ShellNew");
        var mirror = RegPath.Combine(parent, "-ShellNew");
        try
        {
            var activeExists = HasShellNewValues(active);
            var mirrorExists = HasShellNewValues(mirror);
            if (!activeExists && !mirrorExists) return null;

            var progId = RegPath.Leaf(parent).Equals(ext, StringComparison.OrdinalIgnoreCase)
                ? ReadClassString(ext, "")
                : RegPath.Leaf(parent);
            string? typeName = null;
            string? openCommand = null;
            if (!string.IsNullOrWhiteSpace(progId))
            {
                typeName = TextUtil.ResolveText(ReadClassString(progId, "FriendlyTypeName")) ?? TextUtil.ResolveText(ReadClassString(progId, ""));
                openCommand = ReadClassString(progId + @"\shell\open\command", "");
            }
            var exe = TextUtil.ExtractExePath(openCommand);
            var item = new MenuItem
            {
                Id = ShellNewPrefix + active,
                Kind = ItemKind.ShellNew,
                Scenes = ["shellnew"],
                Location = ext,
                LocationLabel = "新建",
                KeyName = ext,
                Title = typeName ?? ext,
                Hive = RegPath.Hive(hiveRoot),
                RegistryPath = activeExists ? active : mirror,
                FilePath = exe,
                IconSource = exe,
                Command = openCommand,
                Enabled = activeExists,
                NeedsAdmin = _reg.RequiresAdmin(active),
                CanDelete = true,
                IsSystem = KnownItems.SystemShellNewExts.Contains(ext),
            };
            if (activeExists && mirrorExists) item.Flags.Add("staleMirror");
            ApplyFileMeta(item);
            return item;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private bool HasShellNewValues(string path)
    {
        using var k = _reg.Open(path);
        if (k is null) return false;
        var names = k.GetValueNames();
        return names.Any(n => ShellNewValueNames.Contains(n, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>按“当前用户优先、其次所有用户”读取 Classes 下的字符串值。</summary>
    private string? ReadClassString(string classSubPath, string valueName)
    {
        foreach (var root in new[] { RegPath.HkcuClasses, RegPath.HklmClasses })
        {
            try
            {
                var s = _reg.ReadString(RegPath.Combine(root, classSubPath), valueName);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            catch
            {
                // 继续看下一个根
            }
        }
        return null;
    }

    // ---------- 按 Id 重新定位（应用改动前读取最新状态）----------

    public MenuItem? Locate(string id)
    {
        if (id.StartsWith(VerbPrefix, StringComparison.Ordinal))
        {
            var path = id[VerbPrefix.Length..];
            if (!SplitClassPath(path, @"\shell\", out var hiveRoot, out var cls, out var name)) return null;
            return ReadVerb(hiveRoot, DefFor(cls), name);
        }
        if (id.StartsWith(ShellExPrefix, StringComparison.Ordinal))
        {
            var path = id[ShellExPrefix.Length..];
            if (!SplitClassPath(path, @"\shellex\ContextMenuHandlers\", out var hiveRoot, out var cls, out var name)) return null;
            return ReadHandler(hiveRoot, DefFor(cls), ItemKind.ShellEx, "ContextMenuHandlers", name);
        }
        if (id.StartsWith(DragDropPrefix, StringComparison.Ordinal))
        {
            var path = id[DragDropPrefix.Length..];
            if (!SplitClassPath(path, @"\shellex\DragDropHandlers\", out var hiveRoot, out var cls, out var name)) return null;
            return ReadHandler(hiveRoot, DefFor(cls), ItemKind.DragDrop, "DragDropHandlers", name);
        }
        if (id.StartsWith(ShellNewPrefix, StringComparison.Ordinal))
        {
            var active = id[ShellNewPrefix.Length..];
            if (!RegPath.Leaf(active).Equals("ShellNew", StringComparison.OrdinalIgnoreCase)) return null;
            var parent = RegPath.Parent(active);
            var hiveRoot = HiveRootOf(parent);
            if (hiveRoot is null) return null;
            var rest = parent[(hiveRoot.Length + 1)..];
            var ext = rest.Split('\\')[0];
            return ReadShellNew(hiveRoot, ext, parent);
        }
        if (id.StartsWith(PackagedPrefix, StringComparison.Ordinal))
            return ScanPackaged().FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (id.StartsWith(SendToPrefix, StringComparison.Ordinal))
            return ScanSendTo().FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static string? HiveRootOf(string path) =>
        HiveClassRoots.FirstOrDefault(r => RegPath.IsUnder(path, r));

    private static bool SplitClassPath(string path, string marker, out string hiveRoot, out string cls, out string name)
    {
        hiveRoot = cls = name = "";
        var root = HiveRootOf(path);
        if (root is null) return false;
        var rest = path[(root.Length + 1)..];
        var i = rest.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i <= 0) return false;
        hiveRoot = root;
        cls = rest[..i];
        name = rest[(i + marker.Length)..];
        return name.Length > 0 && !name.Contains('\\');
    }
}
