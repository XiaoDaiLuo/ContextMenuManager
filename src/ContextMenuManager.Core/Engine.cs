using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContextMenuManager.Core;

// ---------- 历史记录 ----------

public sealed class BatchItem
{
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? AppName { get; set; }
    public string LocationLabel { get; set; } = "";
    public ItemKind Kind { get; set; }
    public ActionKind Action { get; set; }
    /// <summary>pending / applied / partial / failed / undone / superseded</summary>
    public string Status { get; set; } = "applied";
    public string? Error { get; set; }
    public List<PlannedOp> Ops { get; set; } = [];
    /// <summary>改动前导出的 .reg 文件名（在批次目录里）。</summary>
    public List<string> BackupFiles { get; set; } = [];
    public DateTimeOffset? UndoneAt { get; set; }
    /// <summary>状态为 superseded 时：是被哪个批次的改动取代的。那个批次被撤销后，这条记录重新生效。</summary>
    public string? SupersededBy { get; set; }
}

public sealed class Batch
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Title { get; set; } = "";
    public List<BatchItem> Items { get; set; } = [];
}

/// <summary>给界面用的精简版历史（不带步骤和快照）。</summary>
public sealed class BatchSummary
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Title { get; set; } = "";
    public string Directory { get; set; } = "";
    public List<BatchItemSummary> Items { get; set; } = [];
}

public sealed class BatchItemSummary
{
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? AppName { get; set; }
    public string LocationLabel { get; set; } = "";
    public ItemKind Kind { get; set; }
    public ActionKind Action { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public bool HasBackup { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
}

public sealed class HistoryStore(string dataDir)
{
    public string Root { get; } = Path.Combine(dataDir, "history");

    public string DirOf(string batchId)
    {
        if (batchId.Length == 0 || batchId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || batchId.Contains(".."))
            throw new ArgumentException("批次 ID 不合法。");
        return Path.Combine(Root, batchId);
    }

    public void Save(Batch batch)
    {
        var dir = DirOf(batch.Id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "batch.json");
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(batch, Json.Pretty));
        File.Move(tmp, file, true);
    }

    public Batch? Load(string batchId)
    {
        var file = Path.Combine(DirOf(batchId), "batch.json");
        if (!File.Exists(file)) return null;
        return JsonSerializer.Deserialize<Batch>(File.ReadAllText(file), Json.Pretty);
    }

    public List<BatchSummary> List()
    {
        var result = new List<BatchSummary>();
        if (!Directory.Exists(Root)) return result;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            Batch? b;
            try { b = Load(Path.GetFileName(dir)); }
            catch { continue; }
            if (b is null) continue;
            result.Add(new BatchSummary
            {
                Id = b.Id,
                CreatedAt = b.CreatedAt,
                Title = b.Title,
                Directory = dir,
                Items = [.. b.Items.Select(i => new BatchItemSummary
                {
                    ItemId = i.ItemId, Title = i.Title, AppName = i.AppName, LocationLabel = i.LocationLabel,
                    Kind = i.Kind, Action = i.Action, Status = i.Status, Error = i.Error,
                    HasBackup = i.BackupFiles.Count > 0, UndoneAt = i.UndoneAt,
                })],
            });
        }
        return [.. result.OrderByDescending(b => b.CreatedAt)];
    }
}

// ---------- 提权 ----------

public interface IElevator
{
    /// <summary>在提权进程里执行这些改动组。用户拒绝提权时抛 <see cref="OperationCanceledException"/>。</summary>
    List<GroupResult> Run(List<OpGroup> groups, string workDir);
}

/// <summary>用 runas 重新启动本程序（带 --elevated-exec），由提权后的进程执行 HKLM 的改动。</summary>
public sealed class ProcessElevator : IElevator
{
    public List<GroupResult> Run(List<OpGroup> groups, string workDir)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("找不到当前程序路径。");
        Directory.CreateDirectory(workDir);
        var token = Guid.NewGuid().ToString("N");
        var input = Path.Combine(workDir, $"elevated-{token}.in.json");
        var output = Path.Combine(workDir, $"elevated-{token}.out.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(groups, Json.Compact);
        File.WriteAllBytes(input, bytes);
        // 把内容的哈希放在命令行里：提权进程只执行哈希对得上的内容，文件在中途被别人换掉也没用
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"{ElevatedHelper.Switch} \"{input}\" \"{output}\" {hash}",
            };
            Process? p;
            try { p = Process.Start(psi); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("已取消管理员授权。");
            }
            if (p is null) throw new InvalidOperationException("无法启动提权进程。");
            using (p)
            {
                if (!p.WaitForExit(120_000))
                {
                    try { p.Kill(); } catch { /* 没有权限结束提权进程时，靠下面删除输入文件让它无事可做 */ }
                    throw new TimeoutException("提权进程超时。");
                }
            }
            if (!File.Exists(output)) throw new InvalidOperationException("提权进程没有返回结果。");
            return JsonSerializer.Deserialize<List<GroupResult>>(File.ReadAllText(output), Json.Compact) ?? [];
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 临时文件，删不掉无妨 */ }
    }
}

/// <summary>
/// 提权后的那一端：读取改动组、校验、执行、写回结果。
/// 这个入口以管理员权限运行，所以对输入很挑剔：
///   1. 必须是本程序自己启动的（父进程是同一个 exe）；
///   2. 输入内容的 SHA-256 必须和命令行里给的一致；
///   3. 输入、输出文件名必须是约定的格式，并且在同一个目录里；
///   4. 每一步都要通过 <see cref="StepPolicy"/> 的检查（只接受形态精确、不带外来内容的右键菜单改动）。
/// </summary>
public static partial class ElevatedHelper
{
    public const string Switch = "--elevated-exec";

    [GeneratedRegex(@"^elevated-[0-9a-f]{32}\.(in|out)\.json$")]
    private static partial Regex FileNamePattern();

    /// <summary>命令行是提权执行请求时处理并返回 true。</summary>
    public static bool TryHandle(string[] args)
    {
        if (args.Length < 1 || args[0] != Switch) return false;
        if (args.Length != 4) return true;
        string input = args[1], output = args[2], hash = args[3];

        // 连输出位置都不符合约定时，直接什么都不做，也不写任何文件
        if (!FileNamePattern().IsMatch(Path.GetFileName(input)) || !FileNamePattern().IsMatch(Path.GetFileName(output))
            || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(input)), Path.GetDirectoryName(Path.GetFullPath(output)), StringComparison.OrdinalIgnoreCase))
            return true;

        var results = new List<GroupResult>();
        try
        {
            if (!ParentIsSameProgram()) throw new InvalidOperationException("只接受由本程序自己发起的请求。");
            var bytes = File.ReadAllBytes(input);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请求内容校验不一致。");

            var groups = JsonSerializer.Deserialize<List<OpGroup>>(bytes, Json.Compact) ?? [];
            var env = new RealRegistryEnv();
            // 每一步在执行前都要过 StepPolicy：只接受形态精确、不带外来内容的右键菜单改动
            foreach (var g in groups) results.Add(GroupRunner.Run(env, g, step => StepPolicy.Check(env, step)));
        }
        catch (Exception ex)
        {
            results.Add(new GroupResult { Key = "*", Ok = false, RolledBack = true, Error = "提权进程出错：" + ex.Message });
        }
        try
        {
            // 只写自己新建的文件：如果这个名字已经被别人占了（包括被换成了链接），宁可不返回结果
            using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(JsonSerializer.SerializeToUtf8Bytes(results, Json.Compact));
        }
        catch
        {
            // 调用方会报“没有返回结果”
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    private static bool ParentIsSameProgram()
    {
        try
        {
            var info = new ProcessBasicInformation();
            using var self = Process.GetCurrentProcess();
            if (NtQueryInformationProcess(self.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return false;
            using var parent = Process.GetProcessById((int)info.InheritedFromUniqueProcessId);
            // 父进程必须比自己早启动（排除进程号被复用的情况），并且是同一个可执行文件
            if (parent.StartTime > self.StartTime) return false;
            return string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

// ---------- 引擎 ----------

public sealed class Engine(AppEnv env)
{
    private readonly object _gate = new();
    public AppEnv Env { get; } = env;
    public HistoryStore History { get; } = new(env.DataDir);

    public List<MenuItem> Scan() => new Scanner(Env).ScanAll();

    public List<MenuItem> ScanFileType(string ext) => new Scanner(Env).ScanFileType(ext);

    public ApplyResult Apply(IReadOnlyList<ChangeRequest> changes, string? title = null)
    {
        if (Env.ReadOnly) throw new InvalidOperationException("当前是只读模式，不能应用改动。");
        lock (_gate)
        {
            var scanner = new Scanner(Env);
            var batch = new Batch { Id = NewBatchId(), CreatedAt = DateTimeOffset.Now };
            var result = new ApplyResult();
            var work = new List<(BatchItem item, ItemResult res, Plan plan)>();

            foreach (var change in changes.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
            {
                var res = new ItemResult { Id = change.Id, Action = change.Action, Title = change.Id };
                result.Items.Add(res);
                var item = scanner.Locate(change.Id);
                if (item is null)
                {
                    res.Error = "没有找到这一项，可能已经被卸载或改动，请刷新列表。";
                    continue;
                }
                res.Title = item.Title;
                var plan = Planner.Build(Env, item, change.Action);
                if (plan.Error is not null) { res.Error = plan.Error; continue; }
                if (plan.NoOp) { res.Ok = true; res.NoOp = true; continue; }
                var bi = new BatchItem
                {
                    ItemId = item.Id, Title = item.Title, AppName = item.AppName, LocationLabel = item.LocationLabel,
                    Kind = item.Kind, Action = change.Action, Ops = plan.Ops, Status = "pending",
                };
                batch.Items.Add(bi);
                work.Add((bi, res, plan));
            }

            if (work.Count == 0) return result;

            batch.Title = title ?? DescribeBatch(batch);
            result.BatchId = batch.Id;
            var dir = History.DirOf(batch.Id);
            Directory.CreateDirectory(dir);

            // 1. 先备份：导出受影响的键，并把完整计划落盘。这样即使执行中途崩溃，也有恢复依据。
            var n = 0;
            foreach (var (bi, res, plan) in work)
            {
                var keys = plan.BackupKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var key in keys)
                {
                    var file = $"{++n:D3}-{SafeName(bi.Title)}.reg";
                    if (Env.RegExporter(Env.Reg.PhysicalPath(key), Path.Combine(dir, file))) bi.BackupFiles.Add(file);
                }
                // 删除必须有完整的 .reg 备份才动手；禁用和启用本身可逆，备份只是多一层保险
                if (bi.Action == ActionKind.Delete && bi.BackupFiles.Count < keys.Count)
                {
                    bi.Status = "failed";
                    bi.Error = res.Error = "备份没有成功，为安全起见没有删除。可以先试试“禁用”。";
                }
            }
            History.Save(batch);
            work.RemoveAll(w => w.item.Status == "failed");

            // 2. 执行
            var outcomes = Execute(work.Select((w, i) => (key: i.ToString(), ops: w.plan.Ops)).ToList());
            for (var i = 0; i < work.Count; i++)
            {
                var (bi, res, _) = work[i];
                var (ok, error, uncertain) = outcomes[i.ToString()];
                res.Ok = ok;
                res.Error = error;
                // partial：这一项只有一部分步骤生效，或者失败后没能完全回滚。保留成可以从历史里恢复的状态。
                bi.Status = ok ? "applied" : uncertain ? "partial" : "failed";
                bi.Error = error;
                if (ok && bi.Kind is ItemKind.ShellEx or ItemKind.DragDrop) result.RestartSuggested = true;
            }
            History.Save(batch);
            MarkSuperseded(batch);
            Native.NotifyAssocChanged();
            return result;
        }
    }

    /// <summary>撤销一个批次里已应用的改动；<paramref name="itemIds"/> 为空表示整批。</summary>
    public ApplyResult Undo(string batchId, IReadOnlyCollection<string>? itemIds = null)
    {
        if (Env.ReadOnly) throw new InvalidOperationException("当前是只读模式，不能撤销。");
        lock (_gate)
        {
            var batch = History.Load(batchId) ?? throw new InvalidOperationException("找不到这条历史记录。");
            var result = new ApplyResult { BatchId = batchId };
            var targets = batch.Items
                .Where(i => i.Status is "applied" or "pending" or "partial")
                .Where(i => itemIds is null || itemIds.Count == 0 || itemIds.Contains(i.ItemId, StringComparer.OrdinalIgnoreCase))
                .Reverse()
                .ToList();
            if (targets.Count == 0) return result;

            var plans = targets.Select((t, i) => (key: i.ToString(),
                ops: Enumerable.Reverse(t.Ops).Select(o => new PlannedOp(o.Undo, o.Do)).ToList())).ToList();
            var outcomes = Execute(plans);
            for (var i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                var (ok, error, _) = outcomes[i.ToString()];
                result.Items.Add(new ItemResult { Id = t.ItemId, Title = t.Title, Action = t.Action, Undo = true, Ok = ok, Error = error });
                if (ok)
                {
                    t.Status = "undone";
                    t.UndoneAt = DateTimeOffset.Now;
                    t.Error = null;
                    if (t.Kind is ItemKind.ShellEx or ItemKind.DragDrop) result.RestartSuggested = true;
                }
                else
                {
                    t.Error = "撤销失败：" + error;
                }
            }
            History.Save(batch);
            ReviveSuperseded(batchId, [.. result.Items.Where(i => i.Ok).Select(i => i.Id)]);
            Native.NotifyAssocChanged();
            return result;
        }
    }

    /// <summary>
    /// 某个批次的改动被撤销后，之前被它取代的记录重新成立（例如先禁用、再启用、又撤销了启用：那次禁用又生效了）。
    /// </summary>
    private void ReviveSuperseded(string undoneBatchId, HashSet<string> itemIds)
    {
        try
        {
            if (itemIds.Count == 0) return;
            foreach (var dir in Directory.Exists(History.Root) ? Directory.GetDirectories(History.Root) : [])
            {
                var id = Path.GetFileName(dir);
                if (id == undoneBatchId) continue;
                Batch? old;
                try { old = History.Load(id); } catch { continue; }
                if (old is null) continue;
                var hits = old.Items.Where(i => i.Status == "superseded" && i.SupersededBy == undoneBatchId
                                                && itemIds.Contains(i.ItemId, StringComparer.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0) continue;
                foreach (var i in hits) { i.Status = "applied"; i.SupersededBy = null; }
                History.Save(old);
            }
        }
        catch
        {
            // 历史整理失败不影响已经完成的撤销
        }
    }

    /// <summary>
    /// 执行若干项的改动。每一项按“是否需要管理员权限”拆成两组：不需要的在本进程里做；
    /// 需要的在已提权时也在本进程做，否则一次性交给提权助手。
    /// </summary>
    private Dictionary<string, (bool ok, string? error, bool uncertain)> Execute(List<(string key, List<PlannedOp> ops)> items)
    {
        var userGroups = new List<OpGroup>();
        var adminGroups = new List<OpGroup>();
        foreach (var (key, ops) in items)
        {
            var admin = ops.Where(o => o.Do.RequiresAdmin(Env.Reg) || o.Undo.RequiresAdmin(Env.Reg)).ToList();
            var user = ops.Except(admin).ToList();
            if (user.Count > 0) userGroups.Add(new OpGroup { Key = key, Ops = user });
            if (admin.Count > 0) adminGroups.Add(new OpGroup { Key = key, Ops = admin });
        }

        var results = new List<GroupResult>();
        results.AddRange(userGroups.Select(g => GroupRunner.Run(Env.Reg, g)));

        if (adminGroups.Count > 0)
        {
            if (Elevation.IsElevated || Env.Elevator is null)
            {
                // 本进程已经有管理员权限：和提权助手一样，逐步过策略检查
                results.AddRange(adminGroups.Select(g => GroupRunner.Run(Env.Reg, g, step => StepPolicy.Check(Env.Reg, step))));
            }
            else
            {
                try
                {
                    var returned = Env.Elevator.Run(adminGroups, Path.Combine(Env.DataDir, "tmp"));
                    results.AddRange(returned);
                    var fatal = returned.FirstOrDefault(r => r.Key == "*");
                    // 提权进程在动手之前就拒绝或出错时（fatal），这些组一步都没执行；否则不确定执行到了哪里
                    foreach (var g in adminGroups.Where(g => returned.All(r => r.Key != g.Key)))
                        results.Add(new GroupResult
                        {
                            Key = g.Key, Ok = false, RolledBack = fatal?.RolledBack ?? false,
                            Error = fatal?.Error ?? "提权进程没有返回这一项的结果。",
                        });
                }
                catch (Exception ex)
                {
                    // 用户取消授权时什么都没执行；超时、没有返回结果则不确定
                    var nothingRan = ex is OperationCanceledException;
                    results.AddRange(adminGroups.Select(g => new GroupResult { Key = g.Key, Ok = false, Error = ex.Message, RolledBack = nothingRan }));
                }
            }
        }

        var outcome = new Dictionary<string, (bool ok, string? error, bool uncertain)>();
        foreach (var (key, _) in items)
        {
            var mine = results.Where(r => r.Key == key).ToList();
            var failed = mine.Where(r => !r.Ok).ToList();
            if (failed.Count == 0)
            {
                outcome[key] = (true, null, false);
                continue;
            }
            var msg = string.Join("；", failed.Select(f => f.Error).Distinct());
            var notRolledBack = failed.Any(f => !f.RolledBack);
            var halfDone = mine.Any(r => r.Ok);
            if (notRolledBack)
                msg += "（可能有步骤没能回滚，可以在“历史与回滚”里尝试恢复，或用那里的备份文件手动恢复）";
            else if (halfDone)
                msg += "（另一部分改动已经生效，可以在“历史与回滚”里撤销）";
            outcome[key] = (false, msg, notRolledBack || halfDone);
        }
        return outcome;
    }

    /// <summary>
    /// 同一项被这一批成功改动后，更早批次里针对它的“已生效”记录就过期了（比如先禁用、后来又启用）。
    /// 把它们标成 superseded，历史里不再提供对它们的撤销，免得撤销一个早已不成立的状态。
    /// </summary>
    private void MarkSuperseded(Batch current)
    {
        try
        {
            var changed = current.Items.Where(i => i.Status == "applied").Select(i => i.ItemId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (changed.Count == 0) return;
            foreach (var summary in History.List())
            {
                if (summary.Id == current.Id) continue;
                if (!summary.Items.Any(i => i.Status == "applied" && changed.Contains(i.ItemId))) continue;
                var old = History.Load(summary.Id);
                if (old is null) continue;
                foreach (var i in old.Items.Where(i => i.Status == "applied" && changed.Contains(i.ItemId)))
                {
                    i.Status = "superseded";
                    i.SupersededBy = current.Id;
                }
                History.Save(old);
            }
        }
        catch
        {
            // 这只是历史的整理，失败了不影响已经完成的改动
        }
    }

    private static string NewBatchId() =>
        DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];

    private static string DescribeBatch(Batch b)
    {
        var parts = new List<string>();
        foreach (var (action, label) in new[] { (ActionKind.Disable, "禁用"), (ActionKind.Enable, "启用"), (ActionKind.Delete, "删除") })
        {
            var n = b.Items.Count(i => i.Action == action);
            if (n > 0) parts.Add($"{label} {n} 项");
        }
        return string.Join("，", parts);
    }

    private static string SafeName(string title)
    {
        var chars = title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c).ToArray();
        var s = new string(chars);
        return s.Length > 40 ? s[..40] : s;
    }
}

public static class RegExport
{
    /// <summary>用 reg.exe 导出一个键（含子键）。键不存在或导出失败返回 false。</summary>
    public static bool Export(string physicalPath, string file)
    {
        try
        {
            var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
            var psi = new ProcessStartInfo(reg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("export");
            psi.ArgumentList.Add(physicalPath);
            psi.ArgumentList.Add(file);
            psi.ArgumentList.Add("/y");
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch { /* 忽略 */ } return false; }
            return p.ExitCode == 0 && File.Exists(file);
        }
        catch
        {
            return false;
        }
    }
}
