using System.Runtime.InteropServices;
using System.Text;

// MenuProbe：不显示任何界面，直接向外壳要某个文件/文件夹的经典右键菜单（“显示更多选项”那一层），
// 把菜单项文字打印出来。用来验证禁用/恢复确实改变了菜单内容。
//
// 用法：MenuProbe <路径> [--background] [--shift] [--out <文件>]
//   --background  取文件夹空白处的菜单，而不是文件夹本身的
//   --shift       相当于按住 Shift 右键（包含扩展动词）
//
// 注意：它会把第三方外壳扩展加载进本进程，只应该当作一次性的开发工具运行。

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? path = null, outFile = null;
        bool background = false, shift = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--background": background = true; break;
                case "--shift": shift = true; break;
                case "--out": outFile = args[++i]; break;
                default: path = args[i]; break;
            }
        }
        if (path is null)
        {
            Console.Error.WriteLine("用法：MenuProbe <路径> [--background] [--shift] [--out <文件>]");
            return 1;
        }

        var sb = new StringBuilder();
        int code;
        try
        {
            Probe(Path.GetFullPath(path), background, shift, sb);
            code = 0;
        }
        catch (Exception ex)
        {
            sb.AppendLine("错误：" + ex.Message);
            code = 2;
        }
        if (outFile is not null) File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
        else { Console.OutputEncoding = Encoding.UTF8; Console.Write(sb.ToString()); }
        return code;
    }

    private static void Probe(string path, bool background, bool shift, StringBuilder sb)
    {
        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _));
        object? cmObj = null;
        var hMenu = IntPtr.Zero;
        try
        {
            if (background)
            {
                Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out var desktop));
                var iidFolder = typeof(IShellFolder).GUID;
                Marshal.ThrowExceptionForHR(desktop.BindToObject(pidl, IntPtr.Zero, ref iidFolder, out var folderObj));
                var folder = (IShellFolder)folderObj;
                var iidCm = typeof(IContextMenu).GUID;
                Marshal.ThrowExceptionForHR(folder.CreateViewObject(IntPtr.Zero, ref iidCm, out cmObj));
            }
            else
            {
                var iidFolder = typeof(IShellFolder).GUID;
                Marshal.ThrowExceptionForHR(SHBindToParent(pidl, ref iidFolder, out var parentObj, out var child));
                var parent = (IShellFolder)parentObj;
                var iidCm = typeof(IContextMenu).GUID;
                Marshal.ThrowExceptionForHR(parent.GetUIObjectOf(IntPtr.Zero, 1, [child], ref iidCm, IntPtr.Zero, out cmObj));
            }

            var cm = (IContextMenu)cmObj;
            hMenu = CreatePopupMenu();
            const uint CMF_NORMAL = 0, CMF_EXTENDEDVERBS = 0x100, CMF_EXPLORE = 0x4;
            var hr = cm.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE | (shift ? CMF_EXTENDEDVERBS : 0));
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            sb.AppendLine($"# {(background ? "空白处" : "对象")}: {path}{(shift ? "  (Shift)" : "")}");
            Dump(cm, hMenu, 0, sb);
        }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (cmObj is not null) Marshal.FinalReleaseComObject(cmObj);
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static void Dump(IContextMenu cm, IntPtr hMenu, int depth, StringBuilder sb)
    {
        var count = GetMenuItemCount(hMenu);
        for (var i = 0; i < count; i++)
        {
            var info = new MENUITEMINFO { cbSize = Marshal.SizeOf<MENUITEMINFO>(), fMask = 0x100 /* FTYPE */ | 0x2 /* ID */ | 0x4 /* SUBMENU */ };
            if (!GetMenuItemInfo(hMenu, (uint)i, true, ref info)) continue;
            if ((info.fType & 0x800) != 0) continue; // 分隔线

            var text = new StringBuilder(512);
            GetMenuString(hMenu, (uint)i, text, text.Capacity, 0x400 /* MF_BYPOSITION */);
            var verb = "";
            if (info.hSubMenu == IntPtr.Zero && info.wID >= 1 && info.wID <= 0x7FFF)
            {
                var buf = Marshal.AllocCoTaskMem(520);
                try
                {
                    Marshal.WriteInt16(buf, 0);
                    if (cm.GetCommandString((UIntPtr)(info.wID - 1), 0x4 /* GCS_VERBW */, IntPtr.Zero, buf, 256) == 0)
                        verb = Marshal.PtrToStringUni(buf) ?? "";
                }
                catch
                {
                    // 有些扩展不实现 GetCommandString
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buf);
                }
            }
            var label = text.Length > 0 ? text.ToString().Replace("&", "") : "(自绘项，无文字)";
            sb.AppendLine($"{new string(' ', depth * 2)}- {label}{(verb.Length > 0 ? $"  [{verb}]" : "")}{(info.hSubMenu != IntPtr.Zero ? "  >" : "")}");

            if (info.hSubMenu != IntPtr.Zero && depth < 1)
            {
                try
                {
                    if (cm is IContextMenu2 cm2) cm2.HandleMenuMsg(0x117 /* WM_INITMENUPOPUP */, info.hSubMenu, (IntPtr)i);
                }
                catch
                {
                    // 子菜单填充失败就只列顶层
                }
                Dump(cm, info.hSubMenu, depth + 1, sb);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr hMenu, uint item, StringBuilder text, int max, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public int cbSize;
        public uint fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attrs);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr reserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }
}
