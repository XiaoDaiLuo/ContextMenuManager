using ContextMenuManager.Core;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuManager.Core.Tests;

/// <summary>沙箱环境，但像真实环境一样声称“写 HKLM 需要管理员”，用来测试提权分拆的流程。</summary>
internal sealed class AdminRequiringEnv(IRegistryEnv inner) : IRegistryEnv
{
    public bool IsSandbox => inner.IsSandbox;
    public RegistryKey? Open(string path, bool writable = false) => inner.Open(path, writable);
    public RegistryKey Create(string path) => inner.Create(path);
    public void DeleteTree(string path) => inner.DeleteTree(path);
    public string PhysicalPath(string path) => inner.PhysicalPath(path);
    public bool RequiresAdmin(string path) => RegPath.Hive(path) == "HKLM";
}

internal sealed class FakeElevator(IRegistryEnv env) : IElevator
{
    public bool Cancel { get; set; }
    public int Calls { get; private set; }
    public List<OpGroup> LastGroups { get; private set; } = [];

    public List<GroupResult> Run(List<OpGroup> groups, string workDir)
    {
        Calls++;
        LastGroups = groups;
        if (Cancel) throw new OperationCanceledException("已取消管理员授权。");
        return [.. groups.Select(g => GroupRunner.Run(env, g))];
    }
}

public class ElevationFlowTests : SandboxTest
{
    private readonly FakeElevator _elevator;
    private readonly Engine _engine;

    public ElevationFlowTests()
    {
        _elevator = new FakeElevator(Reg);
        var env = new AppEnv
        {
            Reg = new AdminRequiringEnv(Reg),
            DataDir = Env.DataDir,
            SendToDir = Env.SendToDir,
            Packages = Env.Packages,
            Elevator = _elevator,
        };
        _engine = new Engine(env);
    }

    private static readonly bool Elevated = Elevation.IsElevated;

    [Fact]
    public void Machine_wide_steps_go_to_elevator_and_user_steps_stay_in_process()
    {
        if (Elevated) return; // 已提权的测试进程不会走提权助手
        var r = _engine.Apply(
        [
            new ChangeRequest { Id = $@"verb|{Lm}\*\shell\ToDeskSendFile", Action = ActionKind.Disable },
            new ChangeRequest { Id = $@"verb|{Cu}\Directory\shell\MyTool", Action = ActionKind.Disable },
        ]);
        AssertOk(r);
        Assert.Equal(1, _elevator.Calls);
        var group = Assert.Single(_elevator.LastGroups);
        Assert.All(group.Ops, o => Assert.All(o.Do.RegistryPaths(), p => Assert.StartsWith("HKLM", p)));
        Assert.False(Item($@"verb|{Lm}\*\shell\ToDeskSendFile").Enabled);
        Assert.False(Item($@"verb|{Cu}\Directory\shell\MyTool").Enabled);
    }

    [Fact]
    public void Cancelled_elevation_changes_nothing_and_is_not_reported_as_failed_rollback()
    {
        if (Elevated) return;
        _elevator.Cancel = true;
        var id = $@"shellex|{Lm}\Directory\shellex\ContextMenuHandlers\YunShellExt";

        var r = _engine.Apply([new ChangeRequest { Id = id, Action = ActionKind.Disable }]);

        var item = Assert.Single(r.Items);
        Assert.False(item.Ok);
        Assert.Contains("已取消", item.Error);
        Assert.DoesNotContain("回滚", item.Error);
        Assert.True(Item(id).Enabled);
        Assert.Equal("failed", _engine.History.Load(r.BatchId!)!.Items[0].Status);
    }

    [Fact]
    public void Item_that_only_half_succeeded_is_kept_recoverable()
    {
        if (Elevated) return;
        // 同一个 CLSID 同时被当前用户和所有用户阻止：启用它要改两处，其中所有用户那处需要提权
        var id = $"pkg|Demo.Blocked_abcdefgh12345|{SandboxSeeder.MachineBlockedClsid}";
        using (var k = Reg.Create(RegPath.HkcuBlocked)) k.SetValue(SandboxSeeder.MachineBlockedClsid, "mine");
        _elevator.Cancel = true;

        var r = _engine.Apply([new ChangeRequest { Id = id, Action = ActionKind.Enable }]);

        Assert.False(r.Items[0].Ok);
        Assert.Contains("另一部分改动已经生效", r.Items[0].Error);
        Assert.Null(Reg.ReadValue(RegPath.HkcuBlocked, SandboxSeeder.MachineBlockedClsid));       // 当前用户那一半生效了
        Assert.NotNull(Reg.ReadValue(RegPath.HklmBlocked, SandboxSeeder.MachineBlockedClsid));    // 所有用户那一半没动
        Assert.Equal("partial", _engine.History.Load(r.BatchId!)!.Items[0].Status);

        // 从历史里恢复：把已生效的那一半还原
        _elevator.Cancel = false;
        var u = _engine.Undo(r.BatchId!);
        AssertOk(u);
        Assert.Equal("mine", Reg.ReadString(RegPath.HkcuBlocked, SandboxSeeder.MachineBlockedClsid));
        Assert.Equal("blocked by policy", Reg.ReadString(RegPath.HklmBlocked, SandboxSeeder.MachineBlockedClsid));
        Assert.Equal("undone", _engine.History.Load(r.BatchId!)!.Items[0].Status);
    }
}

public class SupersedeTests : SandboxTest
{
    [Fact]
    public void Later_change_to_same_item_retires_the_earlier_history_entry()
    {
        var id = $@"shellex|{Lm}\Directory\shellex\ContextMenuHandlers\YunShellExt";
        var other = $@"verb|{Lm}\*\shell\ToDeskSendFile";
        var first = Engine.Apply([new ChangeRequest { Id = id, Action = ActionKind.Disable }, new ChangeRequest { Id = other, Action = ActionKind.Disable }]);
        var second = Apply(id, ActionKind.Enable);
        AssertOk(second);

        var statuses = Engine.History.Load(first.BatchId!)!.Items.ToDictionary(i => i.ItemId, i => i.Status);
        Assert.Equal("superseded", statuses[id]);
        Assert.Equal("applied", statuses[other]);

        // 撤销第一批：只会处理还有效的那一项，不会去动已经重新启用的扩展
        var u = Engine.Undo(first.BatchId!);
        var only = Assert.Single(u.Items);
        Assert.Equal(other, only.Id);
        Assert.True(only.Ok);
        Assert.True(Item(id).Enabled);
        Assert.True(Item(other).Enabled);
    }

    [Fact]
    public void Failed_change_does_not_retire_earlier_entries()
    {
        var id = $@"verb|{Lm}\*\shell\ToDeskSendFile";
        var first = Apply(id, ActionKind.Disable);
        // 第二次是无效操作（已经是禁用状态），不产生新批次，也不应影响第一批
        var again = Apply(id, ActionKind.Disable);
        Assert.True(again.Items[0].NoOp);
        Assert.Equal("applied", Engine.History.Load(first.BatchId!)!.Items[0].Status);
    }
}
