using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ContextMenuManager.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace ContextMenuManager.App;

internal static class Program
{
    private const string MutexName = @"Local\ContextMenuManager.SingleInstance";
    internal const string WindowTitle = "右键菜单管理器";

    [STAThread]
    private static int Main(string[] args)
    {
        // 被自己以管理员身份重新启动时，只执行注册表改动，不显示界面
        if (ElevatedHelper.TryHandle(args)) return 0;

        var selfTest = args.Contains("--selftest");
        using var mutex = new Mutex(true, selfTest ? MutexName + ".SelfTest" : MutexName, out var first);
        if (!first && selfTest) return 3;
        if (!first)
        {
            var existing = NativeMethods.FindWindow(null, WindowTitle);
            if (existing != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(existing, 9 /* SW_RESTORE */);
                NativeMethods.SetForegroundWindow(existing);
            }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(devTools: args.Contains("--dev"), dataDir: ArgValue(args, "--data-dir"),
            selfTestDir: ArgValue(args, "--selftest"), selfTestAction: ArgValue(args, "--selftest-action")));
        return Environment.ExitCode;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

internal sealed class MainForm : Form
{
    private const string Host = "cmm.app";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Engine _engine;
    private readonly RpcHost _rpc;
    private readonly bool _devTools;
    private readonly string? _selfTestDir;
    private readonly string? _selfTestAction;

    /// <summary>自检模式下窗口放在屏幕外、完全透明、不进任务栏、不抢焦点。</summary>
    protected override bool ShowWithoutActivation => _selfTestDir is not null;

    public MainForm(bool devTools, string? dataDir = null, string? selfTestDir = null, string? selfTestAction = null)
    {
        _devTools = devTools;
        _selfTestDir = selfTestDir;
        _selfTestAction = selfTestDir is null ? null : selfTestAction;
        _engine = new Engine(AppEnv.CreateReal(dataDir));
        // 自检默认只读；只有明确指定了动作才允许改动
        if (selfTestDir is not null && _selfTestAction is null) _engine.Env.ReadOnly = true;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        _rpc = new RpcHost(_engine, shellActions: true, version);

        Text = Program.WindowTitle;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(900, 600);
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
        {
            if (s is not null) Icon = new Icon(s);
        }
        if (selfTestDir is not null)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            ShowInTaskbar = false;
            Opacity = 0;
        }
        Controls.Add(_web);
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Load += async (_, _) => await InitWebViewAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        base.OnFormClosed(e);
    }

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && IsHandleCreated) BeginInvoke(ApplyTheme);
    }

    /// <summary>让标题栏和窗口底色跟随系统的深浅色，避免加载时闪白。</summary>
    private void ApplyTheme()
    {
        var dark = IsSystemDark();
        var back = dark ? Color.FromArgb(0x1b, 0x1c, 0x20) : Color.FromArgb(0xf3, 0xf4, 0xf6);
        BackColor = back;
        _web.DefaultBackgroundColor = back;
        if (IsHandleCreated)
        {
            var value = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!File.Exists(Path.Combine(www, "index.html")))
                throw new FileNotFoundException("程序目录里缺少 wwwroot 界面文件，请重新解压完整的程序包。");

            var userData = Path.Combine(_engine.Env.DataDir, "WebView2");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;

            core.SetVirtualHostNameToFolderMapping(Host, www, CoreWebView2HostResourceAccessKind.Allow);
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = _devTools;
            s.AreDevToolsEnabled = _devTools;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = _devTools;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsSwipeNavigationEnabled = false;

            // 界面只加载本地文件，不允许跳到别处，也不允许弹新窗口
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || !u.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnWebMessage;
            if (_selfTestDir is not null)
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.__errs=[];addEventListener('error',e=>__errs.push(e.message));addEventListener('unhandledrejection',e=>__errs.push(String(e.reason)));");
                core.NavigationCompleted += async (_, _) => await RunSelfTestAsync();
            }
            core.Navigate($"https://{Host}/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "这台电脑没有安装 Microsoft Edge WebView2 运行时，界面无法显示。\n\nWindows 11 一般自带；如果被卸载了，可以到微软官网搜索“WebView2 Runtime”下载安装。",
                Program.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "界面初始化失败：\n" + ex.Message, Program.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    /// <summary>
    /// 自检：不显示窗口，等页面扫描完，把页面状态和一张截图写到指定目录后退出。
    /// 用来在不打扰桌面的前提下验证 WebView2 外壳、消息桥和真实扫描都能跑通。
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        var dir = _selfTestDir!;
        var report = new Dictionary<string, object?>();
        try
        {
            Directory.CreateDirectory(dir);
            var core = _web.CoreWebView2;
            await core.ExecuteScriptAsync(
                "(()=>{const s=document.createElement('style');s.textContent='*,*::before,*::after{animation-duration:.01ms!important;animation-delay:0s!important;transition-duration:.01ms!important}';document.head.appendChild(s)})()");
            var ready = false;
            for (var i = 0; i < 100 && !ready; i++)
            {
                await Task.Delay(200);
                ready = await core.ExecuteScriptAsync("document.querySelectorAll('#list .row').length>0||!!document.querySelector('#list .empty')") == "true";
            }
            report["ready"] = ready;
            await Task.Delay(1500); // 等图标加载
            const string probe = """
                JSON.stringify({
                  errors: window.__errs,
                  nav: [...document.querySelectorAll('#nav .nav-item')].map(b => b.innerText.replace(/\s+/g, ' ').trim()),
                  rows: [...document.querySelectorAll('#list .row')].map(r => r.querySelector('.row-title b').textContent + ' | ' + r.querySelector('.row-sub').textContent + ' | ' + r.querySelector('.state').textContent),
                  icons: document.querySelectorAll('#list .icon.has-img').length,
                  count: document.getElementById('listCount').textContent,
                  banner: document.getElementById('banner').hidden ? null : document.getElementById('banner').innerText,
                  env: document.getElementById('envBadge').innerText
                })
                """;
            var json = await core.ExecuteScriptAsync(probe);
            report["page"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Deserialize<string>(json)!);
            await using (var fs = File.Create(Path.Combine(dir, "main.png")))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            if (_selfTestAction is null)
            {
                foreach (var scene in new[] { "folder", "win11" })
                {
                    await core.ExecuteScriptAsync($"document.querySelector('.nav-item[data-scene={scene}]').click()");
                    await Task.Delay(1200);
                    await using var fs = File.Create(Path.Combine(dir, scene + ".png"));
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                }
                report["ok"] = ready;
            }
            else
            {
                report["ok"] = ready && await RunSelfTestActionAsync(core, dir, report);
            }
        }
        catch (Exception ex)
        {
            report["ok"] = false;
            report["exception"] = ex.ToString();
        }
        try { File.WriteAllText(Path.Combine(dir, "selftest.json"), JsonSerializer.Serialize(report, Json.Pretty)); } catch { /* 写不了就靠退出码 */ }
        Environment.ExitCode = report["ok"] is true ? 0 : 4;
        Close();
    }

    /// <summary>
    /// 自检里的真实操作：像用户一样点界面上的控件，走完“拨开关 → 应用更改 → 确认”或“历史 → 撤销整批”。
    /// 只在命令行明确给了 --selftest-action 时才会执行。
    ///   apply:&lt;标题片段&gt;   在“文件”场景里找到标题包含该片段的项，切换它的开关并应用
    ///   undo-latest          撤销历史里最新的一批
    /// </summary>
    private async Task<bool> RunSelfTestActionAsync(CoreWebView2 core, string dir, Dictionary<string, object?> report)
    {
        async Task<string> Eval(string js) => JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(js)) ?? "";
        async Task<string> WaitFor(string js, int seconds)
        {
            for (var i = 0; i < seconds * 5; i++)
            {
                var v = await Eval(js);
                if (v.Length > 0) return v;
                await Task.Delay(200);
            }
            return "";
        }
        async Task Shot(string name)
        {
            await using var fs = File.Create(Path.Combine(dir, name));
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        }
        const string outcome = "(()=>{const t=document.querySelector('.toast');if(t)return 'toast: '+t.innerText;const e=document.querySelector('.dialog li.err');return e?'error: '+document.querySelector('.dialog').innerText:''})()";

        var action = _selfTestAction!;
        if (action.StartsWith("apply:", StringComparison.Ordinal))
        {
            var title = JsonSerializer.Serialize(action["apply:".Length..]);
            var find = $"[...document.querySelectorAll('#list .row')].find(r=>r.querySelector('.row-title b').textContent.includes({title}))";
            report["target"] = await Eval($"(()=>{{const r={find};if(!r)return '';const s=r.querySelector('.switch');const was=s.getAttribute('aria-checked');s.click();return r.querySelector('.row-title b').textContent+' | 原状态 '+was+' → '+s.getAttribute('aria-checked')}})()");
            if ((string)report["target"]! == "") return false;
            await Task.Delay(300);
            await Eval("(()=>{document.getElementById('btnApply').click();return 'ok'})()");
            report["confirmDialog"] = await WaitFor("(()=>{const d=document.querySelector('.dialog');return d?d.innerText:''})()", 5);
            await Shot("confirm.png");
            await Eval("(()=>{const b=[...document.querySelectorAll('.dialog-foot .btn')].pop();b.click();return b.textContent})()");
            var result = await WaitFor(outcome, 60);
            report["result"] = result;
            await Task.Delay(800);
            report["rowAfter"] = await Eval($"(()=>{{const r={find};return r?r.querySelector('.state').textContent+' / 开关 '+r.querySelector('.switch').getAttribute('aria-checked'):'(行不见了)'}})()");
            await Shot("applied.png");
            return result.StartsWith("toast: 已应用", StringComparison.Ordinal);
        }

        if (action == "undo-latest")
        {
            await Eval("(()=>{document.querySelector('.nav-item[data-view=history]').click();return 'ok'})()");
            report["latestBatch"] = await WaitFor("(()=>{const b=document.querySelector('#history .batch');return b?b.innerText:''})()", 10);
            await Shot("history.png");
            var clicked = await Eval("(()=>{const b=document.querySelector('#history .batch [data-act=undoBatch]');if(!b)return '';b.click();return 'ok'})()");
            if (clicked == "") return false;
            await WaitFor("(()=>{const d=document.querySelector('.dialog');return d?'ok':''})()", 5);
            await Eval("(()=>{const b=[...document.querySelectorAll('.dialog-foot .btn')].pop();b.click();return b.textContent})()");
            var result = await WaitFor(outcome, 60);
            report["result"] = result;
            await Task.Delay(800);
            report["latestBatchAfter"] = await Eval("(()=>{const b=document.querySelector('#history .batch');return b?b.innerText:''})()");
            await Shot("undone.png");
            return result.StartsWith("toast: 已撤销", StringComparison.Ordinal);
        }

        report["error"] = "未知的自检动作：" + action;
        return false;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        long id;
        string method;
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt64();
            method = root.GetProperty("method").GetString() ?? "";
            args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
        }
        catch
        {
            return;
        }

        // 扫描、应用都可能花一点时间，放到后台线程，结果回到界面线程再发给页面
        Task.Run(() =>
        {
            var reply = _rpc.Handle(method, args);
            var json = "{\"id\":" + id + "," + reply[1..];
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (!IsDisposed && _web.CoreWebView2 is not null) _web.CoreWebView2.PostWebMessageAsJson(json);
            });
        });
    }
}

internal static class NativeMethods
{
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
