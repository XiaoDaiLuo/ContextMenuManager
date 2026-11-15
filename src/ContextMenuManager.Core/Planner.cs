using Microsoft.Win32;

namespace ContextMenuManager.Core;

public sealed class Plan
{
    public List<PlannedOp> Ops { get; } = [];
    /// <summary>改动前要用 reg.exe 导出备份的项。</summary>
    public List<string> BackupKeys { get; } = [];
    public string? Error { get; set; }
    public bool NoOp => Error is null && Ops.Count == 0;

    public void Add(Step @do, Step undo) => Ops.Add(new PlannedOp(@do, undo));
}

/// <summary>
/// 把“禁用 / 启用 / 删除某一项”翻译成可逆的步骤。只读注册表，不写。
/// 各类来源的禁用方式：
///   静态项    → 写入 ProgrammaticAccessOnly（只从菜单里隐藏，程序仍可调用该动词）
///   扩展      → 把子键移到同级的 -ContextMenuHandlers / -DragDropHandlers
///   Win11 项  → 把 CLSID 写入当前用户的 Shell Extensions\Blocked
///   发送到    → 给文件加隐藏属性
///   新建      → 把 ShellNew 改名为 -ShellNew
/// 删除不是真的抹掉：整个键被移到本工具的回收位置（<see cref="StepPolicy.HklmRecycle"/>），撤销时原样移回。
/// </summary>
public static class Planner
{
    public const string BlockedMarker = "ContextMenuManager";
    private const string HideValue = "ProgrammaticAccessOnly";

    public static Plan Build(AppEnv env, MenuItem item, ActionKind action)
    {
        var plan = new Plan();

        // 把已经被挪到禁用镜像位置的项移回去，是恢复原状，不会降低安全性；
        // 所以即使是系统项、核心项也允许（比如别的工具把“打开方式”禁用了，可以在这里恢复）。
        // 静态项不算：系统里有不少动词本来就是设计成隐藏的，去掉标记反而是改动。
        var restoring = action == ActionKind.Enable && !item.Enabled
                        && item.Kind is ItemKind.ShellEx or ItemKind.DragDrop or ItemKind.ShellNew;
        if (!restoring)
        {
            if (item.IsProtected)
            {
                plan.Error = "这是系统核心菜单项，不允许改动。";
                return plan;
            }
            if (item.IsSystem && !env.AllowSystem)
            {
                plan.Error = "这是 Windows 自带项，默认锁定。需要先在设置里打开“允许修改系统项”。";
                return plan;
            }
        }
        if (action == ActionKind.Delete && !item.CanDelete)
        {
            plan.Error = "这一项不支持删除，只能禁用。";
            return plan;
        }

        try
        {
            switch (item.Kind)
            {
                case ItemKind.Verb: PlanVerb(env.Reg, item, action, plan); break;
                case ItemKind.ShellEx: PlanMirrored(env.Reg, HandlerPaths(item, "ContextMenuHandlers"), action, plan, true); break;
                case ItemKind.DragDrop: PlanMirrored(env.Reg, HandlerPaths(item, "DragDropHandlers"), action, plan, true); break;
                case ItemKind.ShellNew: PlanMirrored(env.Reg, ShellNewPaths(item), action, plan, false); break;
                case ItemKind.Packaged: PlanPackaged(env.Reg, item, action, plan); break;
                case ItemKind.SendTo: PlanSendTo(item, action, plan); break;
                default: plan.Error = "不支持的菜单项类型。"; break;
            }
        }
        catch (Exception ex)
        {
            plan.Error = GroupRunner.Explain(ex);
        }
        return plan;
    }

    /// <summary>把一个键整个移入回收位置；逆操作是原样移回。</summary>
    private static void Recycle(Plan plan, string key)
    {
        var bin = StepPolicy.NewRecyclePath(key);
        plan.Add(new MoveKeyStep { From = key, To = bin }, new MoveKeyStep { From = bin, To = key });
    }

    private static void PlanVerb(IRegistryEnv reg, MenuItem item, ActionKind action, Plan plan)
    {
        var key = item.Id[Scanner.VerbPrefix.Length..];
        using var k = reg.Open(key);
        if (k is null)
        {
            plan.Error = "注册表项已经不存在，请刷新列表。";
            return;
        }
        plan.BackupKeys.Add(key);

        switch (action)
        {
            case ActionKind.Disable:
                if (k.GetValueNames().Any(n => Scanner.HiddenMarkerNames.Contains(n, StringComparer.OrdinalIgnoreCase))) return;
                var marker = RegValue.String(HideValue, "");
                plan.Add(new SetValueStep { Key = key, Value = marker },
                         new DeleteValueStep { Key = key, Name = HideValue, Expected = marker });
                break;

            case ActionKind.Enable:
                foreach (var name in k.GetValueNames())
                {
                    if (!Scanner.HiddenMarkerNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    var original = RegValue.Read(k, name)!;
                    plan.Add(new DeleteValueStep { Key = key, Name = name, Expected = original },
                             new SetValueStep { Key = key, Value = original });
                }
                break;

            case ActionKind.Delete:
                Recycle(plan, key);
                break;
        }
    }

    private sealed record MirrorPaths(string Active, string Mirror);

    private static MirrorPaths HandlerPaths(MenuItem item, string container)
    {
        var active = item.Id[(item.Id.IndexOf('|') + 1)..];
        var shellex = RegPath.Parent(RegPath.Parent(active));
        return new MirrorPaths(active, RegPath.Combine(RegPath.Combine(shellex, "-" + container), RegPath.Leaf(active)));
    }

    private static MirrorPaths ShellNewPaths(MenuItem item)
    {
        var active = item.Id[Scanner.ShellNewPrefix.Length..];
        return new MirrorPaths(active, RegPath.Combine(RegPath.Parent(active), "-ShellNew"));
    }

    /// <summary>
    /// “启用位置 ↔ 禁用镜像位置”这一类的通用计划。<paramref name="hasContainer"/> 为 true 时，
    /// 镜像位置在一个专门的容器键下（-ContextMenuHandlers），容器按需创建，空了以后清理。
    /// </summary>
    private static void PlanMirrored(IRegistryEnv reg, MirrorPaths p, ActionKind action, Plan plan, bool hasContainer)
    {
        var activeExists = reg.KeyExists(p.Active);
        var mirrorExists = reg.KeyExists(p.Mirror);
        if (!activeExists && !mirrorExists)
        {
            plan.Error = "注册表项已经不存在，请刷新列表。";
            return;
        }
        if (activeExists) plan.BackupKeys.Add(p.Active);
        if (mirrorExists) plan.BackupKeys.Add(p.Mirror);
        var container = RegPath.Parent(p.Mirror);

        switch (action)
        {
            case ActionKind.Disable:
                if (!activeExists) return;
                if (mirrorExists)
                {
                    // 软件更新后重新注册了启用位置，旧的禁用副本已经过时：先收进回收位置再移动
                    Recycle(plan, p.Mirror);
                }
                else if (hasContainer && !reg.KeyExists(container))
                {
                    plan.Add(new CreateKeyStep { Key = container }, new DeleteEmptyKeyStep { Key = container });
                }
                plan.Add(new MoveKeyStep { From = p.Active, To = p.Mirror }, new MoveKeyStep { From = p.Mirror, To = p.Active });
                break;

            case ActionKind.Enable:
                if (!mirrorExists) return;
                if (activeExists)
                    Recycle(plan, p.Mirror);
                else
                    plan.Add(new MoveKeyStep { From = p.Mirror, To = p.Active }, new MoveKeyStep { From = p.Active, To = p.Mirror });
                if (hasContainer)
                    plan.Add(new DeleteEmptyKeyStep { Key = container }, new CreateKeyStep { Key = container });
                break;

            case ActionKind.Delete:
                if (activeExists) Recycle(plan, p.Active);
                if (mirrorExists)
                {
                    Recycle(plan, p.Mirror);
                    if (hasContainer)
                        plan.Add(new DeleteEmptyKeyStep { Key = container }, new CreateKeyStep { Key = container });
                }
                break;
        }
    }

    private static void PlanPackaged(IRegistryEnv reg, MenuItem item, ActionKind action, Plan plan)
    {
        var clsid = item.Clsid ?? throw new InvalidOperationException("缺少 CLSID。");
        var inUser = BlockedList.Find(reg, RegPath.HkcuBlocked, clsid, out var userName);
        var inMachine = BlockedList.Find(reg, RegPath.HklmBlocked, clsid, out var machineName);
        if (reg.KeyExists(RegPath.HkcuBlocked)) plan.BackupKeys.Add(RegPath.HkcuBlocked);

        switch (action)
        {
            case ActionKind.Disable:
                if (inUser || inMachine) return;
                if (!reg.KeyExists(RegPath.HkcuBlocked))
                    plan.Add(new CreateKeyStep { Key = RegPath.HkcuBlocked }, new DeleteEmptyKeyStep { Key = RegPath.HkcuBlocked });
                var value = RegValue.String(clsid, BlockedMarker);
                plan.Add(new SetValueStep { Key = RegPath.HkcuBlocked, Value = value },
                         new DeleteValueStep { Key = RegPath.HkcuBlocked, Name = clsid, Expected = value });
                break;

            case ActionKind.Enable:
                if (inUser)
                {
                    var original = reg.ReadValue(RegPath.HkcuBlocked, userName!)!;
                    plan.Add(new DeleteValueStep { Key = RegPath.HkcuBlocked, Name = userName!, Expected = original },
                             new SetValueStep { Key = RegPath.HkcuBlocked, Value = original });
                }
                if (inMachine)
                {
                    plan.BackupKeys.Add(RegPath.HklmBlocked);
                    var original = reg.ReadValue(RegPath.HklmBlocked, machineName!)!;
                    plan.Add(new DeleteValueStep { Key = RegPath.HklmBlocked, Name = machineName!, Expected = original },
                             new SetValueStep { Key = RegPath.HklmBlocked, Value = original });
                }
                break;
        }
    }

    private static void PlanSendTo(MenuItem item, ActionKind action, Plan plan)
    {
        var path = item.RegistryPath;
        if (!File.Exists(path))
        {
            plan.Error = "文件已经不存在，请刷新列表。";
            return;
        }
        var hidden = (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        if (action == ActionKind.Disable && !hidden)
            plan.Add(new SetFileHiddenStep { Path = path, Hidden = true }, new SetFileHiddenStep { Path = path, Hidden = false });
        else if (action == ActionKind.Enable && hidden)
            plan.Add(new SetFileHiddenStep { Path = path, Hidden = false }, new SetFileHiddenStep { Path = path, Hidden = true });
    }
}

/// <summary>Shell Extensions\Blocked 列表的读取。</summary>
public static class BlockedList
{
    /// <summary>CLSID 是否在某个 Blocked 键里；valueName 返回键里实际使用的值名（大小写、花括号可能不同）。</summary>
    public static bool Find(IRegistryEnv reg, string blockedKey, string clsid, out string? valueName)
    {
        valueName = null;
        try
        {
            using var k = reg.Open(blockedKey);
            if (k is null) return false;
            foreach (var n in k.GetValueNames())
            {
                if (TextUtil.NormalizeGuid(n) is { } g && string.Equals(g, clsid, StringComparison.OrdinalIgnoreCase))
                {
                    valueName = n;
                    return true;
                }
            }
        }
        catch
        {
            // 读不到当作未阻止
        }
        return false;
    }
}
