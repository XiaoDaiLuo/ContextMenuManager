using Microsoft.Win32;

namespace ContextMenuManager.Core;

/// <summary>
/// 往沙箱里写一套仿真的右键菜单注册，供单元测试和界面开发使用。
/// 沙箱整体位于 HKCU\Software\ContextMenuManager.Sandbox\&lt;名字&gt; 下，不影响真实菜单。
/// </summary>
public static class SandboxSeeder
{
    public const string SevenZipClsid = "{23170F69-40C1-278A-1000-000100020000}";
    public const string YunClsid = "{6D85624F-305A-491D-8848-C1927AA0D790}";
    public const string ClipchampClsid = "{8AB635F8-9A67-4698-AB99-784AD929F3B4}";
    public const string TerminalClsid = "{9F156763-7844-4DC4-B2B1-901F640F5155}";
    public const string MachineBlockedClsid = "{AAAAAAAA-0000-0000-0000-000000000001}";

    public static AppEnv CreateSeeded(string name, string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var packages = WritePackages(Path.Combine(dataDir, "packages"));
        var env = AppEnv.CreateSandbox(name, dataDir, new StaticPackageSource(packages));
        ((SandboxRegistryEnv)env.Reg).Destroy();
        Seed(env);
        return env;
    }

    public static void Seed(AppEnv env)
    {
        var reg = env.Reg;
        const string lm = RegPath.HklmClasses;
        const string cu = RegPath.HkcuClasses;
        var sys = Environment.SystemDirectory;

        // ----- 所有文件 -----
        Verb(reg, lm, "*", "ToDeskSendFile", "使用 ToDesk 快传文件", @"""C:\Program Files\ToDesk\ToDesk.exe"" --send ""%1""");
        Verb(reg, lm, "*", "DemoEditor", null, $@"""{sys}\notepad.exe"" ""%1""", k =>
        {
            k.SetValue("MUIVerb", "用 Demo 编辑器打开(&E)");
            k.SetValue("Extended", "");
        });
        Verb(reg, lm, "*", "pintohomefile", "@shell32.dll,-51608", null);
        Verb(reg, lm, "*", "removeproperties", null, null, k => k.SetValue("ProgrammaticAccessOnly", ""));
        Handler(reg, lm, "*", "ContextMenuHandlers", "7-Zip", SevenZipClsid, "7-Zip Shell Extension", @"C:\Program Files\7-Zip\7-zip.dll");
        Handler(reg, lm, "*", "ContextMenuHandlers", "YunShellExt", YunClsid, "YunShellExtContextMenu Class", @"C:\NoSuchDir\YunShellExtV164.dll");
        Handler(reg, lm, "*", "ContextMenuHandlers", "Sharing", "{F81E9010-6EA4-11CE-A7FF-00AA003CA9F6}", "Shell extensions for sharing", $@"{sys}\ntshrui.dll");
        Handler(reg, lm, "*", "ContextMenuHandlers", "Open With", "{09799AFB-AD67-11D1-ABCD-00C04FC30936}", "Open With Context Menu Handler", $@"{sys}\shell32.dll");
        Handler(reg, cu, "*", "ContextMenuHandlers", " FileSyncEx", "{CB3D0F55-BC2C-4C1A-85ED-23ED75B5106B}", "FileSyncEx", @"C:\NoSuchDir\FileSyncShell64.dll", clsidRoot: cu);

        // ----- 文件和文件夹 -----
        Handler(reg, lm, "AllFilesystemObjects", "ContextMenuHandlers", "{596AB062-B4D2-4215-9F74-E9109B0A8153}", null,
            "Previous Versions Property Page", $@"{sys}\twext.dll", keyIsClsid: true);

        // ----- 文件夹 -----
        Verb(reg, lm, "Directory", "git_shell", "Open Git Ba&sh here", @"""C:\Program Files\Git\git-bash.exe"" ""--cd=%1""",
            k => k.SetValue("Icon", @"C:\Program Files\Git\git-bash.exe"));
        Verb(reg, lm, "Directory", "cmd", "@shell32.dll,-8506", @"cmd.exe /s /k pushd ""%V""", k => k.SetValue("Extended", ""));
        Verb(reg, lm, "Directory", "find", null, null, k => k.SetValue("LegacyDisable", ""));
        Verb(reg, lm, "Directory", "ToDeskSendFile", "使用 ToDesk 快传文件", @"""C:\Program Files\ToDesk\ToDesk.exe"" --send ""%1""");
        Verb(reg, cu, "Directory", "MyTool", "用我的工具打开", @"""C:\Tools\mytool.exe"" ""%1""");
        Handler(reg, lm, "Directory", "ContextMenuHandlers", "7-Zip", SevenZipClsid, null, null);
        Handler(reg, lm, "Directory", "ContextMenuHandlers", "YunShellExt", YunClsid, null, null);
        Handler(reg, lm, "Directory", "-ContextMenuHandlers", "OldDisabledExt", "{11111111-2222-3333-4444-555555555555}", null, null);
        Handler(reg, cu, "Directory", "ContextMenuHandlers", "  Quark.ContextMenuExt", "{E2529821-0B54-4D5A-9268-C6AD72C6E1D0}",
            "Quark.ContextMenuExt Class", @"C:\NoSuchDir\QuarkMenuExtension.dll", clsidRoot: cu);
        Handler(reg, lm, "Directory", "DragDropHandlers", "7-Zip", SevenZipClsid, null, null);
        Verb(reg, lm, "Folder", "open", null, null);
        Verb(reg, lm, "Folder", "pintohome", "@shell32.dll,-51601", null);

        // ----- 空白处 -----
        Verb(reg, lm, @"Directory\Background", "git_gui", "Open Git &GUI here", @"""C:\Program Files\Git\cmd\git-gui.exe"" ""--working-dir"" ""%v.""");
        Handler(reg, lm, @"Directory\Background", "ContextMenuHandlers", "NvCplDesktopContext", "{3D1975AF-48C6-4F8E-A182-BE0E08FA86A9}",
            "NVIDIA CPL Context Menu Extension", @"C:\NoSuchDir\nvshext.dll");
        Verb(reg, lm, "DesktopBackground", "Display", $@"@{sys}\display.dll,-4", $@"{sys}\rundll32.exe display.dll,ShowAdapterSettings");

        // ----- 磁盘、其他 -----
        Handler(reg, lm, "Drive", "ContextMenuHandlers", "EPP", "{09A47860-11B0-4DA5-AFA5-26D86198A780}", null, @"C:\NoSuchDir\shellext.dll");
        Verb(reg, lm, "exefile", "runas", null, null);

        // ----- 指定文件类型 .jpg -----
        using (var k = reg.Create(lm + @"\.jpg"))
        {
            k.SetValue("", "jpegfile");
            k.SetValue("PerceivedType", "image");
        }
        Verb(reg, lm, "jpegfile", "DemoViewer", "用 Demo 看图打开", @"""C:\Tools\viewer.exe"" ""%1""");
        Verb(reg, lm, @"SystemFileAssociations\image", "DemoConvert", "转换图片格式", @"""C:\Tools\convert.exe"" ""%1""");
        Handler(reg, lm, @"SystemFileAssociations\.jpg", "ContextMenuHandlers", "DemoThumb", "{22222222-3333-4444-5555-666666666666}", null, null);

        // ----- 新建 -----
        using (var k = reg.Create(lm + @"\.txt")) k.SetValue("", "txtfile");
        using (var k = reg.Create(lm + @"\txtfile")) k.SetValue("", "文本文档");
        using (var k = reg.Create(lm + @"\.txt\ShellNew")) k.SetValue("NullFile", "");
        using (var k = reg.Create(lm + @"\.docx")) k.SetValue("", "Word.Document.12");
        using (var k = reg.Create(lm + @"\Word.Document.12")) k.SetValue("", "Microsoft Word 文档");
        using (var k = reg.Create(lm + @"\.docx\Word.Document.12\ShellNew")) k.SetValue("FileName", "template.docx");
        using (var k = reg.Create(cu + @"\.demo")) k.SetValue("", "demofile");
        using (var k = reg.Create(cu + @"\demofile")) k.SetValue("", "Demo 文档");
        using (var k = reg.Create(cu + @"\.demo\-ShellNew")) k.SetValue("NullFile", "");

        // ----- Blocked 列表 -----
        using (var k = reg.Create(RegPath.HklmBlocked))
        {
            k.SetValue("{9421DD08-935F-4701-A9CA-22DF90AC4EA6}", "");
            k.SetValue(MachineBlockedClsid, "blocked by policy");
        }

        // ----- 发送到 -----
        Directory.CreateDirectory(env.SendToDir);
        foreach (var f in Directory.GetFiles(env.SendToDir))
        {
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
        File.WriteAllText(Path.Combine(env.SendToDir, "Desktop (create shortcut).DeskLink"), "");
        File.WriteAllText(Path.Combine(env.SendToDir, "MyUploader.lnk"), "");
        var hidden = Path.Combine(env.SendToDir, "Hidden Tool.lnk");
        File.WriteAllText(hidden, "");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(env.SendToDir, "desktop.ini"), "");
    }

    private static void Verb(IRegistryEnv reg, string root, string cls, string name, string? text, string? command, Action<RegistryKey>? extra = null)
    {
        var path = $@"{root}\{cls}\shell\{name}";
        using (var k = reg.Create(path))
        {
            if (text is not null) k.SetValue("", text);
            extra?.Invoke(k);
        }
        if (command is not null)
        {
            using var c = reg.Create(path + @"\command");
            c.SetValue("", command);
        }
    }

    private static void Handler(IRegistryEnv reg, string root, string cls, string container, string name, string? clsid,
        string? clsidName, string? dll, bool keyIsClsid = false, string? clsidRoot = null)
    {
        using (var k = reg.Create($@"{root}\{cls}\shellex\{container}\{name}"))
        {
            if (!keyIsClsid && clsid is not null) k.SetValue("", clsid);
        }
        var id = keyIsClsid ? name : clsid;
        if (id is null || (clsidName is null && dll is null)) return;
        var clsPath = $@"{clsidRoot ?? RegPath.HklmClasses}\CLSID\{id}";
        using (var k = reg.Create(clsPath))
        {
            if (clsidName is not null) k.SetValue("", clsidName);
        }
        if (dll is not null)
        {
            using var s = reg.Create(clsPath + @"\InprocServer32");
            s.SetValue("", dll);
        }
    }

    private static List<PackageInfo> WritePackages(string dir)
    {
        var list = new List<PackageInfo>();
        Add("Clipchamp.Clipchamp_4.5.0.0_x64__yxz26nhyzhsrt", "Clipchamp", "Microsoft Corp.",
            [(".mp4", "EditWith", ClipchampClsid), (".mov", "EditWith", ClipchampClsid), (".mkv", "EditWith", ClipchampClsid)]);
        Add("Microsoft.WindowsTerminal_1.23.0.0_x64__8wekyb3d8bbwe", "Windows Terminal", "Microsoft Corporation",
            [("Directory", "OpenTerminalHere", TerminalClsid), (@"Directory\Background", "OpenTerminalHere", TerminalClsid)]);
        Add("Demo.Blocked_1.0.0.0_x64__abcdefgh12345", "已被策略阻止的示例", "Demo Inc.",
            [("*", "DemoVerb", MachineBlockedClsid)]);
        return list;

        void Add(string fullName, string displayName, string publisher, (string type, string verb, string clsid)[] decls)
        {
            var root = Path.Combine(dir, fullName);
            Directory.CreateDirectory(root);
            var types = string.Join("\n", decls.Select(d =>
                $"""            <desktop5:ItemType Type="{d.type}"><desktop5:Verb Id="{d.verb}" Clsid="{d.clsid.Trim('{', '}')}" /></desktop5:ItemType>"""));
            File.WriteAllText(Path.Combine(root, "AppxManifest.xml"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                         xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
                         xmlns:desktop5="http://schemas.microsoft.com/appx/manifest/desktop/windows10/5">
                  <Properties>
                    <DisplayName>{displayName}</DisplayName>
                    <PublisherDisplayName>{publisher}</PublisherDisplayName>
                    <Logo>Assets\StoreLogo.png</Logo>
                  </Properties>
                  <Applications>
                    <Application Id="App">
                      <Extensions>
                        <desktop4:Extension Category="windows.fileExplorerContextMenus">
                          <desktop4:FileExplorerContextMenus>
                {types}
                          </desktop4:FileExplorerContextMenus>
                        </desktop4:Extension>
                      </Extensions>
                    </Application>
                  </Applications>
                </Package>
                """.TrimStart());
            list.Add(new PackageInfo(fullName, root));
        }
    }
}
