using ContextMenuManager.Core;
using Xunit;

namespace ContextMenuManager.Core.Tests;

public class IconTests
{
    [Fact]
    public void System_icon_resource_becomes_png_data_uri()
    {
        var uri = IconService.GetDataUri(Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",3");
        Assert.NotNull(uri);
        Assert.StartsWith("data:image/png;base64,", uri);
    }

    [Fact]
    public void Executable_without_index_uses_its_first_icon()
    {
        var uri = IconService.GetDataUri(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
        Assert.StartsWith("data:image/png;base64,", uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\NoSuchDir\nothing.dll")]
    [InlineData(@"C:\NoSuchDir\nothing.exe,-101")]
    public void Missing_source_gives_null_instead_of_throwing(string? source)
    {
        Assert.Null(IconService.GetDataUri(source));
    }
}

public class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cmm-tests", "hist-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(@"..\outside")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("")]
    [InlineData("..")]
    public void Batch_id_cannot_escape_history_directory(string id)
    {
        var store = new HistoryStore(_dir);
        Assert.Throws<ArgumentException>(() => store.DirOf(id));
    }

    [Fact]
    public void List_orders_newest_first_and_skips_unreadable_batches()
    {
        var store = new HistoryStore(_dir);
        store.Save(new Batch { Id = "20260101-000000-aaaa", CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)), Title = "旧" });
        store.Save(new Batch { Id = "20260201-000000-bbbb", CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(8)), Title = "新" });
        Directory.CreateDirectory(Path.Combine(store.Root, "broken"));
        File.WriteAllText(Path.Combine(store.Root, "broken", "batch.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(store.Root, "empty"));

        var list = store.List();

        Assert.Equal(["新", "旧"], list.Select(b => b.Title));
    }

    [Fact]
    public void Missing_batch_loads_as_null()
    {
        Assert.Null(new HistoryStore(_dir).Load("20260101-000000-none"));
    }
}
