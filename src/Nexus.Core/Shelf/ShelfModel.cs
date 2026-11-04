using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Core.Shelf;

/// <summary>One file or folder parked on the shelf. Only the path is kept; the file itself is never copied.</summary>
/// <param name="IsPreview">Sample item from the Preview lab. Shown like any other item, never saved to disk.</param>
public sealed record ShelfItem(string Path, bool IsFolder, DateTimeOffset AddedAt, bool IsPreview = false)
{
    /// <summary>File or folder name; the whole path for a drive root ("D:\").</summary>
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd('\\', '/');
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }

    /// <summary>Containing folder, or null for a drive root.</summary>
    public string? Folder => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\', '/'));
}

/// <summary>A path offered to the shelf, with whether it is a folder (decided by the caller, who can touch the disk).</summary>
public readonly record struct ShelfCandidate(string Path, bool IsFolder);

/// <param name="Added">Paths that were not on the shelf before.</param>
/// <param name="MovedToTop">Paths that were already on the shelf and moved back to the top.</param>
/// <param name="Evicted">Oldest items removed to stay within the capacity.</param>
public readonly record struct ShelfAddResult(int Added, int MovedToTop, int Evicted)
{
    public int Total => Added + MovedToTop;
}

/// <summary>
/// The shelf's contents: newest first, no duplicate paths (compared case-insensitively, as Windows does), and at
/// most <see cref="Capacity"/> items. Pure and deterministic: time is passed in and nothing here touches the disk.
/// </summary>
public sealed class ShelfModel
{
    public const int DefaultCapacity = 24;

    private readonly List<ShelfItem> _items = new();

    public ShelfModel(int capacity = DefaultCapacity) => Capacity = Math.Max(1, capacity);

    public int Capacity { get; }
    public IReadOnlyList<ShelfItem> Items => _items;
    public int Count => _items.Count;

    /// <summary>Raised after every change to <see cref="Items"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Normalised identity of a path: trailing separators removed, case ignored by the comparer.</summary>
    public static string KeyOf(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        // Keep "C:\" intact; strip the trailing separator from everything else.
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }

    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public bool Contains(string path)
    {
        var key = KeyOf(path);
        return _items.Any(i => Comparer.Equals(KeyOf(i.Path), key));
    }

    /// <summary>
    /// Adds dropped paths in the order they were dropped, as a group at the top of the shelf. A path already on
    /// the shelf moves to the top instead of appearing twice. When the shelf overflows, the oldest items go.
    /// </summary>
    public ShelfAddResult Add(IEnumerable<ShelfCandidate> candidates, DateTimeOffset now, bool preview = false)
    {
        var incoming = new List<ShelfCandidate>();
        var seen = new HashSet<string>(Comparer);
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.Path)) continue;
            if (seen.Add(KeyOf(c.Path))) incoming.Add(c with { Path = KeyOf(c.Path) });
        }
        if (incoming.Count == 0) return default;

        int added = 0, moved = 0;
        foreach (var c in incoming)
        {
            int existing = _items.FindIndex(i => Comparer.Equals(KeyOf(i.Path), c.Path));
            if (existing >= 0) { _items.RemoveAt(existing); moved++; }
            else added++;
        }
        // Insert so the first dropped path ends up first.
        for (int i = incoming.Count - 1; i >= 0; i--)
            _items.Insert(0, new ShelfItem(incoming[i].Path, incoming[i].IsFolder, now, preview));

        int evicted = 0;
        while (_items.Count > Capacity)
        {
            _items.RemoveAt(_items.Count - 1);
            evicted++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return new ShelfAddResult(added, moved, evicted);
    }

    /// <summary>How many existing items a drop of these paths would push off the shelf.</summary>
    public int WouldEvict(IEnumerable<string> paths)
    {
        var keys = new HashSet<string>(paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(KeyOf), Comparer);
        int newOnes = keys.Count(k => !_items.Any(i => Comparer.Equals(KeyOf(i.Path), k)));
        return Math.Max(0, _items.Count + newOnes - Capacity);
    }

    public bool Remove(string path)
    {
        var key = KeyOf(path);
        int removed = _items.RemoveAll(i => Comparer.Equals(KeyOf(i.Path), key));
        if (removed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return removed > 0;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the contents with saved items (newest first), dropping duplicates and overflow.</summary>
    public void Load(IEnumerable<ShelfItem> items)
    {
        _items.Clear();
        var seen = new HashSet<string>(Comparer);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Path)) continue;
            var key = KeyOf(item.Path);
            if (!seen.Add(key)) continue;
            _items.Add(item with { Path = key });
            if (_items.Count == Capacity) break;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"1 file", "3 files", "2 folders", "4 items" (files and folders mixed).</summary>
    public static string Describe(int files, int folders)
    {
        int total = files + folders;
        if (total == 0) return "Empty";
        if (folders == 0) return files == 1 ? "1 file" : $"{files} files";
        if (files == 0) return folders == 1 ? "1 folder" : $"{folders} folders";
        return $"{total} items";
    }

    public static string Describe(IEnumerable<ShelfItem> items)
    {
        int files = 0, folders = 0;
        foreach (var i in items) { if (i.IsFolder) folders++; else files++; }
        return Describe(files, folders);
    }
}
