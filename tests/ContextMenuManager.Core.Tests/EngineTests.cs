using System.Text.Json;
using ContextMenuManager.Core;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuManager.Core.Tests;

/// <summary>
/// 每个测试用一个独立的沙箱（HKCU\Software\ContextMenuManager.Sandbox\test-xxxx）和临时数据目录，
/// 结束后清理。真实的右键菜单注册不会被碰到。
/// </summary>
public abstract class SandboxTest : IDisposable
{
    protected readonly AppEnv Env;
    protected readonly Engine Engine;
    protected readonly string DataDir;
    protected IRegistryEnv Reg => Env.Reg;

    protected const string Lm = RegPath.HklmClasses;
    protected const string Cu = RegPath.HkcuClasses;

    protected SandboxTest()
    {
        var name = "test-" + Guid.NewGuid().ToString("N")[..10];
        DataDir = Path.Combine(Path.GetTempPath(), "cmm-tests", name);
        Env = SandboxSeeder.CreateSeeded(name, DataDir);
        Engine = new Engine(Env);
    }

    public void Dispose()
    {
        ((SandboxRegistryEnv)Env.Reg).Destroy();
        try
        {
            foreach (var f in Directory.GetFiles(DataDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(DataDir, true);
        }
        catch
        {
            // 临时目录清不掉不算测试失败
        }
        GC.SuppressFinalize(this);
    }

    protected MenuItem Item(string id) =>
        Engine.Scan().FirstOrDefault(i => i.Id == id) ?? throw new Xunit.Sdk.XunitException("扫描结果里没有：" + id);

    protected ApplyResult Apply(string id, ActionKind action) =>
        Engine.Apply([new ChangeRequest { Id = id, Action = action }]);

    protected static void AssertOk(ApplyResult r)
    {
        Assert.All(r.Items, i => Assert.True(i.Ok, i.Error));
    }
}

public class ScanTests : SandboxTest
{
    [Fact]
    public void Scan_classifies_third_party_system_and_protected()
    {
        var items = Engine.Scan();

        var todesk = items.Single(i => i.Id == $@"verb|{Lm}\*\shell\ToDeskSendFile");
        Assert.False(todesk.IsSystem);
        Assert.True(todesk.Enabled);
        Assert.Equal("使用 ToDesk 快传文件", todesk.Title);
        Assert.Equal(["file"], todesk.Scenes);

        var cmd = items.Single(i => i.Id == $@"verb|{Lm}\Directory\shell\cmd");
        Assert.True(cmd.IsSystem);
        Assert.Contains("extended", cmd.Flags);

        // 命令指向系统程序，但文字是软件自己写的：算第三方
        var demo = items.Single(i => i.Id == $@"verb|{Lm}\*\shell\DemoEditor");
        Assert.False(demo.IsSystem);
        Assert.Equal("用 Demo 编辑器打开", demo.Title);

        var openWith = items.Single(i => i.KeyName == "Open With");
        Assert.True(openWith.IsProtected);
        Assert.True(items.Single(i => i.Id == $@"verb|{Lm}\Folder\shell\open").IsProtected);

        var prev = items.Single(i => i.KeyName == "{596AB062-B4D2-4215-9F74-E9109B0A8153}");
        Assert.True(prev.IsSystem);
        Assert.Equal("还原以前的版本", prev.Title);
        Assert.Equal(["file", "folder"], prev.Scenes);
    }

    [Fact]
    public void Scan_reads_hidden_markers_and_mirrors_as_disabled()
    {
        var items = Engine.Scan();
        Assert.False(items.Single(i => i.Id == $@"verb|{Lm}\Directory\shell\find").Enabled);
        Assert.False(items.Single(i => i.Id == $@"verb|{Lm}\*\shell\removeproperties").Enabled);
        var old = items.Single(i => i.KeyName == "OldDisabledExt");
        Assert.False(old.Enabled);
        Assert.EndsWith(@"\-ContextMenuHandlers\OldDisabledExt", old.RegistryPath);
        Assert.False(items.Single(i => i.Id == $@"shellnew|{Cu}\.demo\ShellNew").Enabled);
        Assert.False(items.Single(i => i.Id == "sendto|Hidden Tool.lnk").Enabled);
    }

    [Fact]
    public void Scan_keeps_leading_spaces_in_key_names_and_reports_hive()
    {
        var items = Engine.Scan();
        var sync = items.Single(i => i.Id == $@"shellex|{Cu}\*\shellex\ContextMenuHandlers\ FileSyncEx");
        Assert.Equal(" FileSyncEx", sync.KeyName);
        Assert.Equal("FileSyncEx", sync.Title);
        Assert.Equal("HKCU", sync.Hive);
        Assert.Contains(items, i => i.KeyName == "  Quark.ContextMenuExt");
    }

    [Fact]
    public void Scan_packaged_groups_types_and_reads_blocked_state()
    {
        var items = Engine.Scan().Where(i => i.Kind == ItemKind.Packaged).ToList();
        Assert.Equal(3, items.Count);

        var clip = items.Single(i => i.Clsid == SandboxSeeder.ClipchampClsid);
        Assert.True(clip.Enabled);
        Assert.Equal([".mp4", ".mov", ".mkv"], clip.FileTypes);
        Assert.Equal("EditWith", clip.KeyName);
        Assert.False(clip.CanDelete);

        var term = items.Single(i => i.Clsid == SandboxSeeder.TerminalClsid);
        Assert.Equal(["Directory", @"Directory\Background"], term.FileTypes);

        var blocked = items.Single(i => i.Clsid == SandboxSeeder.MachineBlockedClsid);
        Assert.False(blocked.Enabled);
        Assert.Contains("machineBlocked", blocked.Flags);
    }

    [Fact]
    public void Scan_packaged_reads_manifest_stored_outside_package_root()
    {
        // 稀疏包：包目录是软件安装位置，清单在别处（真实系统里是 WindowsApps）
        var manifestDir = Path.Combine(DataDir, "staged", "Sparse.App_1.0.0.0_neutral__abc123");
        Directory.CreateDirectory(manifestDir);
        File.Copy(Path.Combine(DataDir, "packages", "Clipchamp.Clipchamp_4.5.0.0_x64__yxz26nhyzhsrt", "AppxManifest.xml"),
            Path.Combine(manifestDir, "AppxManifest.xml"));
        var installDir = Path.Combine(DataDir, "install-location-without-manifest");
        Directory.CreateDirectory(installDir);
        var env = AppEnv.CreateSandbox(((SandboxRegistryEnv)Reg).Name, DataDir, new StaticPackageSource(
        [
            new PackageInfo("Sparse.App_1.0.0.0_neutral__abc123", installDir, Path.Combine(manifestDir, "AppxManifest.xml")),
            new PackageInfo("No.Manifest_1.0.0.0_neutral__zzz", installDir),
        ]));

        var item = Assert.Single(new Scanner(env).ScanPackaged());
        Assert.Equal($"pkg|Sparse.App_abc123|{SandboxSeeder.ClipchampClsid}", item.Id);
        Assert.Equal(installDir, item.FilePath);
    }

    [Fact]
    public void Scan_sendto_and_shellnew()
    {
        var items = Engine.Scan();
        var desk = items.Single(i => i.Id == "sendto|Desktop (create shortcut).DeskLink");
        Assert.True(desk.IsSystem);
        Assert.DoesNotContain(items, i => i.KeyName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));

        var txt = items.Single(i => i.Id == $@"shellnew|{Lm}\.txt\ShellNew");
        Assert.True(txt.IsSystem);
        Assert.Equal("文本文档", txt.Title);
        var docx = items.Single(i => i.Id == $@"shellnew|{Lm}\.docx\Word.Document.12\ShellNew");
        Assert.False(docx.IsSystem);
        Assert.Equal("Microsoft Word 文档", docx.Title);
    }

    [Fact]
    public void ScanFileType_follows_progid_and_system_file_associations()
    {
        var items = Engine.ScanFileType("jpg");
        Assert.Contains(items, i => i.Id == $@"verb|{Lm}\jpegfile\shell\DemoViewer");
        Assert.Contains(items, i => i.Id == $@"verb|{Lm}\SystemFileAssociations\image\shell\DemoConvert");
        Assert.Contains(items, i => i.Id == $@"shellex|{Lm}\SystemFileAssociations\.jpg\shellex\ContextMenuHandlers\DemoThumb");
        Assert.All(items, i => Assert.Equal(["filetype"], i.Scenes));
    }

    [Fact]
    public void Locate_returns_same_item_as_scan()
    {
        var scanner = new Scanner(Env);
        foreach (var item in Engine.Scan().Concat(Engine.ScanFileType(".jpg")))
        {
            var located = scanner.Locate(item.Id);
            Assert.NotNull(located);
            Assert.Equal(item.Enabled, located.Enabled);
            Assert.Equal(item.Title, located.Title);
            Assert.Equal(item.RegistryPath, located.RegistryPath);
        }
    }
}

public class VerbTests : SandboxTest
{
    private const string Id = $@"verb|{Lm}\*\shell\ToDeskSendFile";
    private const string Key = $@"{Lm}\*\shell\ToDeskSendFile";

    [Fact]
    public void Disable_writes_marker_backs_up_and_undo_restores()
    {
        var before = Reg.Snapshot(Key)!;

        var r = Apply(Id, ActionKind.Disable);
        AssertOk(r);
        Assert.NotNull(r.BatchId);
        Assert.False(r.RestartSuggested);

        Assert.Equal("", Reg.ReadString(Key, "ProgrammaticAccessOnly"));
        Assert.False(Item(Id).Enabled);

        // 历史和 .reg 备份都落盘了
        var batch = Engine.History.Load(r.BatchId!)!;
        var bi = Assert.Single(batch.Items);
        Assert.Equal("applied", bi.Status);
        var backup = Assert.Single(bi.BackupFiles);
        var regText = File.ReadAllText(Path.Combine(Engine.History.DirOf(r.BatchId!), backup));
        Assert.Contains("ToDeskSendFile", regText);

        var u = Engine.Undo(r.BatchId!);
        AssertOk(u);
        Assert.True(Item(Id).Enabled);
        Assert.True(before.SameAs(Reg.Snapshot(Key)!));
        Assert.Equal("undone", Engine.History.Load(r.BatchId!)!.Items[0].Status);
    }

    [Fact]
    public void Disable_twice_is_noop_and_creates_no_batch()
    {
        AssertOk(Apply(Id, ActionKind.Disable));
        var again = Apply(Id, ActionKind.Disable);
        Assert.True(again.Items[0].NoOp);
        Assert.Null(again.BatchId);
        Assert.Single(Engine.History.List());
    }

    [Fact]
    public void Enable_removes_markers_written_by_other_tools_and_undo_puts_them_back()
    {
        using (var k = Reg.Open(Key, true)!)
        {
            k.SetValue("LegacyDisable", "");
            k.SetValue("HideBasedOnVelocityId", 0x639bc8, RegistryValueKind.DWord);
        }
        Assert.False(Item(Id).Enabled);

        var r = Apply(Id, ActionKind.Enable);
        AssertOk(r);
        Assert.True(Item(Id).Enabled);
        Assert.Null(Reg.ReadValue(Key, "LegacyDisable"));

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.Equal("", Reg.ReadString(Key, "LegacyDisable"));
        var dword = Reg.ReadValue(Key, "HideBasedOnVelocityId")!;
        Assert.Equal(nameof(RegistryValueKind.DWord), dword.Kind);
        Assert.Equal(0x639bc8, dword.N);
    }

    [Fact]
    public void Delete_removes_whole_key_and_undo_restores_it_exactly()
    {
        var before = Reg.Snapshot(Key)!;
        Assert.NotEmpty(before.SubKeys);

        var r = Apply(Id, ActionKind.Delete);
        AssertOk(r);
        Assert.False(Reg.KeyExists(Key));
        Assert.DoesNotContain(Engine.Scan(), i => i.Id == Id);

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.True(before.SameAs(Reg.Snapshot(Key)!));
    }

    [Fact]
    public void Undo_refuses_when_marker_was_changed_by_someone_else()
    {
        var r = Apply(Id, ActionKind.Disable);
        using (var k = Reg.Open(Key, true)!) k.SetValue("ProgrammaticAccessOnly", "changed by another tool");

        var u = Engine.Undo(r.BatchId!);
        Assert.False(u.Items[0].Ok);
        Assert.Contains("已被其他程序改动", u.Items[0].Error);
        Assert.Equal("changed by another tool", Reg.ReadString(Key, "ProgrammaticAccessOnly"));
        Assert.Equal("applied", Engine.History.Load(r.BatchId!)!.Items[0].Status);
    }
}

public class HandlerTests : SandboxTest
{
    private const string Active = $@"{Lm}\Directory\shellex\ContextMenuHandlers\YunShellExt";
    private const string Mirror = $@"{Lm}\Directory\shellex\-ContextMenuHandlers\YunShellExt";
    private const string Id = "shellex|" + Active;

    [Fact]
    public void Disable_moves_key_to_mirror_and_undo_moves_it_back()
    {
        using (var k = Reg.Open(Active, true)!)
        {
            k.SetValue("Extra", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            using var sub = k.CreateSubKey("Sub");
            sub.SetValue("Path", "%SystemRoot%\\x.dll", RegistryValueKind.ExpandString);
        }
        var before = Reg.Snapshot(Active)!;

        var r = Apply(Id, ActionKind.Disable);
        AssertOk(r);
        Assert.True(r.RestartSuggested);
        Assert.False(Reg.KeyExists(Active));
        Assert.True(before.SameAs(Reg.Snapshot(Mirror)!));
        // 展开字符串不能在移动时被展开
        Assert.Equal(nameof(RegistryValueKind.ExpandString), Reg.ReadValue(Mirror + @"\Sub", "Path")!.Kind);
        Assert.Equal("%SystemRoot%\\x.dll", Reg.ReadValue(Mirror + @"\Sub", "Path")!.S);
        var item = Item(Id);
        Assert.False(item.Enabled);
        Assert.Equal(Mirror, item.RegistryPath);

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.True(before.SameAs(Reg.Snapshot(Active)!));
        Assert.False(Reg.KeyExists(Mirror));
        Assert.True(Item(Id).Enabled);
    }

    [Fact]
    public void Disable_creates_mirror_container_and_undo_cleans_it_up()
    {
        const string container = $@"{Lm}\*\shellex\-ContextMenuHandlers";
        Assert.False(Reg.KeyExists(container));
        var r = Apply($@"shellex|{Lm}\*\shellex\ContextMenuHandlers\7-Zip", ActionKind.Disable);
        AssertOk(r);
        Assert.True(Reg.KeyExists(container + @"\7-Zip"));

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.False(Reg.KeyExists(container));
    }

    [Fact]
    public void Disable_and_enable_work_for_key_names_with_leading_spaces()
    {
        const string id = $@"shellex|{Cu}\Directory\shellex\ContextMenuHandlers\  Quark.ContextMenuExt";
        AssertOk(Apply(id, ActionKind.Disable));
        Assert.True(Reg.KeyExists($@"{Cu}\Directory\shellex\-ContextMenuHandlers\  Quark.ContextMenuExt"));
        Assert.False(Item(id).Enabled);

        AssertOk(Apply(id, ActionKind.Enable));
        Assert.True(Reg.KeyExists($@"{Cu}\Directory\shellex\ContextMenuHandlers\  Quark.ContextMenuExt"));
        Assert.False(Reg.KeyExists($@"{Cu}\Directory\shellex\-ContextMenuHandlers"));
    }

    [Fact]
    public void Enable_moves_previously_disabled_item_back()
    {
        const string id = $@"shellex|{Lm}\Directory\shellex\ContextMenuHandlers\OldDisabledExt";
        var r = Apply(id, ActionKind.Enable);
        AssertOk(r);
        Assert.True(Reg.KeyExists($@"{Lm}\Directory\shellex\ContextMenuHandlers\OldDisabledExt"));
        Assert.False(Reg.KeyExists($@"{Lm}\Directory\shellex\-ContextMenuHandlers"));

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.True(Reg.KeyExists($@"{Lm}\Directory\shellex\-ContextMenuHandlers\OldDisabledExt"));
        Assert.False(Item(id).Enabled);
    }

    [Fact]
    public void Stale_mirror_is_replaced_on_disable_and_both_restored_on_undo()
    {
        // 模拟：之前禁用过，软件更新后又把启用位置注册回来了
        using (var k = Reg.Create(Mirror)) k.SetValue("", "{OLD}");
        var activeBefore = Reg.Snapshot(Active)!;
        var mirrorBefore = Reg.Snapshot(Mirror)!;
        var item = Item(Id);
        Assert.True(item.Enabled);
        Assert.Contains("staleMirror", item.Flags);

        var r = Apply(Id, ActionKind.Disable);
        AssertOk(r);
        Assert.False(Reg.KeyExists(Active));
        Assert.True(activeBefore.SameAs(Reg.Snapshot(Mirror)!));

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.True(activeBefore.SameAs(Reg.Snapshot(Active)!));
        Assert.True(mirrorBefore.SameAs(Reg.Snapshot(Mirror)!));
    }

    [Fact]
    public void Undo_refuses_when_software_re_registered_the_handler()
    {
        var r = Apply(Id, ActionKind.Disable);
        using (var k = Reg.Create(Active)) k.SetValue("", "{NEW}");

        var u = Engine.Undo(r.BatchId!);
        Assert.False(u.Items[0].Ok);
        Assert.Contains("目标位置已存在", u.Items[0].Error);
        Assert.Equal("{NEW}", Reg.ReadString(Active));
        Assert.True(Reg.KeyExists(Mirror));
    }

    [Fact]
    public void Delete_handler_and_undo()
    {
        var before = Reg.Snapshot(Active)!;
        var r = Apply(Id, ActionKind.Delete);
        AssertOk(r);
        Assert.False(Reg.KeyExists(Active));
        AssertOk(Engine.Undo(r.BatchId!));
        Assert.True(before.SameAs(Reg.Snapshot(Active)!));
    }

    [Fact]
    public void DragDrop_handler_uses_its_own_mirror_container()
    {
        const string id = $@"dragdrop|{Lm}\Directory\shellex\DragDropHandlers\7-Zip";
        var r = Apply(id, ActionKind.Disable);
        AssertOk(r);
        Assert.True(Reg.KeyExists($@"{Lm}\Directory\shellex\-DragDropHandlers\7-Zip"));
        // 同名的右键菜单扩展不受影响
        Assert.True(Reg.KeyExists($@"{Lm}\Directory\shellex\ContextMenuHandlers\7-Zip"));
        AssertOk(Engine.Undo(r.BatchId!));
        Assert.True(Reg.KeyExists($@"{Lm}\Directory\shellex\DragDropHandlers\7-Zip"));
    }
}

public class OtherKindTests : SandboxTest
{
    [Fact]
    public void ShellNew_disable_renames_key_and_undo_restores()
    {
        const string id = $@"shellnew|{Lm}\.docx\Word.Document.12\ShellNew";
        var r = Apply(id, ActionKind.Disable);
        AssertOk(r);
        Assert.False(Reg.KeyExists($@"{Lm}\.docx\Word.Document.12\ShellNew"));
        Assert.Equal("template.docx", Reg.ReadString($@"{Lm}\.docx\Word.Document.12\-ShellNew", "FileName"));
        Assert.False(Item(id).Enabled);

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.True(Item(id).Enabled);
    }

    [Fact]
    public void ShellNew_enable_from_disabled_state()
    {
        const string id = $@"shellnew|{Cu}\.demo\ShellNew";
        AssertOk(Apply(id, ActionKind.Enable));
        Assert.True(Reg.KeyExists($@"{Cu}\.demo\ShellNew"));
        Assert.False(Reg.KeyExists($@"{Cu}\.demo\-ShellNew"));
    }

    [Fact]
    public void Packaged_disable_writes_user_blocked_value_and_undo_removes_key_it_created()
    {
        var id = $"pkg|Clipchamp.Clipchamp_yxz26nhyzhsrt|{SandboxSeeder.ClipchampClsid}";
        Assert.False(Reg.KeyExists(RegPath.HkcuBlocked));

        var r = Apply(id, ActionKind.Disable);
        AssertOk(r);
        Assert.Equal(Planner.BlockedMarker, Reg.ReadString(RegPath.HkcuBlocked, SandboxSeeder.ClipchampClsid));
        var item = Item(id);
        Assert.False(item.Enabled);
        Assert.Contains("userBlocked", item.Flags);

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.False(Reg.KeyExists(RegPath.HkcuBlocked));
        Assert.True(Item(id).Enabled);
    }

    [Fact]
    public void Packaged_disable_keeps_other_values_in_blocked_key()
    {
        using (var k = Reg.Create(RegPath.HkcuBlocked)) k.SetValue("{12345678-1234-1234-1234-123456789012}", "someone else");
        var id = $"pkg|Clipchamp.Clipchamp_yxz26nhyzhsrt|{SandboxSeeder.ClipchampClsid}";
        var r = Apply(id, ActionKind.Disable);
        AssertOk(Engine.Undo(r.BatchId!));
        Assert.Equal("someone else", Reg.ReadString(RegPath.HkcuBlocked, "{12345678-1234-1234-1234-123456789012}"));
    }

    [Fact]
    public void Packaged_enable_removes_machine_block_and_undo_restores_original_data()
    {
        var id = $"pkg|Demo.Blocked_abcdefgh12345|{SandboxSeeder.MachineBlockedClsid}";
        var r = Apply(id, ActionKind.Enable);
        AssertOk(r);
        Assert.Null(Reg.ReadValue(RegPath.HklmBlocked, SandboxSeeder.MachineBlockedClsid));
        Assert.True(Item(id).Enabled);
        // 列表里别人的值不动
        Assert.NotNull(Reg.ReadValue(RegPath.HklmBlocked, "{9421DD08-935F-4701-A9CA-22DF90AC4EA6}"));

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.Equal("blocked by policy", Reg.ReadString(RegPath.HklmBlocked, SandboxSeeder.MachineBlockedClsid));
    }

    [Fact]
    public void Packaged_cannot_be_deleted()
    {
        var id = $"pkg|Clipchamp.Clipchamp_yxz26nhyzhsrt|{SandboxSeeder.ClipchampClsid}";
        var r = Apply(id, ActionKind.Delete);
        Assert.False(r.Items[0].Ok);
        Assert.Contains("不支持删除", r.Items[0].Error);
    }

    [Fact]
    public void SendTo_disable_hides_file_and_undo_unhides()
    {
        const string id = "sendto|MyUploader.lnk";
        var file = Path.Combine(Env.SendToDir, "MyUploader.lnk");
        var r = Apply(id, ActionKind.Disable);
        AssertOk(r);
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.Hidden));
        Assert.False(Item(id).Enabled);

        AssertOk(Engine.Undo(r.BatchId!));
        Assert.False(File.GetAttributes(file).HasFlag(FileAttributes.Hidden));
    }
}

public class SafetyTests : SandboxTest
{
    [Fact]
    public void System_items_are_locked_unless_allowed()
    {
        var id = $@"verb|{Lm}\Directory\shell\cmd";
        var r = Apply(id, ActionKind.Disable);
        Assert.False(r.Items[0].Ok);
        Assert.Contains("Windows 自带项", r.Items[0].Error);
        Assert.Null(Reg.ReadValue($@"{Lm}\Directory\shell\cmd", "ProgrammaticAccessOnly"));

        Env.AllowSystem = true;
        AssertOk(Apply(id, ActionKind.Disable));
        Assert.False(Item(id).Enabled);
    }

    [Fact]
    public void Protected_items_are_never_modifiable()
    {
        Env.AllowSystem = true;
        foreach (var id in new[]
                 {
                     $@"verb|{Lm}\Folder\shell\open",
                     $@"verb|{Lm}\exefile\shell\runas",
                     $@"shellex|{Lm}\*\shellex\ContextMenuHandlers\Open With",
                 })
        {
            foreach (var action in new[] { ActionKind.Disable, ActionKind.Delete })
            {
                var r = Apply(id, action);
                Assert.False(r.Items[0].Ok);
                Assert.Contains("核心", r.Items[0].Error);
            }
        }
        Assert.Empty(Engine.History.List());
    }

    [Fact]
    public void Unknown_id_fails_without_affecting_other_changes_in_batch()
    {
        var r = Engine.Apply(
        [
            new ChangeRequest { Id = $@"verb|{Lm}\*\shell\NoSuchVerb", Action = ActionKind.Disable },
            new ChangeRequest { Id = @"verb|HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", Action = ActionKind.Delete },
            new ChangeRequest { Id = "garbage", Action = ActionKind.Disable },
            new ChangeRequest { Id = $@"verb|{Lm}\*\shell\ToDeskSendFile", Action = ActionKind.Disable },
        ]);
        Assert.Equal(3, r.Failed);
        Assert.Equal(1, r.Succeeded);
        Assert.False(Item($@"verb|{Lm}\*\shell\ToDeskSendFile").Enabled);
    }

    [Fact]
    public void ReadOnly_engine_refuses_apply_and_undo()
    {
        Env.ReadOnly = true;
        Assert.Throws<InvalidOperationException>(() => Apply($@"verb|{Lm}\*\shell\ToDeskSendFile", ActionKind.Disable));
        Assert.Throws<InvalidOperationException>(() => Engine.Undo("x"));
    }

    [Fact]
    public void Group_rolls_back_completed_steps_when_a_later_step_fails()
    {
        const string key = $@"{Lm}\*\shell\ToDeskSendFile";
        var group = new OpGroup
        {
            Key = "g",
            Ops =
            [
                new PlannedOp(new SetValueStep { Key = key, Value = RegValue.String("A", "1") }, new DeleteValueStep { Key = key, Name = "A" }),
                new PlannedOp(new MoveKeyStep { From = $@"{Lm}\NoSuch", To = $@"{Lm}\Other" }, new MoveKeyStep { From = $@"{Lm}\Other", To = $@"{Lm}\NoSuch" }),
            ],
        };
        var result = GroupRunner.Run(Reg, group);
        Assert.False(result.Ok);
        Assert.True(result.RolledBack);
        Assert.Null(Reg.ReadValue(key, "A"));
    }

    [Fact]
    public void Undo_of_single_item_leaves_rest_of_batch_applied()
    {
        var a = $@"verb|{Lm}\*\shell\ToDeskSendFile";
        var b = $@"shellex|{Lm}\*\shellex\ContextMenuHandlers\YunShellExt";
        var r = Engine.Apply([new ChangeRequest { Id = a, Action = ActionKind.Disable }, new ChangeRequest { Id = b, Action = ActionKind.Disable }]);
        AssertOk(r);

        AssertOk(Engine.Undo(r.BatchId!, [a]));
        Assert.True(Item(a).Enabled);
        Assert.False(Item(b).Enabled);
        var statuses = Engine.History.Load(r.BatchId!)!.Items.ToDictionary(i => i.ItemId, i => i.Status);
        Assert.Equal("undone", statuses[a]);
        Assert.Equal("applied", statuses[b]);

        // 再撤销一次整批：只处理还没撤销的
        var again = Engine.Undo(r.BatchId!);
        Assert.Single(again.Items);
        Assert.True(Item(b).Enabled);
    }
}

public class UtilTests
{
    [Theory]
    [InlineData("打开(&O)", "打开")]
    [InlineData("Open Git Ba&sh here", "Open Git Bash here")]
    [InlineData("Tom && Jerry", "Tom & Jerry")]
    [InlineData("  plain  ", "plain")]
    public void CleanMenuText_strips_accelerators(string input, string expected)
    {
        Assert.Equal(expected, TextUtil.CleanMenuText(input));
    }

    [Fact]
    public void ExtractExePath_handles_quotes_bare_names_and_spaces()
    {
        var sys = Environment.SystemDirectory;
        Assert.Equal(Path.Combine(sys, "notepad.exe"), TextUtil.ExtractExePath($"\"{sys}\\notepad.exe\" \"%1\""), ignoreCase: true);
        Assert.Equal(Path.Combine(sys, "cmd.exe"), TextUtil.ExtractExePath("cmd.exe /s /k pushd \"%V\""), ignoreCase: true);
        Assert.Equal(Path.Combine(sys, "rundll32.exe"), TextUtil.ExtractExePath("%SystemRoot%\\System32\\rundll32.exe shell32.dll,Foo"), ignoreCase: true);
        Assert.Null(TextUtil.ExtractExePath("\"C:\\NoSuchDir\\nothing.exe\" %1"));
        Assert.Null(TextUtil.ExtractExePath(""));
    }

    [Fact]
    public void NormalizeGuid_accepts_with_and_without_braces()
    {
        Assert.Equal("{8AB635F8-9A67-4698-AB99-784AD929F3B4}", TextUtil.NormalizeGuid("8ab635f8-9a67-4698-ab99-784ad929f3b4"));
        Assert.Equal("{8AB635F8-9A67-4698-AB99-784AD929F3B4}", TextUtil.NormalizeGuid(" {8AB635F8-9A67-4698-AB99-784AD929F3B4} "));
        Assert.Null(TextUtil.NormalizeGuid("7-Zip"));
    }

    [Fact]
    public void Steps_survive_json_round_trip()
    {
        var snap = new RegSnapshot { Values = [RegValue.String("", "x"), new RegValue { Name = "n", Kind = "DWord", N = 5 }] };
        snap.SubKeys["command"] = new RegSnapshot { Values = [new RegValue { Name = "m", Kind = "MultiString", M = ["a", "b"] }] };
        var ops = new List<PlannedOp>
        {
            new(new DeleteKeyStep { Key = "HKLM\\A" }, new RestoreKeyStep { Key = "HKLM\\A", Snapshot = snap }),
            new(new MoveKeyStep { From = "HKLM\\A", To = "HKLM\\B" }, new MoveKeyStep { From = "HKLM\\B", To = "HKLM\\A" }),
            new(new SetFileHiddenStep { Path = "C:\\x", Hidden = true }, new SetFileHiddenStep { Path = "C:\\x", Hidden = false }),
        };
        var json = JsonSerializer.Serialize(ops, Json.Pretty);
        var back = JsonSerializer.Deserialize<List<PlannedOp>>(json, Json.Pretty)!;
        Assert.IsType<DeleteKeyStep>(back[0].Do);
        var restore = Assert.IsType<RestoreKeyStep>(back[0].Undo);
        Assert.True(snap.SameAs(restore.Snapshot));
        Assert.IsType<MoveKeyStep>(back[1].Do);
        Assert.True(Assert.IsType<SetFileHiddenStep>(back[2].Do).Hidden);
    }
}

public class RpcTests : SandboxTest
{
    private static JsonElement Call(RpcHost rpc, string method, object? args = null)
    {
        var json = rpc.Handle(method, JsonSerializer.SerializeToElement(args ?? new { }, Json.Compact));
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Scan_apply_history_undo_through_rpc()
    {
        var rpc = new RpcHost(Engine, shellActions: false, version: "test");
        var id = $@"verb|{Lm}\*\shell\ToDeskSendFile";

        var info = Call(rpc, "info");
        Assert.True(info.GetProperty("ok").GetBoolean());
        Assert.True(info.GetProperty("result").GetProperty("sandbox").GetBoolean());

        var scan = Call(rpc, "scan").GetProperty("result").GetProperty("items");
        Assert.True(scan.GetArrayLength() > 20);
        Assert.Equal("Verb", scan.EnumerateArray().First(i => i.GetProperty("id").GetString() == id).GetProperty("kind").GetString());

        var apply = Call(rpc, "apply", new { changes = new[] { new { id, action = "Disable" } } }).GetProperty("result");
        var batchId = apply.GetProperty("batchId").GetString()!;
        Assert.Equal(1, apply.GetProperty("succeeded").GetInt32());

        var history = Call(rpc, "history").GetProperty("result").GetProperty("batches");
        Assert.Equal(batchId, history[0].GetProperty("id").GetString());
        Assert.Equal("applied", history[0].GetProperty("items")[0].GetProperty("status").GetString());

        var undo = Call(rpc, "undo", new { batchId }).GetProperty("result");
        Assert.Equal(1, undo.GetProperty("succeeded").GetInt32());
        Assert.True(Item(id).Enabled);
    }

    [Fact]
    public void Errors_and_shell_actions_are_reported_not_thrown()
    {
        var rpc = new RpcHost(Engine, shellActions: false, version: "test");
        Assert.False(Call(rpc, "nope").GetProperty("ok").GetBoolean());
        var restart = Call(rpc, "restartExplorer");
        Assert.False(restart.GetProperty("ok").GetBoolean());
        Assert.Contains("预览模式", restart.GetProperty("error").GetString());
        Assert.False(Call(rpc, "apply", new { changes = Array.Empty<object>() }).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void AllowSystem_setting_is_persisted()
    {
        var rpc = new RpcHost(Engine, shellActions: false, version: "test");
        Assert.False(Env.AllowSystem);
        Call(rpc, "setAllowSystem", new { value = true });
        Assert.True(Env.AllowSystem);
        Assert.True(AppSettings.Load(DataDir).AllowSystem);
    }
}
