using System.Net;
using System.Text;
using System.Text.Json;
using ContextMenuManager.Core;

// 右键菜单管理器的命令行入口：给开发、测试和高级用户用。
// 图形界面和它共用同一个核心库。

if (ElevatedHelper.TryHandle(args)) return 0;

var opt = Options.Parse(args);
var output = new StringBuilder();
int exit;
try
{
    exit = Run(opt, output);
}
catch (Exception ex)
{
    output.AppendLine("错误：" + ex.Message);
    exit = 1;
}

if (opt.Out is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opt.Out))!);
    File.WriteAllText(opt.Out, output.ToString(), new UTF8Encoding(false));
}
else
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.Write(output.ToString());
}
return exit;

static int Run(Options opt, StringBuilder o)
{
    if (opt.Command is null or "help")
    {
        o.AppendLine("""
            用法：cmmcli [全局选项] <命令> [参数]

            命令：
              scan [--scene <场景>] [--ext <.扩展名>] [--third-party]   扫描右键菜单项
              apply [--disable <id>]... [--enable <id>]... [--delete <id>]...   应用改动（自动备份并记入历史）
              history                                         列出历史批次
              undo <批次ID> [--item <id>]...                   撤销一个批次（或其中几项）
              serve [--port 5210] [--wwwroot <目录>]           启动界面预览服务（非沙箱时只读）
              sandbox-destroy                                 删除指定沙箱

            全局选项：
              --sandbox <名字>   使用沙箱注册表（HKCU\Software\ContextMenuManager.Sandbox\<名字>），不碰真实菜单
              --seed             配合 --sandbox：先写入一套仿真数据
              --data-dir <目录>  历史和备份的存放目录（默认 %LOCALAPPDATA%\ContextMenuManager）
              --allow-system     允许改动 Windows 自带项（核心项仍然锁定）
              --json             输出 JSON
              --out <文件>       把输出写到文件（UTF-8）

            场景：file folder background drive other dragdrop win11 sendto shellnew
            """);
        return 0;
    }

    if (opt.Command == "sandbox-destroy")
    {
        if (opt.Sandbox is null) throw new ArgumentException("需要 --sandbox <名字>。");
        new SandboxRegistryEnv(opt.Sandbox).Destroy();
        o.AppendLine("已删除沙箱：" + opt.Sandbox);
        return 0;
    }

    var env = BuildEnv(opt);
    var engine = new Engine(env);

    switch (opt.Command)
    {
        case "scan":
        {
            var items = opt.Ext is not null ? engine.ScanFileType(opt.Ext) : engine.Scan();
            if (opt.Scene is not null) items = [.. items.Where(i => i.Scenes.Contains(opt.Scene, StringComparer.OrdinalIgnoreCase))];
            if (opt.ThirdParty) items = [.. items.Where(i => !i.IsSystem)];
            if (opt.Json)
            {
                o.AppendLine(JsonSerializer.Serialize(items, Json.Pretty));
            }
            else
            {
                foreach (var g in items.GroupBy(i => i.Scenes.FirstOrDefault() ?? ""))
                {
                    o.AppendLine($"== {g.Key} ==");
                    foreach (var i in g)
                    {
                        var state = i.Enabled ? "启用" : "禁用";
                        var tag = i.IsProtected ? "核心" : i.IsSystem ? "系统" : "第三方";
                        o.AppendLine($"  [{state}] [{tag}] [{i.Kind}] {i.Title}  （{i.LocationLabel} · {i.AppName ?? "-"}）");
                        o.AppendLine($"        {i.Id}");
                    }
                }
                o.AppendLine($"共 {items.Count} 项。");
            }
            return 0;
        }

        case "apply":
        {
            if (opt.Changes.Count == 0) throw new ArgumentException("没有指定改动：用 --disable / --enable / --delete <id>。");
            var result = engine.Apply(opt.Changes);
            WriteApply(opt, o, result);
            return result.Failed > 0 ? 2 : 0;
        }

        case "history":
        {
            var list = engine.History.List();
            if (opt.Json)
            {
                o.AppendLine(JsonSerializer.Serialize(list, Json.Pretty));
            }
            else
            {
                foreach (var b in list)
                {
                    o.AppendLine($"{b.Id}  {b.CreatedAt:yyyy-MM-dd HH:mm:ss}  {b.Title}");
                    foreach (var i in b.Items) o.AppendLine($"    [{i.Status}] {i.Action} {i.Title}  {i.Error}");
                }
                o.AppendLine($"共 {list.Count} 个批次。");
            }
            return 0;
        }

        case "undo":
        {
            var id = opt.Positional.FirstOrDefault() ?? throw new ArgumentException("需要批次 ID。");
            var result = engine.Undo(id, opt.Items.Count > 0 ? opt.Items : null);
            WriteApply(opt, o, result);
            return result.Failed > 0 ? 2 : 0;
        }

        case "serve":
            return Serve(opt, engine, o);

        default:
            throw new ArgumentException("未知命令：" + opt.Command);
    }
}

static AppEnv BuildEnv(Options opt)
{
    AppEnv env;
    if (opt.Sandbox is not null)
    {
        var dataDir = opt.DataDir ?? Path.Combine(Path.GetTempPath(), "ContextMenuManager.Sandbox", opt.Sandbox);
        env = opt.Seed ? SandboxSeeder.CreateSeeded(opt.Sandbox, dataDir) : AppEnv.CreateSandbox(opt.Sandbox, dataDir);
    }
    else
    {
        env = AppEnv.CreateReal(opt.DataDir);
    }
    env.AllowSystem = opt.AllowSystem;
    return env;
}

static void WriteApply(Options opt, StringBuilder o, ApplyResult result)
{
    if (opt.Json)
    {
        o.AppendLine(JsonSerializer.Serialize(result, Json.Pretty));
        return;
    }
    o.AppendLine($"批次：{result.BatchId ?? "(无改动)"}");
    foreach (var i in result.Items)
        o.AppendLine($"  [{(i.Ok ? (i.NoOp ? "无需改动" : "成功") : "失败")}] {(i.Undo ? "撤销 " : "")}{i.Action} {i.Title}  {i.Error}");
    if (result.RestartSuggested) o.AppendLine("提示：改动了扩展类菜单，重启资源管理器后完全生效。");
}

static int Serve(Options opt, Engine engine, StringBuilder o)
{
    var www = opt.WwwRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
    if (!Directory.Exists(www)) throw new DirectoryNotFoundException("找不到界面文件目录：" + www);
    if (!engine.Env.Reg.IsSandbox) engine.Env.ReadOnly = true;
    var rpc = new RpcHost(engine, shellActions: false, version: "dev");
    if (opt.AllowSystem) engine.Env.AllowSystem = true;

    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{opt.Port}/");
    listener.Start();
    Console.WriteLine($"预览服务已启动：http://localhost:{opt.Port}/  （{(engine.Env.Reg.IsSandbox ? "沙箱" : "真实注册表，只读")}）");

    while (true)
    {
        var ctx = listener.GetContext();
        _ = Task.Run(() =>
        {
            try { HandleRequest(ctx, www, rpc); }
            catch { try { ctx.Response.Abort(); } catch { /* 连接已断 */ } }
        });
    }
}

static void HandleRequest(HttpListenerContext ctx, string www, RpcHost rpc)
{
    var req = ctx.Request;
    var res = ctx.Response;
    res.Headers["Cache-Control"] = "no-store";
    if (req.HttpMethod == "POST" && req.Url!.AbsolutePath == "/rpc")
    {
        // 只接受预览页面自己发来的请求，别的网页不能借浏览器调用这个接口
        // Host 必须就是 localhost：防止别的域名被解析到本机后绕过同源检查
        var origin = req.Headers["Origin"];
        var hostOk = req.Url.IsLoopback && string.Equals(req.Url.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!hostOk || (origin is not null && !string.Equals(origin, $"http://{req.Url.Authority}", StringComparison.OrdinalIgnoreCase)))
        {
            res.StatusCode = 403;
            res.Close();
            return;
        }
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        using var doc = JsonDocument.Parse(reader.ReadToEnd());
        var method = doc.RootElement.GetProperty("method").GetString() ?? "";
        var args = doc.RootElement.TryGetProperty("args", out var a) ? a : default;
        var body = Encoding.UTF8.GetBytes(rpc.Handle(method, args));
        res.ContentType = "application/json; charset=utf-8";
        res.OutputStream.Write(body);
        res.Close();
        return;
    }

    var rel = Uri.UnescapeDataString(req.Url!.AbsolutePath).TrimStart('/');
    if (rel.Length == 0) rel = "index.html";
    var full = Path.GetFullPath(Path.Combine(www, rel));
    var root = Path.GetFullPath(www).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
    {
        res.StatusCode = 404;
        res.Close();
        return;
    }
    res.ContentType = Path.GetExtension(full).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };
    res.OutputStream.Write(File.ReadAllBytes(full));
    res.Close();
}

sealed class Options
{
    public string? Command;
    public string? Sandbox, DataDir, Out, Scene, Ext, WwwRoot;
    public bool Seed, AllowSystem, Json, ThirdParty;
    public int Port = 5210;
    public List<ChangeRequest> Changes = [];
    public List<string> Items = [];
    public List<string> Positional = [];

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} 后面缺少参数。");
            switch (a)
            {
                case "--sandbox": o.Sandbox = Next(); break;
                case "--seed": o.Seed = true; break;
                case "--data-dir": o.DataDir = Next(); break;
                case "--allow-system": o.AllowSystem = true; break;
                case "--json": o.Json = true; break;
                case "--out": o.Out = Next(); break;
                case "--scene": o.Scene = Next(); break;
                case "--ext": o.Ext = Next(); break;
                case "--third-party": o.ThirdParty = true; break;
                case "--port": o.Port = int.Parse(Next()); break;
                case "--wwwroot": o.WwwRoot = Next(); break;
                case "--disable": o.Changes.Add(new ChangeRequest { Id = Next(), Action = ActionKind.Disable }); break;
                case "--enable": o.Changes.Add(new ChangeRequest { Id = Next(), Action = ActionKind.Enable }); break;
                case "--delete": o.Changes.Add(new ChangeRequest { Id = Next(), Action = ActionKind.Delete }); break;
                case "--item": o.Items.Add(Next()); break;
                case "-h" or "--help": o.Command = "help"; break;
                default:
                    if (o.Command is null) o.Command = a;
                    else o.Positional.Add(a);
                    break;
            }
        }
        return o;
    }
}
