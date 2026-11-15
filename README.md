# 右键菜单管理器

查看并整理 Windows 右键菜单里由各种软件添加的项：不想要的可以关掉，关错了可以随时恢复。

- 覆盖文件、文件夹、桌面与空白处、磁盘、Win11 新式菜单、指定文件类型、发送到、新建、拖拽菜单等位置。
- 默认只“禁用”，不删除。每次改动前自动备份，并记入历史，可以整批或单项撤销。
- Windows 自带的菜单项默认锁定，核心项（打开、以管理员身份运行、打开方式等）始终不允许改动。

## 运行要求

- Windows 10 / 11，64 位。
- [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（Desktop Runtime x64）。
- Microsoft Edge WebView2 运行时（Windows 11 自带）。

## 使用

运行 `ContextMenuManager.exe`。

1. 左侧选场景，右侧列出这个位置上的菜单项。默认只显示第三方软件添加的项。
2. 拨动某一项的开关，或者勾选多项后在底部点“禁用 / 启用 / 删除”。这时只是标记，还没有真正改动。
   - 勾选时按住 Shift 可以连选一段；Ctrl+A 全选当前列表；Esc 取消选择。
   - 点一行可以展开详情：注册表位置、对应的程序文件、CLSID 等。
3. 点底部的“应用更改”，确认后生效。涉及“所有用户”的注册表时会请求管理员授权。
4. 想恢复时，到“历史与回滚”里撤销整批或其中一项；或者直接把开关拨回去再应用一次。

其他快捷键：F5 重新扫描，Ctrl+F 或 `/` 聚焦搜索框。

### 各场景对应的位置

| 场景 | 注册位置 |
|---|---|
| 文件 | `*`、`AllFilesystemObjects` |
| 文件夹 | `Directory`、`Folder`、`AllFilesystemObjects` |
| 桌面与空白处 | `Directory\Background`、`DesktopBackground` |
| 磁盘 | `Drive` |
| Win11 新菜单 | 应用包清单里声明的 `fileExplorerContextMenus`（含 VS Code、Notepad++ 这类稀疏包） |
| 指定文件类型 | 扩展名本身、它关联的 ProgID、`SystemFileAssociations` |
| 发送到 | `%APPDATA%\Microsoft\Windows\SendTo` 目录 |
| 新建 | 各扩展名下的 `ShellNew` |
| 拖拽菜单 | `shellex\DragDropHandlers` |
| 其他位置 | 快捷方式、程序文件、库、此电脑、回收站 |

以上位置同时查看“所有用户”（`HKLM\SOFTWARE\Classes`）和“当前用户”（`HKCU\Software\Classes`）两处。

## 禁用是怎么做的

每种来源都用可以原样恢复的方式：

| 来源 | 禁用方式 | 是否需要管理员 |
|---|---|---|
| 静态项（`shell\<动词>`） | 写入 `ProgrammaticAccessOnly` 值：只从菜单里隐藏，程序仍然可以调用这个动词 | 写在所有用户下时需要 |
| 扩展（`shellex\ContextMenuHandlers`） | 把子键移到同级的 `-ContextMenuHandlers` | 同上 |
| 拖拽扩展 | 把子键移到同级的 `-DragDropHandlers` | 同上 |
| Win11 应用声明项 | 把 CLSID 写入当前用户的 `Shell Extensions\Blocked` | 不需要 |
| 发送到 | 给文件加“隐藏”属性 | 不需要 |
| 新建 | 把 `ShellNew` 改名为 `-ShellNew` | 写在所有用户下时需要 |

“永久删除”是次要操作，在每一项的详情里。Win11 应用声明项和“发送到”只能禁用，不能删除。

删除并不是真的抹掉：整个键会被移到本工具的回收位置，从系统的角度看这个菜单项已经不存在，但随时可以从历史里原样移回。

- 所有用户的项移到 `HKLM\SOFTWARE\ContextMenuManager\Deleted\<编号>`。
- 当前用户的项移到 `HKCU\Software\ContextMenuManager\Deleted\<编号>`。
- 确定不再需要时，手动删掉这两个键即可彻底清除。
- 删除前必须先成功导出 `.reg` 备份，导出失败就不删。

## 回滚和备份

- 历史和备份放在 `%LOCALAPPDATA%\ContextMenuManager\history\<批次>\`。
- 每个批次有一份 `batch.json`（记录每一步改动和对应的逆操作），以及改动前用 `reg.exe` 导出的 `.reg` 文件。
- 在界面里撤销时，工具会先检查当前状态：
  - 如果别的程序改过同一处（比如软件更新后重新注册了菜单），它会停下来并说明原因，不会硬覆盖。
  - 如果目标状态已经成立（比如你已经手动恢复过），它会直接把记录标成已撤销。
  - 如果上次应用中途中断，历史里会显示“未完成”或“部分生效”，可以点“尝试恢复”。
- 同一项后来又被改动过时，更早的那条记录会显示“已被取代”，不再提供撤销；撤销后来的那次改动后，它会重新生效。
- 即使这个工具不能用了，也可以双击对应的 `.reg` 文件手动恢复；被禁用的扩展只要把键从 `-ContextMenuHandlers` 移回 `ContextMenuHandlers` 即可。

## 卸载

这个工具不安装任何东西，删除程序目录和 `%LOCALAPPDATA%\ContextMenuManager` 即可。

注意：已经禁用的菜单项不会因为删掉本工具而自动恢复。想恢复的，先在界面里启用（或在历史里撤销）再删除。

## 安全设计

- **系统项锁定**：组件文件位于 Windows 目录且出自微软的项，判定为系统项，默认隐藏并锁定。在“设置”里打开“允许修改系统项”后才能改。
- **核心项**：`open`、`runas`、`openas`、打开方式、新建、发送到等，任何情况下都不允许禁用或删除。
- **恢复不受锁定限制**：如果某个扩展或“新建”项已经被（别的工具）挪到了禁用位置，即使它是系统项或核心项，也可以在这里启用回去。
- **不接管权限**：遇到受系统保护（TrustedInstaller 所有）的键，工具会报告没有权限，不会去改所有者或访问控制列表。
- **提权范围**：界面本身不提权。只有写 `HKLM` 的步骤会交给一个提权进程，它在执行前逐步检查：
  - 请求必须由本程序自己发起，内容的校验值要和命令行里的一致。
  - 只接受几种形态固定的动作：
    - 在静态项上写入或去掉隐藏标记；
    - 在 Blocked 列表里增删一个 CLSID；
    - 把某一项在启用位置和它自己的禁用位置之间移动；
    - 把某一项移入或移出回收位置；
    - 创建或清理空的禁用容器。
  - “按记录的内容重建注册表项”和“直接删除”一律拒绝。
  - 核心项在这里再拦一次。

### 关于历史文件被篡改

历史文件放在用户目录里，任何以你的身份运行的程序都能改它，而撤销时会按里面的记录去写 `HKLM`。所以提权的一端不信任文件里的内容：

- 被删除的项停放在只有管理员能写的回收位置，恢复就是把它移回来，不会从历史文件里取内容。
- 即使历史文件被改，提权进程也写不出新的命令或新的组件引用。
- 篡改者最多能让它在已有的菜单项之间做上面那几种动作（例如隐藏或恢复某一项）。

这不等于防住了已经在你账户下运行的恶意程序：这个工具是放在普通目录里的便携程序，那样的程序可以直接替换它的 exe。想要更强的保证，就把程序目录放到只有管理员能写的位置。

## 命令行

`cmmcli.exe` 和界面共用同一个核心库：

```text
cmmcli scan [--scene <场景>] [--ext <.扩展名>] [--third-party] [--json]
cmmcli apply [--disable <id>]... [--enable <id>]... [--delete <id>]...
cmmcli history
cmmcli undo <批次ID> [--item <id>]...
```

`<id>` 用 `scan` 的输出。加 `--sandbox <名字> --seed` 可以在一套仿真数据上试，不碰真实菜单。

## 开发

```powershell
dotnet build ContextMenuManager.sln
dotnet test tests/ContextMenuManager.Core.Tests
.\publish.ps1            # 产物在 dist\ContextMenuManager
```

- `src/ContextMenuManager.Core`：扫描、计划、执行、历史、提权，全部逻辑都在这里。
- `src/ContextMenuManager.App`：WinForms + WebView2 外壳，界面在 `wwwroot`（原生 HTML/CSS/JS，没有构建步骤）。
- `src/ContextMenuManager.Cli`：命令行，以及开发用的界面预览服务。
- `tests`：单元测试全部跑在沙箱里（`HKCU\Software\ContextMenuManager.Sandbox\test-*`），不会碰真实菜单。
- `tools/MenuProbe`：不显示界面，直接向外壳要某个文件的经典右键菜单并打印出来，用来确认改动真的生效。

界面预览（在浏览器里调界面，数据是沙箱里的仿真数据）：

```powershell
cmmcli --sandbox ui --seed serve --port 5210 --wwwroot src\ContextMenuManager.App\wwwroot
```

`ContextMenuManager.exe --selftest <目录>` 会在不显示窗口的情况下加载界面、做一次只读扫描，并把页面状态和截图写到指定目录，用来检查外壳和消息桥是否正常。

再加上 `--selftest-action apply:<标题片段>` 或 `--selftest-action undo-latest`，它会像用户一样点界面上的控件，真的应用或撤销一次改动（窗口仍然不显示）。这会改动真实的菜单，只在需要端到端验证时使用。

## 已知限制

- 禁用扩展时是“复制到镜像位置再删除原位置”，键上单独设置的访问控制列表不会保留（会继承父键的）。
- 软件更新或修复安装可能重新注册被禁用的菜单项，这时它会重新出现，需要再禁用一次。
- Win11 应用声明项的显示名取自应用包，真正显示在菜单里的文字由应用运行时决定，两者可能不同。
- 通过 Blocked 列表禁用是按 CLSID 生效的：同一个应用声明的多种文件类型会一起被禁用。
- 只处理 64 位视图下的注册；只安装了 32 位外壳扩展的老软件不在范围内。
- 动词如果是通过 `AppliesTo` 之类的条件隐藏的，工具仍然显示为“已启用”。
- Win11 系统自带的命令（共享、发送到手机等）不在管理范围内。
- 微软商店应用（记事本、照片、终端、Clipchamp 等）声明的 Win11 菜单项不算系统项，不用解锁就能禁用。它们只对当前用户生效，随时可以恢复。
- 同名的菜单项如果同时注册在“所有用户”和“当前用户”下，列表里会出现两条，需要分别禁用。
- 经典扩展如果被 Blocked 列表阻止，列表里仍显示“已启用”，但会带一个“被 Blocked 列表阻止”的标签；这个工具不会替经典扩展改 Blocked 列表。
- 启用某个扩展后，如果它所在的 `-ContextMenuHandlers` 容器空了，工具会顺手删掉这个空键，即使它原本不是本工具建的。
- 位于网络路径（UNC、映射的网络盘）上的组件不会被读取文件信息和图标，这类项会显示为“组件缺失”。
- 提权进程等待管理员授权的超时处理没有在会弹出 UAC 提示的机器上验证过。

## 参考

机制上参考了 [PLFJY/ContextMenuMgr](https://github.com/PLFJY/ContextMenuMgr) 的公开文档（用 `ProgrammaticAccessOnly` 隐藏动词、`-ContextMenuHandlers` 镜像、当前用户 Blocked 列表），没有使用它的代码。
