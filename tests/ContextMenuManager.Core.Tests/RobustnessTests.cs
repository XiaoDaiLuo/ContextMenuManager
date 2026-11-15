using System.Security.Cryptography;
using System.Text.Json;
using ContextMenuManager.Core;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuManager.Core.Tests;

/// <summary>包一层沙箱环境，让指定路径的删除“只做一半就失败”，模拟没有删除权限的子项。</summary>
internal sealed class HalfDeletingEnv(IRegistryEnv inner, string failingPath) : IRegistryEnv
{
    public bool IsSandbox => inner.IsSandbox;
    public RegistryKey? Open(string path, bool writable = false) => inner.Open(path, writable);
    public RegistryKey Create(string path) => inner.Create(path);
    public string PhysicalPath(string path) => inner.PhysicalPath(path);
    public bool RequiresAdmin(string path) => inner.RequiresAdmin(path);

    public void DeleteTree(string path)
    {
        if (!string.Equals(path, failingPath, StringComparison.OrdinalIgnoreCase))
        {
            inner.DeleteTree(path);
            return;
        }
        // 删掉第一个子项和一个值，然后报没有权限
        using (var k = inner.Open(path, true)!)
        {
            var sub = k.GetSubKeyNames().FirstOrDefault();
            if (sub is not null) k.DeleteSubKeyTree(sub);
            var value = k.GetValueNames().FirstOrDefault();
            if (value is not null) k.DeleteValue(value);
        }
        throw new UnauthorizedAccessException("模拟：没有删除权限");
    }
}

public class RobustnessTests : SandboxTest
{
    private const string Key = $@"{Lm}\*\shell\ToDeskSendFile";

    [Fact]
    public void Move_restores_source_and_removes_copy_when_delete_fails_halfway()
    {
        var before = Reg.Snapshot(Key)!;
        var env = new HalfDeletingEnv(Reg, Key);
        var step = new MoveKeyStep { From = Key, To = $@"{Lm}\*\shell\-Moved" };

        Assert.Throws<UnauthorizedAccessException>(() => step.Run(env));

        Assert.True(before.SameAs(Reg.Snapshot(Key)!));
        Assert.False(Reg.KeyExists($@"{Lm}\*\shell\-Moved"));
    }

    [Fact]
    public void Failed_delete_is_repaired_from_snapshot()
    {
        var before = Reg.Snapshot(Key)!;
        var env = new HalfDeletingEnv(Reg, Key);
        var group = new OpGroup
        {
            Key = "g",
            Ops = [new PlannedOp(new DeleteKeyStep { Key = Key }, new RestoreKeyStep { Key = Key, Snapshot = before })],
        };

        var result = GroupRunner.Run(env, group);

        Assert.False(result.Ok);
        Assert.True(result.RolledBack);
        Assert.Contains("没有权限", result.Error);
        Assert.True(before.SameAs(Reg.Snapshot(Key)!));
    }

    [Fact]
    public void Interrupted_batch_can_still_be_undone()
    {
        // 模拟：步骤已经执行，但程序在把结果写回历史之前就退出了，状态停在 pending
        var r = Apply($"verb|{Key}", ActionKind.Disable);
        var batch = Engine.History.Load(r.BatchId!)!;
        batch.Items[0].Status = "pending";
        Engine.History.Save(batch);

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.True(Item($"verb|{Key}").Enabled);
        Assert.Equal("undone", Engine.History.Load(r.BatchId!)!.Items[0].Status);
    }

    [Fact]
    public void Interrupted_batch_whose_steps_never_ran_is_closed_without_changing_anything()
    {
        const string active = $@"{Lm}\Directory\shellex\ContextMenuHandlers\YunShellExt";
        var r = Apply("shellex|" + active, ActionKind.Disable);
        // 把注册表手动还原成“步骤没执行过”的样子，再把状态标成 pending
        AssertOk(Engine.Undo(r.BatchId!));
        var before = Reg.Snapshot(active)!;
        var batch = Engine.History.Load(r.BatchId!)!;
        batch.Items[0].Status = "pending";
        Engine.History.Save(batch);

        var u = Engine.Undo(r.BatchId!);
        Assert.True(u.Items[0].Ok, u.Items[0].Error);
        Assert.True(before.SameAs(Reg.Snapshot(active)!));
        Assert.False(Reg.KeyExists($@"{Lm}\Directory\shellex\-ContextMenuHandlers\YunShellExt"));
        Assert.Equal("undone", Engine.History.Load(r.BatchId!)!.Items[0].Status);
    }

    [Theory]
    [InlineData(@"..\..\Microsoft")]
    [InlineData(@".jpg\shell")]
    [InlineData("a/b")]
    [InlineData(".")]
    [InlineData("")]
    public void ScanFileType_rejects_anything_that_is_not_a_plain_extension(string ext)
    {
        Assert.Empty(Engine.ScanFileType(ext));
    }
}

public class ElevatedHelperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cmm-tests", "helper-" + Guid.NewGuid().ToString("N")[..8]);
    private const string ProbeKey = @"HKCU\Software\Classes\__CmmHelperTest\shell\probe";

    public ElevatedHelperTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\__CmmHelperTest", false);
        GC.SuppressFinalize(this);
    }

    private (string input, string output, string hash) WriteRequest(string inName, string outName)
    {
        var groups = new List<OpGroup>
        {
            new() { Key = "0", Ops = [new PlannedOp(new CreateKeyStep { Key = ProbeKey }, new DeleteEmptyKeyStep { Key = ProbeKey })] },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(groups, Json.Compact);
        var input = Path.Combine(_dir, inName);
        File.WriteAllBytes(input, bytes);
        return (input, Path.Combine(_dir, outName), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static bool ProbeKeyExists()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\__CmmHelperTest\shell\probe");
        return k is not null;
    }

    [Fact]
    public void Ordinary_command_lines_are_not_treated_as_helper_requests()
    {
        Assert.False(ElevatedHelper.TryHandle([]));
        Assert.False(ElevatedHelper.TryHandle(["scan", "--json"]));
    }

    [Fact]
    public void Request_with_unexpected_file_names_is_ignored_and_writes_nothing()
    {
        var (input, _, hash) = WriteRequest("elevated-0123456789abcdef0123456789abcdef.in.json", "x");
        var output = Path.Combine(_dir, "result.txt");

        Assert.True(ElevatedHelper.TryHandle([ElevatedHelper.Switch, input, output, hash]));

        Assert.False(File.Exists(output));
        Assert.False(ProbeKeyExists());
    }

    [Fact]
    public void Request_not_started_by_this_program_is_refused()
    {
        // 测试进程的父进程不是它自己，所以即使文件名和哈希都对，也必须拒绝
        var (input, output, hash) = WriteRequest(
            "elevated-0123456789abcdef0123456789abcdef.in.json",
            "elevated-0123456789abcdef0123456789abcdef.out.json");

        Assert.True(ElevatedHelper.TryHandle([ElevatedHelper.Switch, input, output, hash]));

        var results = JsonSerializer.Deserialize<List<GroupResult>>(File.ReadAllText(output), Json.Compact)!;
        var only = Assert.Single(results);
        Assert.False(only.Ok);
        Assert.Contains("本程序自己发起", only.Error);
        Assert.False(ProbeKeyExists());
    }
}
