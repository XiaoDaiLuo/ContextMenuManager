namespace ContextMenuManager.Core;

/// <summary>
/// 需要管理员权限的步骤在执行前要过的检查。
///
/// 历史记录（batch.json）放在用户目录里，任何以当前用户身份运行的程序都能改它；
/// 而撤销时会按里面记录的逆操作去写 HKLM。所以提权的一端不能照单全收，只接受下面这几种
/// “不带外来内容”的动作，而且形态必须精确：
///   - 在静态项上写入或删除隐藏标记（ProgrammaticAccessOnly 等）；
///   - 在所有用户的 Blocked 列表里增删一个 CLSID；
///   - 把扩展 / 新建项在“启用位置”和它对应的“禁用镜像位置”之间移动；
///   - 把某一项整个移入回收位置，或从回收位置移回来；
///   - 创建或清理空的镜像容器。
/// 直接删除、按快照重建注册表项这两种动作一律拒绝：删除改为“移入回收位置”，恢复就是移回来，
/// 内容始终来自只有管理员能写的地方，而不是来自用户目录里的文件。
/// 核心系统项（打开、以管理员身份运行、打开方式等）在这里再拦一次。
/// </summary>
public static class StepPolicy
{
    public const string HklmRecycle = @"HKLM\SOFTWARE\ContextMenuManager\Deleted";
    public const string HkcuRecycle = @"HKCU\Software\ContextMenuManager\Deleted";

    private static readonly string[] HandlerContainers = ["ContextMenuHandlers", "DragDropHandlers"];

    /// <summary>为要删除的项分配一个回收位置（和原项在同一个注册表根下）。</summary>
    public static string NewRecyclePath(string forPath) =>
        RegPath.Combine(RegPath.Hive(forPath) == "HKLM" ? HklmRecycle : HkcuRecycle, Guid.NewGuid().ToString("N"));

    private enum Kind { None, Verb, Handler, HandlerMirror, MirrorContainer, ShellNew, ShellNewMirror, Blocked, Recycle }

    private sealed record Target(Kind Kind, string Name = "", string? Counterpart = null)
    {
        public bool IsItem => Kind is Kind.Verb or Kind.Handler or Kind.HandlerMirror or Kind.ShellNew or Kind.ShellNewMirror;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static Target Classify(string path)
    {
        var none = new Target(Kind.None);
        if (path.Contains("..") || RegPath.Hive(path) != "HKLM") return none;
        if (Eq(path, RegPath.HklmBlocked)) return new Target(Kind.Blocked);
        if (RegPath.IsUnder(path, HklmRecycle))
        {
            var id = path[(HklmRecycle.Length + 1)..];
            return id.Length == 32 && id.All(Uri.IsHexDigit) ? new Target(Kind.Recycle, id) : none;
        }
        if (!RegPath.IsUnder(path, RegPath.HklmClasses)) return none;

        var parts = path[(RegPath.HklmClasses.Length + 1)..].Split('\\');
        var n = parts.Length;
        if (parts.Any(p => p.Length == 0)) return none;
        var parent = RegPath.Parent(path);

        if (n >= 3 && Eq(parts[n - 2], "shell")) return new Target(Kind.Verb, parts[n - 1]);

        if (n >= 4 && Eq(parts[n - 3], "shellex"))
        {
            var shellex = RegPath.Parent(parent);
            foreach (var c in HandlerContainers)
            {
                if (Eq(parts[n - 2], c))
                    return new Target(Kind.Handler, parts[n - 1], RegPath.Combine(RegPath.Combine(shellex, "-" + c), parts[n - 1]));
                if (Eq(parts[n - 2], "-" + c))
                    return new Target(Kind.HandlerMirror, parts[n - 1], RegPath.Combine(RegPath.Combine(shellex, c), parts[n - 1]));
            }
        }

        if (n >= 3 && Eq(parts[n - 2], "shellex") && HandlerContainers.Any(c => Eq(parts[n - 1], "-" + c)))
            return new Target(Kind.MirrorContainer, parts[n - 1]);

        if (n >= 2 && Eq(parts[n - 1], "ShellNew")) return new Target(Kind.ShellNew, parts[n - 1], RegPath.Combine(parent, "-ShellNew"));
        if (n >= 2 && Eq(parts[n - 1], "-ShellNew")) return new Target(Kind.ShellNewMirror, parts[n - 1], RegPath.Combine(parent, "ShellNew"));
        return none;
    }

    private static bool IsMarker(string name) =>
        Scanner.HiddenMarkerNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>这个位置上的项是不是核心系统项。</summary>
    private static bool IsProtected(IRegistryEnv env, Target t, string path)
    {
        if (t.Kind == Kind.Verb) return KnownItems.ProtectedVerbs.Contains(t.Name);
        if (t.Kind is Kind.Handler or Kind.HandlerMirror)
        {
            string? clsid;
            try { clsid = TextUtil.NormalizeGuid(t.Name) ?? TextUtil.NormalizeGuid(env.ReadString(path)); }
            catch { return true; } // 读不到就按核心项处理，宁可拒绝
            return clsid is not null && KnownItems.ProtectedClsids.Contains(clsid);
        }
        return false;
    }

    /// <summary>检查一个需要管理员权限的步骤。返回 null 表示允许，否则是拒绝的原因。</summary>
    public static string? Check(IRegistryEnv env, Step step)
    {
        const string outOfScope = "拒绝执行：这一步不在右键菜单管理允许的范围内（";
        const string core = "拒绝执行：系统核心菜单项不允许禁用或删除（";

        switch (step)
        {
            case SetValueStep s:
            {
                var t = Classify(s.Key);
                if (t.Kind == Kind.Blocked && TextUtil.NormalizeGuid(s.Value.Name) is not null) return null;
                if (t.Kind == Kind.Verb && IsMarker(s.Value.Name))
                    return IsProtected(env, t, s.Key) ? core + s.Key + "）。" : null;
                break;
            }

            case DeleteValueStep d:
            {
                var t = Classify(d.Key);
                if (t.Kind == Kind.Blocked && TextUtil.NormalizeGuid(d.Name) is not null) return null;
                if (t.Kind == Kind.Verb && IsMarker(d.Name)) return null; // 去掉隐藏标记是恢复，不拦
                break;
            }

            case MoveKeyStep m:
            {
                var from = Classify(m.From);
                var to = Classify(m.To);
                // 启用位置 ↔ 它自己的禁用镜像
                if (from.Counterpart is not null && Eq(from.Counterpart, m.To))
                {
                    var disabling = from.Kind is Kind.Handler or Kind.ShellNew;
                    return disabling && IsProtected(env, from, m.From) ? core + m.From + "）。" : null;
                }
                // 移入回收位置（相当于删除）
                if (to.Kind == Kind.Recycle && from.IsItem)
                    return IsProtected(env, from, m.From) ? core + m.From + "）。" : null;
                // 从回收位置移回来（撤销删除）
                if (from.Kind == Kind.Recycle && to.IsItem)
                    return to.Kind == Kind.Verb && KnownItems.ProtectedVerbs.Contains(to.Name) ? core + m.To + "）。" : null;
                break;
            }

            case CreateKeyStep c when Classify(c.Key).Kind == Kind.MirrorContainer:
            case DeleteEmptyKeyStep e when Classify(e.Key).Kind == Kind.MirrorContainer:
                return null;
        }
        return outOfScope + step.Describe() + "）。";
    }
}
