using System.Text.Json.Serialization;

namespace ContextMenuManager.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ItemKind>))]
public enum ItemKind
{
    /// <summary>经典静态项：<c>shell\&lt;verb&gt;</c>。</summary>
    Verb,
    /// <summary>经典扩展：<c>shellex\ContextMenuHandlers</c>。</summary>
    ShellEx,
    /// <summary>右键拖拽菜单：<c>shellex\DragDropHandlers</c>。</summary>
    DragDrop,
    /// <summary>Win11 新式菜单（打包应用声明的 IExplorerCommand）。</summary>
    Packaged,
    /// <summary>“发送到”目录里的文件。</summary>
    SendTo,
    /// <summary>“新建”子菜单：<c>.ext\ShellNew</c>。</summary>
    ShellNew,
}

[JsonConverter(typeof(JsonStringEnumConverter<ActionKind>))]
public enum ActionKind { Disable, Enable, Delete }

/// <summary>一个右键菜单项。Id 在启用、禁用前后保持不变。</summary>
public sealed class MenuItem
{
    public string Id { get; set; } = "";
    public ItemKind Kind { get; set; }
    /// <summary>这个项出现在哪些场景页里：file / folder / background / drive / other / dragdrop / win11 / sendto / shellnew / filetype。</summary>
    public List<string> Scenes { get; set; } = [];
    /// <summary>注册位置的中文说明，例如“所有文件”。</summary>
    public string LocationLabel { get; set; } = "";
    /// <summary>注册位置的类名，例如 <c>*</c>、<c>Directory\Background</c>。</summary>
    public string Location { get; set; } = "";
    public string Title { get; set; } = "";
    public string KeyName { get; set; } = "";
    public string? AppName { get; set; }
    public string? Publisher { get; set; }
    /// <summary>实现这个菜单项的 exe 或 dll。</summary>
    public string? FilePath { get; set; }
    public string? Command { get; set; }
    public string? Clsid { get; set; }
    /// <summary>取图标用的来源：文件路径，或 <c>路径,索引</c>，或图片文件。</summary>
    public string? IconSource { get; set; }
    /// <summary>HKLM（所有用户）或 HKCU（当前用户）；文件类项目为空。</summary>
    public string Hive { get; set; } = "";
    /// <summary>当前所在的逻辑路径（禁用后可能是镜像位置）。</summary>
    public string RegistryPath { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Windows 自带项，默认锁定。</summary>
    public bool IsSystem { get; set; }
    /// <summary>核心系统项，任何模式下都不允许改。</summary>
    public bool IsProtected { get; set; }
    public bool NeedsAdmin { get; set; }
    public bool CanDelete { get; set; }
    /// <summary>extended（按住 Shift 才显示）、submenu（级联菜单）、staleMirror（启用和禁用位置同时存在）、machineBlocked 等。</summary>
    public List<string> Flags { get; set; } = [];
    /// <summary>Win11 新式菜单适用的文件类型。</summary>
    public List<string>? FileTypes { get; set; }
    public string? Note { get; set; }
}

public sealed class ChangeRequest
{
    public string Id { get; set; } = "";
    public ActionKind Action { get; set; }
}

public sealed class ItemResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public ActionKind Action { get; set; }
    /// <summary>这是一次撤销：Action 是当初被撤销的那个动作。</summary>
    public bool Undo { get; set; }
    public bool Ok { get; set; }
    /// <summary>当前状态已经符合要求，没有做任何改动。</summary>
    public bool NoOp { get; set; }
    public string? Error { get; set; }
}

public sealed class ApplyResult
{
    public string? BatchId { get; set; }
    public List<ItemResult> Items { get; set; } = [];
    /// <summary>改动了扩展类菜单，资源管理器可能要重启才完全生效。</summary>
    public bool RestartSuggested { get; set; }
    public int Succeeded => Items.Count(i => i.Ok && !i.NoOp);
    public int Failed => Items.Count(i => !i.Ok);
}
