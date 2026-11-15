using System.Security;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContextMenuManager.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Compact = Make(false);
    public static readonly JsonSerializerOptions Pretty = Make(true);

    private static JsonSerializerOptions Make(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = indented,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>前置条件不满足（当前状态和计划时不一致）。这类失败不代表出错，通常是别的程序改过同一处。</summary>
public sealed class StepConflictException(string message) : Exception(message);

/// <summary>
/// 一个最小的、可逆的改动。每个高层操作（禁用、启用、删除）都被编译成若干 Step，
/// 并在计划阶段算好对应的逆操作，写进历史记录，回滚时照着执行。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(SetValueStep), "setValue")]
[JsonDerivedType(typeof(DeleteValueStep), "deleteValue")]
[JsonDerivedType(typeof(MoveKeyStep), "moveKey")]
[JsonDerivedType(typeof(DeleteKeyStep), "deleteKey")]
[JsonDerivedType(typeof(RestoreKeyStep), "restoreKey")]
[JsonDerivedType(typeof(CreateKeyStep), "createKey")]
[JsonDerivedType(typeof(DeleteEmptyKeyStep), "deleteEmptyKey")]
[JsonDerivedType(typeof(SetFileHiddenStep), "setFileHidden")]
public abstract class Step
{
    public abstract void Run(IRegistryEnv env);
    /// <summary>这一步涉及的注册表路径（用于判断是否要提权、以及提权助手的范围校验）。</summary>
    public abstract IEnumerable<string> RegistryPaths();
    public abstract string Describe();
    public bool RequiresAdmin(IRegistryEnv env) => RegistryPaths().Any(env.RequiresAdmin);

    /// <summary>
    /// 这一步想达到的状态是不是已经成立了。成立就跳过，不算冲突。
    /// 用在撤销上：上次应用如果只做了一半，没发生过的那几步的逆操作就不必（也不能）再执行。
    /// </summary>
    public virtual bool AlreadySatisfied(IRegistryEnv env) => false;
}

public sealed class SetValueStep : Step
{
    public string Key { get; set; } = "";
    public RegValue Value { get; set; } = new();

    public override void Run(IRegistryEnv env)
    {
        using var k = env.Open(Key, true) ?? throw new StepConflictException($"注册表项不存在：{Key}");
        Value.Write(k);
    }

    public override IEnumerable<string> RegistryPaths() => [Key];
    public override string Describe() => $"写入值 {Key} [{Value.Name}]";
}

public sealed class DeleteValueStep : Step
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>删除前期望看到的值；不一致说明被别人改过，不删。</summary>
    public RegValue? Expected { get; set; }

    public override void Run(IRegistryEnv env)
    {
        using var k = env.Open(Key, true);
        if (k is null) return;
        var current = RegValue.Read(k, Name);
        if (current is null) return;
        if (Expected is not null && !Expected.SameData(current))
            throw new StepConflictException($"值已被其他程序改动，未删除：{Key} [{Name}]");
        k.DeleteValue(Name, false);
    }

    public override IEnumerable<string> RegistryPaths() => [Key];
    public override string Describe() => $"删除值 {Key} [{Name}]";
}

public sealed class MoveKeyStep : Step
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";

    public override void Run(IRegistryEnv env)
    {
        RegSnapshot snap;
        using (var src = env.Open(From))
        {
            if (src is null) throw new StepConflictException($"要移动的注册表项不存在：{From}");
            snap = RegSnapshot.Capture(src);
        }
        if (env.KeyExists(To)) throw new StepConflictException($"目标位置已存在：{To}");
        try
        {
            using (var dst = env.Create(To)) snap.WriteTo(dst);
            var written = env.Snapshot(To);
            if (written is null || !written.SameAs(snap)) throw new IOException($"写入后校验不一致：{To}");
        }
        catch
        {
            try { env.DeleteTree(To); } catch { /* 清理失败不掩盖原始错误 */ }
            throw;
        }
        try
        {
            env.DeleteTree(From);
            if (env.KeyExists(From)) throw new UnauthorizedAccessException($"原位置未能删除：{From}");
        }
        catch
        {
            // 删除可能只做了一半（比如某个子项没有删除权限）：把原位置补回完整内容，再撤掉副本
            try { using var back = env.Create(From); snap.WriteTo(back); } catch { /* 尽力而为 */ }
            try { env.DeleteTree(To); } catch { /* 尽力而为 */ }
            throw;
        }
    }

    public override bool AlreadySatisfied(IRegistryEnv env) => !env.KeyExists(From) && env.KeyExists(To);

    public override IEnumerable<string> RegistryPaths() => [From, To];
    public override string Describe() => $"移动 {From} → {To}";
}

public sealed class DeleteKeyStep : Step
{
    public string Key { get; set; } = "";

    public override void Run(IRegistryEnv env)
    {
        if (!env.KeyExists(Key)) return;
        env.DeleteTree(Key);
        if (env.KeyExists(Key)) throw new UnauthorizedAccessException($"注册表项未能删除：{Key}");
    }

    public override IEnumerable<string> RegistryPaths() => [Key];
    public override string Describe() => $"删除项 {Key}";
}

public sealed class RestoreKeyStep : Step
{
    public string Key { get; set; } = "";
    public RegSnapshot Snapshot { get; set; } = new();

    public override void Run(IRegistryEnv env)
    {
        if (env.KeyExists(Key)) throw new StepConflictException($"要恢复的位置已存在（可能已被软件重新注册）：{Key}");
        try
        {
            using var k = env.Create(Key);
            Snapshot.WriteTo(k);
        }
        catch
        {
            try { env.DeleteTree(Key); } catch { /* 同上 */ }
            throw;
        }
    }

    public override bool AlreadySatisfied(IRegistryEnv env)
    {
        var current = env.Snapshot(Key);
        return current is not null && current.SameAs(Snapshot);
    }

    /// <summary>删除只做了一半时用：不管项还在不在，都把快照内容写回去。</summary>
    public void Repair(IRegistryEnv env)
    {
        using var k = env.Create(Key);
        Snapshot.WriteTo(k);
    }

    public override IEnumerable<string> RegistryPaths() => [Key];
    public override string Describe() => $"恢复项 {Key}";
}

public sealed class CreateKeyStep : Step
{
    public string Key { get; set; } = "";
    public override void Run(IRegistryEnv env) { using var k = env.Create(Key); }
    public override IEnumerable<string> RegistryPaths() => [Key];
    public override string Describe() => $"创建项 {Key}";
}

/// <summary>只在项为空时删除它；用来清理本工具创建的容器项。</summary>
public sealed class DeleteEmptyKeyStep : Step
{
    public string Key { get; set; } = "";

    public override void Run(IRegistryEnv env)
    {
        if (env.IsEmptyKey(Key)) env.DeleteTree(Key);
    }

    public override IEnumerable<string> RegistryPaths() => [Key];
    public override string Describe() => $"清理空项 {Key}";
}

public sealed class SetFileHiddenStep : Step
{
    public string Path { get; set; } = "";
    public bool Hidden { get; set; }

    public override void Run(IRegistryEnv env)
    {
        if (!File.Exists(Path)) throw new StepConflictException($"文件不存在：{Path}");
        var attr = File.GetAttributes(Path);
        var next = Hidden ? attr | FileAttributes.Hidden : attr & ~FileAttributes.Hidden;
        File.SetAttributes(Path, next == 0 ? FileAttributes.Normal : next);
    }

    public override IEnumerable<string> RegistryPaths() => [];
    public override string Describe() => (Hidden ? "隐藏文件 " : "取消隐藏 ") + Path;
}

/// <summary>一个改动和它的逆操作。</summary>
public sealed class PlannedOp
{
    public Step Do { get; set; } = null!;
    public Step Undo { get; set; } = null!;

    public PlannedOp() { }
    public PlannedOp(Step @do, Step undo) { Do = @do; Undo = undo; }
}

/// <summary>要么全做、要么全回滚的一组改动。</summary>
public sealed class OpGroup
{
    public string Key { get; set; } = "";
    public List<PlannedOp> Ops { get; set; } = [];
}

public sealed class GroupResult
{
    public string Key { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>失败后已做的步骤是否全部回滚成功。</summary>
    public bool RolledBack { get; set; }
}

public static class GroupRunner
{
    /// <param name="policy">可选的检查：返回非空表示拒绝执行这一步，返回值是原因。</param>
    public static GroupResult Run(IRegistryEnv env, OpGroup group, Func<Step, string?>? policy = null)
    {
        var done = new List<PlannedOp>();
        foreach (var op in group.Ops)
        {
            try
            {
                if (policy?.Invoke(op.Do) is { } denied) throw new StepConflictException(denied);
                if (op.Do.AlreadySatisfied(env)) continue;
                op.Do.Run(env);
                done.Add(op);
            }
            catch (Exception ex)
            {
                var rolledBack = true;
                if (op.Do is DeleteKeyStep && op.Undo is RestoreKeyStep restore)
                {
                    // 删除失败时项可能已经缺了一部分子项：只有内容确实变了才按快照补全
                    try
                    {
                        var current = env.Snapshot(restore.Key);
                        if (current is null || !current.SameAs(restore.Snapshot)) restore.Repair(env);
                    }
                    catch { rolledBack = false; }
                }
                for (var i = done.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (policy?.Invoke(done[i].Undo) is not null) { rolledBack = false; continue; }
                        done[i].Undo.Run(env);
                    }
                    catch { rolledBack = false; }
                }
                return new GroupResult { Key = group.Key, Ok = false, Error = Explain(ex, op.Do), RolledBack = rolledBack };
            }
        }
        return new GroupResult { Key = group.Key, Ok = true, RolledBack = false };
    }

    public static string Explain(Exception ex, Step? step = null) => ex switch
    {
        StepConflictException => ex.Message,
        UnauthorizedAccessException or SecurityException =>
            "没有权限修改" + (step is null ? "" : $"（{step.Describe()}）") + "：需要管理员权限，或者该项受系统保护。",
        _ => (step is null ? "" : step.Describe() + " 失败：") + ex.Message,
    };
}
