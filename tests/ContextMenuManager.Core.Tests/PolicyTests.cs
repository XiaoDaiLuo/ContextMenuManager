using ContextMenuManager.Core;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuManager.Core.Tests;

/// <summary>提权端的步骤策略：只放行形态精确、不带外来内容的右键菜单改动。</summary>
public class PolicyTests : SandboxTest
{
    private const string Verb = $@"{Lm}\*\shell\ToDeskSendFile";
    private const string Active = $@"{Lm}\Directory\shellex\ContextMenuHandlers\YunShellExt";
    private const string Mirror = $@"{Lm}\Directory\shellex\-ContextMenuHandlers\YunShellExt";
    private const string Bin = StepPolicy.HklmRecycle + @"\0123456789abcdef0123456789abcdef";

    private string? Check(Step step) => StepPolicy.Check(Reg, step);

    [Fact]
    public void Allows_the_exact_shapes_the_planner_produces()
    {
        Assert.Null(Check(new SetValueStep { Key = Verb, Value = RegValue.String("ProgrammaticAccessOnly", "") }));
        Assert.Null(Check(new DeleteValueStep { Key = Verb, Name = "LegacyDisable" }));
        Assert.Null(Check(new SetValueStep { Key = RegPath.HklmBlocked, Value = RegValue.String(SandboxSeeder.ClipchampClsid, "x") }));
        Assert.Null(Check(new DeleteValueStep { Key = RegPath.HklmBlocked, Name = SandboxSeeder.ClipchampClsid }));
        Assert.Null(Check(new MoveKeyStep { From = Active, To = Mirror }));
        Assert.Null(Check(new MoveKeyStep { From = Mirror, To = Active }));
        Assert.Null(Check(new MoveKeyStep { From = $@"{Lm}\.docx\Word.Document.12\ShellNew", To = $@"{Lm}\.docx\Word.Document.12\-ShellNew" }));
        Assert.Null(Check(new MoveKeyStep { From = Verb, To = Bin }));
        Assert.Null(Check(new MoveKeyStep { From = Bin, To = Verb }));
        Assert.Null(Check(new MoveKeyStep { From = Mirror, To = Bin }));
        Assert.Null(Check(new CreateKeyStep { Key = $@"{Lm}\*\shellex\-ContextMenuHandlers" }));
        Assert.Null(Check(new DeleteEmptyKeyStep { Key = $@"{Lm}\Drive\shellex\-DragDropHandlers" }));
    }

    [Fact]
    public void Rejects_steps_that_would_bring_content_from_the_history_file()
    {
        // 这正是篡改 batch.json 想做的事：借撤销把任意命令写进所有用户的注册表
        var evil = new RegSnapshot { Values = [RegValue.String("", "evil")] };
        evil.SubKeys["command"] = new RegSnapshot { Values = [RegValue.String("", @"C:\evil.exe ""%1""")] };
        Assert.NotNull(Check(new RestoreKeyStep { Key = $@"{Lm}\exefile\shell\open", Snapshot = evil }));
        Assert.NotNull(Check(new RestoreKeyStep { Key = $@"{Lm}\*\shell\Harmless", Snapshot = evil }));
        Assert.NotNull(Check(new SetValueStep { Key = $@"{Lm}\exefile\shell\open\command", Value = RegValue.String("", "evil.exe") }));
        Assert.NotNull(Check(new SetValueStep { Key = Verb, Value = RegValue.String("", "renamed") }));
        Assert.NotNull(Check(new SetValueStep { Key = Verb + @"\command", Value = RegValue.String("", "evil.exe") }));
        Assert.NotNull(Check(new SetValueStep { Key = RegPath.HklmBlocked, Value = RegValue.String("not-a-guid", "") }));
    }

    [Fact]
    public void Rejects_plain_deletes_and_anything_outside_menu_locations()
    {
        Assert.NotNull(Check(new DeleteKeyStep { Key = $@"{Lm}\Directory\shell" }));
        Assert.NotNull(Check(new DeleteKeyStep { Key = Verb }));
        Assert.NotNull(Check(new DeleteEmptyKeyStep { Key = $@"{Lm}\Directory\shell" }));
        Assert.NotNull(Check(new CreateKeyStep { Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" }));
        Assert.NotNull(Check(new MoveKeyStep { From = Verb, To = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\x" }));
        Assert.NotNull(Check(new MoveKeyStep { From = $@"{Lm}\CLSID\{{11111111-2222-3333-4444-555555555555}}\InprocServer32", To = Bin }));
        Assert.NotNull(Check(new MoveKeyStep { From = Verb, To = StepPolicy.HklmRecycle + @"\..\..\Classes\exefile\shell\open" }));
        Assert.NotNull(Check(new MoveKeyStep { From = Verb, To = StepPolicy.HklmRecycle + @"\not-an-id" }));
        Assert.NotNull(Check(new SetFileHiddenStep { Path = @"C:\Windows\System32\drivers\etc\hosts", Hidden = true }));
        // 当前用户的位置不该交给提权的一端
        Assert.NotNull(Check(new SetValueStep { Key = $@"{Cu}\*\shell\X", Value = RegValue.String("ProgrammaticAccessOnly", "") }));
        Assert.NotNull(Check(new CreateKeyStep { Key = RegPath.HkcuBlocked }));
    }

    [Fact]
    public void Move_must_stay_between_an_item_and_its_own_mirror()
    {
        Assert.NotNull(Check(new MoveKeyStep { From = Active, To = $@"{Lm}\*\shellex\-ContextMenuHandlers\YunShellExt" }));
        Assert.NotNull(Check(new MoveKeyStep { From = Active, To = $@"{Lm}\Directory\shellex\-ContextMenuHandlers\Other" }));
        Assert.NotNull(Check(new MoveKeyStep { From = Active, To = $@"{Lm}\Directory\shellex\-DragDropHandlers\YunShellExt" }));
        Assert.NotNull(Check(new MoveKeyStep { From = Verb, To = $@"{Lm}\exefile\shell\Planted" }));
        Assert.NotNull(Check(new MoveKeyStep { From = $@"{Lm}\.docx\ShellNew", To = $@"{Lm}\.txt\-ShellNew" }));
    }

    [Fact]
    public void Core_items_cannot_be_hidden_or_removed_but_can_be_restored()
    {
        const string open = $@"{Lm}\Folder\shell\open";
        const string openWith = $@"{Lm}\*\shellex\ContextMenuHandlers\Open With";
        const string openWithMirror = $@"{Lm}\*\shellex\-ContextMenuHandlers\Open With";

        Assert.NotNull(Check(new SetValueStep { Key = open, Value = RegValue.String("ProgrammaticAccessOnly", "") }));
        Assert.NotNull(Check(new MoveKeyStep { From = open, To = Bin }));
        Assert.NotNull(Check(new MoveKeyStep { From = Bin, To = $@"{Lm}\exefile\shell\open" }));
        Assert.NotNull(Check(new MoveKeyStep { From = openWith, To = openWithMirror }));
        Assert.NotNull(Check(new MoveKeyStep { From = openWith, To = Bin }));

        // 恢复方向不拦
        Assert.Null(Check(new DeleteValueStep { Key = open, Name = "ProgrammaticAccessOnly" }));
        using (var k = Reg.Create(openWithMirror)) k.SetValue("", "{09799AFB-AD67-11D1-ABCD-00C04FC30936}");
        Reg.DeleteTree(openWith);
        Assert.Null(Check(new MoveKeyStep { From = openWithMirror, To = openWith }));
    }

    [Fact]
    public void Every_plan_for_machine_wide_items_passes_the_policy_in_both_directions()
    {
        Env.AllowSystem = true;
        var checkedOps = 0;
        foreach (var item in Engine.Scan().Concat(Engine.ScanFileType(".jpg")).Where(i => i.Hive == "HKLM" && !i.IsProtected && i.Kind != ItemKind.Packaged))
        {
            foreach (var action in new[] { ActionKind.Disable, ActionKind.Enable, ActionKind.Delete })
            {
                var plan = Planner.Build(Env, item, action);
                Assert.Null(plan.Error);
                foreach (var op in plan.Ops)
                {
                    Assert.True(Check(op.Do) is null, $"{action} {item.Id}: {Check(op.Do)}");
                    Assert.True(Check(op.Undo) is null, $"撤销 {action} {item.Id}: {Check(op.Undo)}");
                    checkedOps++;
                }
            }
        }
        Assert.True(checkedOps > 30);
    }

    [Fact]
    public void Runner_with_policy_refuses_a_tampered_group_and_changes_nothing()
    {
        var before = Reg.Snapshot(Verb)!;
        var evil = new RegSnapshot { Values = [RegValue.String("", "evil")] };
        var group = new OpGroup
        {
            Key = "g",
            Ops =
            [
                new PlannedOp(new SetValueStep { Key = Verb, Value = RegValue.String("ProgrammaticAccessOnly", "") }, new DeleteValueStep { Key = Verb, Name = "ProgrammaticAccessOnly" }),
                new PlannedOp(new RestoreKeyStep { Key = $@"{Lm}\exefile\shell\open2", Snapshot = evil }, new DeleteKeyStep { Key = $@"{Lm}\exefile\shell\open2" }),
            ],
        };

        var result = GroupRunner.Run(Reg, group, s => StepPolicy.Check(Reg, s));

        Assert.False(result.Ok);
        Assert.True(result.RolledBack);
        Assert.Contains("拒绝执行", result.Error);
        Assert.False(Reg.KeyExists($@"{Lm}\exefile\shell\open2"));
        Assert.True(before.SameAs(Reg.Snapshot(Verb)!));
    }
}

public class ReviewFixTests : SandboxTest
{
    private const string Active = $@"{Lm}\*\shellex\ContextMenuHandlers\7-Zip";
    private const string Mirror = $@"{Lm}\*\shellex\-ContextMenuHandlers\7-Zip";
    private const string Container = $@"{Lm}\*\shellex\-ContextMenuHandlers";

    private Batch SavePending(MenuItem item, ActionKind action, Plan plan)
    {
        var batch = new Batch
        {
            Id = "20260101-000000-" + Guid.NewGuid().ToString("N")[..4],
            CreatedAt = DateTimeOffset.Now,
            Title = "中断的批次",
            Items = [new BatchItem { ItemId = item.Id, Title = item.Title, Kind = item.Kind, Action = action, Ops = plan.Ops, Status = "pending" }],
        };
        Engine.History.Save(batch);
        return batch;
    }

    private int RecycleCount()
    {
        using var k = Reg.Open(StepPolicy.HklmRecycle);
        return k?.SubKeyCount ?? 0;
    }

    [Fact]
    public void Delete_parks_the_key_in_the_recycle_location_and_undo_brings_it_back()
    {
        var id = $@"verb|{Lm}\*\shell\ToDeskSendFile";
        var before = Reg.Snapshot($@"{Lm}\*\shell\ToDeskSendFile")!;

        var r = Apply(id, ActionKind.Delete);
        AssertOk(r);
        Assert.Equal(1, RecycleCount());
        // 历史文件里不再带着被删内容的副本：恢复靠的是回收位置里的原件
        var op = Assert.Single(Engine.History.Load(r.BatchId!)!.Items[0].Ops);
        Assert.IsType<MoveKeyStep>(op.Do);
        Assert.IsType<MoveKeyStep>(op.Undo);

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.Equal(0, RecycleCount());
        Assert.True(before.SameAs(Reg.Snapshot($@"{Lm}\*\shell\ToDeskSendFile")!));
    }

    [Fact]
    public void Undo_recovers_a_disable_that_stopped_after_its_first_step()
    {
        var item = Item("shellex|" + Active);
        var plan = Planner.Build(Env, item, ActionKind.Disable);
        Assert.Equal(2, plan.Ops.Count);          // 建容器、移动
        var before = Reg.Snapshot(Active)!;
        var batch = SavePending(item, ActionKind.Disable, plan);
        plan.Ops[0].Do.Run(Reg);                   // 只做了第一步就“崩溃”
        Assert.True(Reg.KeyExists(Container));

        var u = Engine.Undo(batch.Id);

        AssertOk(u);
        Assert.False(Reg.KeyExists(Container));
        Assert.True(before.SameAs(Reg.Snapshot(Active)!));
        Assert.Equal("undone", Engine.History.Load(batch.Id)!.Items[0].Status);
    }

    [Fact]
    public void Undo_recovers_a_delete_that_stopped_halfway()
    {
        // 启用位置和禁用副本同时存在，删除要动两处；只做了第一处
        using (var k = Reg.Create(Mirror)) k.SetValue("", "{OLD}");
        var item = Item("shellex|" + Active);
        var activeBefore = Reg.Snapshot(Active)!;
        var mirrorBefore = Reg.Snapshot(Mirror)!;
        var plan = Planner.Build(Env, item, ActionKind.Delete);
        Assert.Equal(3, plan.Ops.Count);
        var batch = SavePending(item, ActionKind.Delete, plan);
        plan.Ops[0].Do.Run(Reg);
        Assert.False(Reg.KeyExists(Active));

        AssertOk(Engine.Undo(batch.Id));

        Assert.True(activeBefore.SameAs(Reg.Snapshot(Active)!));
        Assert.True(mirrorBefore.SameAs(Reg.Snapshot(Mirror)!));
        Assert.Equal(0, RecycleCount());
    }

    [Fact]
    public void Undo_succeeds_quietly_when_the_item_was_already_put_back_by_hand()
    {
        var r = Apply("shellex|" + Active, ActionKind.Disable);
        new MoveKeyStep { From = Mirror, To = Active }.Run(Reg);   // 用户自己或别的工具已经挪回去了

        var u = Engine.Undo(r.BatchId!);

        AssertOk(u);
        Assert.True(Item("shellex|" + Active).Enabled);
        Assert.False(Reg.KeyExists(Container));
    }

    [Fact]
    public void Delete_is_refused_when_the_reg_backup_fails_but_disable_still_works()
    {
        Env.RegExporter = (_, _) => false;
        var id = $@"verb|{Lm}\*\shell\ToDeskSendFile";

        var del = Apply(id, ActionKind.Delete);
        Assert.False(del.Items[0].Ok);
        Assert.Contains("备份没有成功", del.Items[0].Error);
        Assert.True(Reg.KeyExists($@"{Lm}\*\shell\ToDeskSendFile"));
        Assert.Equal("failed", Engine.History.Load(del.BatchId!)!.Items[0].Status);

        var dis = Apply(id, ActionKind.Disable);
        AssertOk(dis);
        Assert.Empty(Engine.History.Load(dis.BatchId!)!.Items[0].BackupFiles);
    }

    [Fact]
    public void Undoing_the_later_batch_revives_the_entry_it_superseded()
    {
        var id = "shellex|" + Active;
        var a = Apply(id, ActionKind.Disable);
        var b = Apply(id, ActionKind.Enable);
        Assert.Equal("superseded", Engine.History.Load(a.BatchId!)!.Items[0].Status);

        AssertOk(Engine.Undo(b.BatchId!));          // 撤销“启用”→ 又回到禁用状态

        Assert.False(Item(id).Enabled);
        var revived = Engine.History.Load(a.BatchId!)!.Items[0];
        Assert.Equal("applied", revived.Status);
        Assert.Null(revived.SupersededBy);

        AssertOk(Engine.Undo(a.BatchId!));          // 现在第一批又可以撤销了
        Assert.True(Item(id).Enabled);
    }

    [Fact]
    public void Disabled_core_and_system_handlers_can_be_restored_without_unlocking()
    {
        // 模拟别的工具把“打开方式”（核心项）和“授予访问权限”（系统项）挪到了禁用位置
        foreach (var name in new[] { "Open With", "Sharing" })
            new MoveKeyStep { From = $@"{Lm}\*\shellex\ContextMenuHandlers\{name}", To = $@"{Lm}\*\shellex\-ContextMenuHandlers\{name}" }.Run(Reg);
        Assert.False(Env.AllowSystem);

        foreach (var name in new[] { "Open With", "Sharing" })
        {
            var id = $@"shellex|{Lm}\*\shellex\ContextMenuHandlers\{name}";
            Assert.False(Item(id).Enabled);
            AssertOk(Apply(id, ActionKind.Enable));
            Assert.True(Item(id).Enabled);
            // 恢复之后仍然不能禁用
            Assert.False(Apply(id, ActionKind.Disable).Items[0].Ok);
        }
    }

    [Fact]
    public void Natively_hidden_system_verbs_stay_locked()
    {
        var r = Apply($@"verb|{Lm}\*\shell\removeproperties", ActionKind.Enable);
        Assert.False(r.Items[0].Ok);
        Assert.Equal("", Reg.ReadString($@"{Lm}\*\shell\removeproperties", "ProgrammaticAccessOnly"));
    }

    [Fact]
    public void SendTo_file_with_only_the_hidden_attribute_can_be_enabled()
    {
        var file = Path.Combine(Env.SendToDir, "Hidden Tool.lnk");
        Assert.Equal(FileAttributes.Hidden, File.GetAttributes(file));

        AssertOk(Apply("sendto|Hidden Tool.lnk", ActionKind.Enable));

        Assert.False(File.GetAttributes(file).HasFlag(FileAttributes.Hidden));
        Assert.True(Item("sendto|Hidden Tool.lnk").Enabled);
    }

    [Theory]
    [InlineData(@"\\no-such-server\share\tool.exe")]
    [InlineData(@"\\?\UNC\no-such-server\share\x.dll")]
    public void Network_paths_are_never_probed(string path)
    {
        Assert.False(TextUtil.CanProbe(path));
        Assert.Null(TextUtil.GetFileMeta(path));
        Assert.Null(TextUtil.ExtractExePath($"\"{path}\" \"%1\""));
        Assert.Null(IconService.GetDataUri(path));
    }

    [Fact]
    public void ReadOnly_preview_does_not_write_the_settings_file()
    {
        Env.ReadOnly = true;
        var rpc = new RpcHost(Engine, shellActions: false, version: "test");
        rpc.Handle("setAllowSystem", System.Text.Json.JsonSerializer.SerializeToElement(new { value = true }));
        Assert.True(Env.AllowSystem);
        Assert.False(File.Exists(Path.Combine(DataDir, "settings.json")));
    }
}
