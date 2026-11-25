using IsleWin.Core;
using Xunit;

namespace IsleWin.Tests;

public class ShelfTests
{
    static string[] Files(Tmp t, int n) => Enumerable.Range(0, n).Select(i => { var p = Path.Combine(t.Dir, $"f{i}.txt"); File.WriteAllText(p, "x"); return p; }).ToArray();
    static ShelfStore Open(Tmp t) => new(Path.Combine(t.Dir, "shelf.json"));

    [Fact]
    public void Keeps_order_and_caps_at_ten()
    {
        using var t = new Tmp(); using var s = Open(t);
        var f = Files(t, 12);
        Assert.Equal(10, s.Add(f));
        Assert.Equal(f.Take(10), s.Items.Select(i => i.Path));
        Assert.True(s.IsFull);
        Assert.Equal(0, s.Add(new[] { f[11] }));
        Assert.Equal(0, s.WouldKeep(new[] { f[11] }));
    }

    [Fact]
    public void Dedups_by_path_ignoring_case_and_within_one_drop()
    {
        using var t = new Tmp(); using var s = Open(t);
        var p = Files(t, 1)[0];
        Assert.Equal(1, s.Add(new[] { p, p.ToUpperInvariant(), p }));
        Assert.Equal(0, s.Add(new[] { p }));
        Assert.Single(s.Items);
    }

    [Fact]
    public void Persists_and_restores_in_order()
    {
        using var t = new Tmp();
        var f = Files(t, 3); Directory.CreateDirectory(Path.Combine(t.Dir, "dir"));
        using (var s = Open(t)) { s.Add(f); s.Add(new[] { Path.Combine(t.Dir, "dir") }); }
        using var again = Open(t);
        Assert.Equal(4, again.Count);
        Assert.Equal(f, again.Items.Take(3).Select(i => i.Path));
        Assert.True(again.Items[3].IsFolder);
        Assert.Equal("1 folder, 3 files", again.Tally);
    }

    [Fact]
    public void Missing_files_are_dropped_on_load()
    {
        using var t = new Tmp();
        var f = Files(t, 3);
        using (var s = Open(t)) s.Add(f);
        File.Delete(f[1]);
        using var again = Open(t);
        Assert.Equal(new[] { f[0], f[2] }, again.Items.Select(i => i.Path));
        Assert.DoesNotContain(f[1], File.ReadAllText(Path.Combine(t.Dir, "shelf.json")).Replace("\\\\", "\\"));
    }

    [Fact]
    public void Prune_drops_what_has_gone_and_never_touches_files()
    {
        using var t = new Tmp(); using var s = Open(t);
        var f = Files(t, 3); s.Add(f);
        var changed = 0; s.Changed += () => changed++;
        File.Delete(f[0]); File.Move(f[1], f[1] + ".moved");
        s.PruneNow();
        Assert.Equal(new[] { f[2] }, s.Items.Select(i => i.Path));
        Assert.Equal(1, changed);
        Assert.True(File.Exists(f[1] + ".moved") && File.Exists(f[2]));
    }

    [Fact]
    public void Prune_in_background_and_watcher_remove_a_deleted_file()
    {
        using var t = new Tmp(); using var s = Open(t);
        var f = Files(t, 2); s.Add(f);
        s.Prune();
        File.Delete(f[0]);
        for (var i = 0; i < 100 && s.Count > 1; i++) { s.Prune(); Thread.Sleep(50); }
        Assert.Equal(new[] { f[1] }, s.Items.Select(i => i.Path));
    }

    [Fact]
    public void Remove_and_clear_leave_the_files_alone()
    {
        using var t = new Tmp(); using var s = Open(t);
        var f = Files(t, 3); s.Add(f);
        Assert.True(s.Remove(f[1].ToUpperInvariant()));
        Assert.False(s.Remove(f[1]));
        Assert.Equal(2, s.Count);
        s.Clear();
        Assert.True(s.IsEmpty);
        Assert.All(f, p => Assert.True(File.Exists(p)));
        using var again = Open(t);
        Assert.True(again.IsEmpty);
    }

    [Fact]
    public void Kinds_line_pluralises()
    {
        Assert.Equal("1 folder", ShelfStore.KindsLine(1, 0));
        Assert.Equal("2 folders, 1 file", ShelfStore.KindsLine(2, 1));
    }
}
