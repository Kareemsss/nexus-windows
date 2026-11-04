using System;
using System.IO;
using System.Linq;
using Nexus.Core.Settings;
using Nexus.Core.Shelf;
using Xunit;

namespace Nexus.Core.Tests;

public class ShelfModelTests
{
    private readonly DateTimeOffset _t0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static ShelfCandidate F(string path) => new(path, false);
    private static ShelfCandidate D(string path) => new(path, true);

    [Fact]
    public void Adds_newest_first_keeping_drop_order_within_a_group()
    {
        var m = new ShelfModel();
        m.Add(new[] { F(@"C:\a.txt") }, _t0);
        var r = m.Add(new[] { F(@"C:\b.txt"), F(@"C:\c.txt") }, _t0.AddMinutes(1));

        Assert.Equal(new ShelfAddResult(2, 0, 0), r);
        Assert.Equal(new[] { @"C:\b.txt", @"C:\c.txt", @"C:\a.txt" }, m.Items.Select(i => i.Path));
    }

    [Fact]
    public void Duplicate_path_moves_to_top_instead_of_appearing_twice()
    {
        var m = new ShelfModel();
        m.Add(new[] { F(@"C:\a.txt"), F(@"C:\b.txt") }, _t0);
        var r = m.Add(new[] { F(@"c:\A.TXT") }, _t0.AddMinutes(1)); // Windows paths are case-insensitive

        Assert.Equal(new ShelfAddResult(0, 1, 0), r);
        Assert.Equal(2, m.Count);
        Assert.Equal(@"c:\A.TXT", m.Items[0].Path);
        Assert.Equal(_t0.AddMinutes(1), m.Items[0].AddedAt);
    }

    [Fact]
    public void Duplicates_inside_one_drop_count_once()
    {
        var m = new ShelfModel();
        var r = m.Add(new[] { F(@"C:\a.txt"), F(@"C:\A.txt"), F("  "), F(@"C:\a.txt\") }, _t0);
        Assert.Equal(1, r.Added);
        Assert.Single(m.Items);
    }

    [Fact]
    public void Capacity_evicts_the_oldest_and_reports_it()
    {
        var m = new ShelfModel(capacity: 3);
        m.Add(new[] { F(@"C:\1"), F(@"C:\2"), F(@"C:\3") }, _t0);
        Assert.Equal(1, m.WouldEvict(new[] { @"C:\4", @"C:\1" })); // C:\1 is already there, only C:\4 is new
        var r = m.Add(new[] { F(@"C:\4") }, _t0.AddMinutes(1));

        Assert.Equal(1, r.Evicted);
        Assert.Equal(new[] { @"C:\4", @"C:\1", @"C:\2" }, m.Items.Select(i => i.Path));
    }

    [Fact]
    public void Remove_and_clear_raise_changed()
    {
        var m = new ShelfModel();
        m.Add(new[] { F(@"C:\a.txt"), D(@"C:\folder") }, _t0);
        int changes = 0;
        m.Changed += (_, _) => changes++;

        Assert.True(m.Remove(@"C:\FOLDER\"));
        Assert.False(m.Remove(@"C:\missing"));
        m.Clear();
        m.Clear(); // already empty: no event

        Assert.Empty(m.Items);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Folder_flag_name_and_parent_are_kept()
    {
        var m = new ShelfModel();
        m.Add(new[] { D(@"C:\Users\me\Projects\"), F(@"D:\") }, _t0);
        var folder = m.Items[0];
        Assert.True(folder.IsFolder);
        Assert.Equal(@"C:\Users\me\Projects", folder.Path);
        Assert.Equal("Projects", folder.Name);
        Assert.Equal(@"C:\Users\me", folder.Folder);
        Assert.Equal(@"D:\", m.Items[1].Name); // a drive root has no file name: show the root itself
    }

    [Theory]
    [InlineData(1, 0, "1 file")]
    [InlineData(3, 0, "3 files")]
    [InlineData(0, 1, "1 folder")]
    [InlineData(0, 2, "2 folders")]
    [InlineData(2, 2, "4 items")]
    [InlineData(0, 0, "Empty")]
    public void Describe_counts_files_and_folders(int files, int folders, string expected) =>
        Assert.Equal(expected, ShelfModel.Describe(files, folders));

    [Fact]
    public void Load_drops_duplicates_and_overflow()
    {
        var m = new ShelfModel(capacity: 2);
        m.Load(new[]
        {
            new ShelfItem(@"C:\a", false, _t0), new ShelfItem(@"c:\A", false, _t0),
            new ShelfItem(@"C:\b", false, _t0), new ShelfItem(@"C:\c", false, _t0),
        });
        Assert.Equal(new[] { @"C:\a", @"C:\b" }, m.Items.Select(i => i.Path));
    }
}

public class ShelfStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-shelf-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _t0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private string FilePath => Path.Combine(_dir, "shelf.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Round_trip_keeps_order_and_skips_preview_items()
    {
        var store = new ShelfStore(FilePath);
        var items = new[]
        {
            new ShelfItem(@"C:\b.txt", false, _t0.AddMinutes(1)),
            new ShelfItem(@"C:\Windows\win.ini", false, _t0, IsPreview: true),
            new ShelfItem(@"C:\folder", true, _t0),
        };
        Assert.True(store.Save(items));

        var loaded = new ShelfStore(FilePath).Load();
        Assert.Equal(new[] { @"C:\b.txt", @"C:\folder" }, loaded.Select(i => i.Path));
        Assert.True(loaded[1].IsFolder);
        Assert.Equal(_t0.AddMinutes(1), loaded[0].AddedAt);
        Assert.False(File.Exists(FilePath + ".tmp")); // atomic save leaves no partial file behind
    }

    [Fact]
    public void Missing_file_loads_empty()
    {
        Assert.Empty(new ShelfStore(FilePath).Load());
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_loads_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not json");

        Assert.Empty(new ShelfStore(FilePath).Load());
        Assert.True(File.Exists(FilePath + ".corrupt"));
        Assert.Equal("{ this is not json", File.ReadAllText(FilePath + ".corrupt"));
    }

    [Fact]
    public void Delete_removes_the_saved_shelf()
    {
        var store = new ShelfStore(FilePath);
        store.Save(new[] { new ShelfItem(@"C:\a", false, _t0) });
        store.Delete();
        Assert.False(File.Exists(FilePath));
        Assert.Empty(store.Load());
    }
}

public class ShelfSettingsTests
{
    [Fact]
    public void Old_center_layout_gets_the_shelf_right_after_now_playing()
    {
        var c = new CenterSettings
        {
            Sections = new()
            {
                new CenterSectionItem { Section = CenterSection.Privacy },
                new CenterSectionItem { Section = CenterSection.NowPlaying },
                new CenterSectionItem { Section = CenterSection.Mixer },
                new CenterSectionItem { Section = CenterSection.Meeting },
                new CenterSectionItem { Section = CenterSection.SystemStatus },
                new CenterSectionItem { Section = CenterSection.QuickActions },
            },
        };
        c.Normalize();
        var order = c.Sections.Select(s => s.Section).ToList();
        Assert.Equal(order.IndexOf(CenterSection.NowPlaying) + 1, order.IndexOf(CenterSection.Shelf));
        Assert.True(c.IsVisible(CenterSection.Shelf));
    }

    [Fact]
    public void Settings_without_a_shelf_section_load_with_defaults()
    {
        var json = """{ "SchemaVersion": 1, "Behavior": { "QuietMode": true } }""";
        var s = System.Text.Json.JsonSerializer.Deserialize<NexusSettings>(json, NexusSettings.JsonOptions)!;
        s.Shelf = null!; // as if an older writer emitted "Shelf": null
        s.Normalize();
        Assert.True(s.Shelf.Remember);
        Assert.False(s.Shelf.KeepAfterDragOut);
    }
}
