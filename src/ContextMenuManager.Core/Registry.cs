using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ContextMenuManager.Core;

/// <summary>
/// 逻辑注册表路径的工具方法。逻辑路径形如 <c>HKLM\SOFTWARE\Classes\*\shell\X</c>，
/// 由 <see cref="IRegistryEnv"/> 映射到真实位置或沙箱位置。
/// 注意：键名可以带前导空格（OneDrive、夸克都这么干），所以任何地方都不能 Trim 路径片段。
/// </summary>
public static class RegPath
{
    public const string HklmClasses = @"HKLM\SOFTWARE\Classes";
    public const string HkcuClasses = @"HKCU\Software\Classes";
    public const string BlockedSuffix = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    public const string HkcuBlocked = @"HKCU\" + BlockedSuffix;
    public const string HklmBlocked = @"HKLM\" + BlockedSuffix;

    public static string Combine(string parent, string child) => parent + "\\" + child;

    public static string Parent(string path)
    {
        var i = path.LastIndexOf('\\');
        return i < 0 ? "" : path[..i];
    }

    public static string Leaf(string path)
    {
        var i = path.LastIndexOf('\\');
        return i < 0 ? path : path[(i + 1)..];
    }

    public static string Hive(string path)
    {
        var i = path.IndexOf('\\');
        return (i < 0 ? path : path[..i]).ToUpperInvariant();
    }

    public static string SubPath(string path)
    {
        var i = path.IndexOf('\\');
        return i < 0 ? "" : path[(i + 1)..];
    }

    public static bool IsUnder(string path, string root) =>
        path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == '\\';
}

/// <summary>把逻辑路径映射到物理注册表。真实环境直接映射；沙箱环境全部落到 HKCU 下的一个子树，供测试和界面开发用。</summary>
public interface IRegistryEnv
{
    bool IsSandbox { get; }
    RegistryKey? Open(string path, bool writable = false);
    RegistryKey Create(string path);
    void DeleteTree(string path);
    /// <summary>给 reg.exe 用的完整物理路径，例如 <c>HKEY_LOCAL_MACHINE\SOFTWARE\Classes\...</c>。</summary>
    string PhysicalPath(string path);
    /// <summary>写这个路径是否需要管理员权限。</summary>
    bool RequiresAdmin(string path);
}

public sealed class RealRegistryEnv : IRegistryEnv
{
    public bool IsSandbox => false;

    private static (RegistryKey root, string sub) Resolve(string path)
    {
        var hive = RegPath.Hive(path);
        var sub = RegPath.SubPath(path);
        return hive switch
        {
            "HKLM" => (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), sub),
            "HKCU" => (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64), sub),
            _ => throw new ArgumentException($"不支持的注册表根：{path}"),
        };
    }

    public RegistryKey? Open(string path, bool writable = false)
    {
        var (root, sub) = Resolve(path);
        using (root) return root.OpenSubKey(sub, writable);
    }

    public RegistryKey Create(string path)
    {
        var (root, sub) = Resolve(path);
        using (root) return root.CreateSubKey(sub, true) ?? throw new IOException($"无法创建注册表项：{path}");
    }

    public void DeleteTree(string path)
    {
        var (root, sub) = Resolve(path);
        using (root) root.DeleteSubKeyTree(sub, false);
    }

    public string PhysicalPath(string path) =>
        (RegPath.Hive(path) == "HKLM" ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + "\\" + RegPath.SubPath(path);

    public bool RequiresAdmin(string path) => RegPath.Hive(path) == "HKLM";
}

public sealed class SandboxRegistryEnv : IRegistryEnv
{
    public const string SandboxParent = @"Software\ContextMenuManager.Sandbox";
    private readonly string _rootSub;

    public SandboxRegistryEnv(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/']) >= 0)
            throw new ArgumentException("沙箱名不能为空，也不能包含斜杠。");
        Name = name;
        _rootSub = SandboxParent + "\\" + name;
    }

    public string Name { get; }
    public bool IsSandbox => true;

    private string Map(string path)
    {
        var hive = RegPath.Hive(path);
        if (hive is not ("HKLM" or "HKCU")) throw new ArgumentException($"不支持的注册表根：{path}");
        var sub = RegPath.SubPath(path);
        return sub.Length == 0 ? _rootSub + "\\" + hive : _rootSub + "\\" + hive + "\\" + sub;
    }

    public RegistryKey? Open(string path, bool writable = false) => Registry.CurrentUser.OpenSubKey(Map(path), writable);

    public RegistryKey Create(string path) =>
        Registry.CurrentUser.CreateSubKey(Map(path), true) ?? throw new IOException($"无法创建注册表项：{path}");

    public void DeleteTree(string path) => Registry.CurrentUser.DeleteSubKeyTree(Map(path), false);

    public string PhysicalPath(string path) => "HKEY_CURRENT_USER\\" + Map(path);

    public bool RequiresAdmin(string path) => false;

    /// <summary>删除整个沙箱。</summary>
    public void Destroy() => Registry.CurrentUser.DeleteSubKeyTree(_rootSub, false);
}

/// <summary>可序列化的注册表值。</summary>
public sealed class RegValue
{
    public string Name { get; set; } = "";
    /// <summary>RegistryValueKind 的名字。</summary>
    public string Kind { get; set; } = nameof(RegistryValueKind.String);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? S { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? M { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? N { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? B { get; set; }

    public static RegValue String(string name, string data) => new() { Name = name, Kind = nameof(RegistryValueKind.String), S = data };

    public static RegValue? Read(RegistryKey key, string name)
    {
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null) return null;
        var kind = key.GetValueKind(name);
        var v = new RegValue { Name = name, Kind = kind.ToString() };
        switch (raw)
        {
            case string s: v.S = s; break;
            case string[] m: v.M = m; break;
            case int i: v.N = i; break;
            case long l: v.N = l; break;
            case byte[] b: v.B = Convert.ToBase64String(b); break;
            default: v.S = raw.ToString(); break;
        }
        return v;
    }

    public void Write(RegistryKey key)
    {
        var kind = Enum.TryParse<RegistryValueKind>(Kind, out var k) ? k : RegistryValueKind.String;
        object data = kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => S ?? "",
            RegistryValueKind.MultiString => M ?? [],
            RegistryValueKind.DWord => unchecked((int)(N ?? 0)),
            RegistryValueKind.QWord => N ?? 0L,
            _ => B is null ? [] : Convert.FromBase64String(B),
        };
        // None/Unknown 类型的值按二进制写回，类型保持不变
        key.SetValue(Name, data, kind is RegistryValueKind.Unknown ? RegistryValueKind.Binary : kind);
    }

    public bool SameData(RegValue? other) =>
        other is not null
        && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
        && S == other.S && N == other.N && B == other.B
        && ((M is null && other.M is null) || (M is not null && other.M is not null && M.SequenceEqual(other.M)));
}

/// <summary>一个注册表项及其全部子项、值的快照，用于移动、删除后恢复。</summary>
public sealed class RegSnapshot
{
    public List<RegValue> Values { get; set; } = [];
    public Dictionary<string, RegSnapshot> SubKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static RegSnapshot Capture(RegistryKey key)
    {
        var snap = new RegSnapshot();
        foreach (var name in key.GetValueNames())
        {
            var v = RegValue.Read(key, name);
            if (v is not null) snap.Values.Add(v);
        }
        foreach (var subName in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(subName);
            if (sub is not null) snap.SubKeys[subName] = Capture(sub);
        }
        return snap;
    }

    public void WriteTo(RegistryKey key)
    {
        foreach (var v in Values) v.Write(key);
        foreach (var (name, child) in SubKeys)
        {
            using var sub = key.CreateSubKey(name, true) ?? throw new IOException($"无法创建子项：{name}");
            child.WriteTo(sub);
        }
    }

    public bool SameAs(RegSnapshot other)
    {
        if (Values.Count != other.Values.Count || SubKeys.Count != other.SubKeys.Count) return false;
        foreach (var v in Values)
        {
            var o = other.Values.FirstOrDefault(x => string.Equals(x.Name, v.Name, StringComparison.OrdinalIgnoreCase));
            if (!v.SameData(o)) return false;
        }
        foreach (var (name, child) in SubKeys)
        {
            if (!other.SubKeys.TryGetValue(name, out var oc) || !child.SameAs(oc)) return false;
        }
        return true;
    }
}

public static class RegistryEnvExtensions
{
    public static bool KeyExists(this IRegistryEnv env, string path)
    {
        using var k = env.Open(path);
        return k is not null;
    }

    public static RegValue? ReadValue(this IRegistryEnv env, string path, string name)
    {
        using var k = env.Open(path);
        return k is null ? null : RegValue.Read(k, name);
    }

    public static string? ReadString(this IRegistryEnv env, string path, string name = "")
    {
        using var k = env.Open(path);
        return k?.GetValue(name) as string;
    }

    public static RegSnapshot? Snapshot(this IRegistryEnv env, string path)
    {
        using var k = env.Open(path);
        return k is null ? null : RegSnapshot.Capture(k);
    }

    public static bool IsEmptyKey(this IRegistryEnv env, string path)
    {
        using var k = env.Open(path);
        return k is not null && k.SubKeyCount == 0 && k.ValueCount == 0;
    }
}
