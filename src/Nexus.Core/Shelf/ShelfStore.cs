using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Core.Diagnostics;

namespace Nexus.Core.Shelf;

/// <summary>
/// Saves the shelf as a small JSON file (paths and the time each was added, nothing else). Writes are atomic, a
/// corrupt file is backed up as <c>shelf.json.corrupt</c> and the shelf starts empty. File names and paths are
/// never written to the log.
/// </summary>
public sealed class ShelfStore
{
    private sealed record Entry(string Path, bool IsFolder, DateTimeOffset AddedAt);
    private sealed record Document(int Version, List<Entry> Items);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public ShelfStore(string path) => FilePath = path;

    public string FilePath { get; }

    public IReadOnlyList<ShelfItem> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<ShelfItem>();
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), Json);
            if (doc?.Items is null) throw new JsonException("Shelf file has no item list.");
            var items = doc.Items
                .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path))
                .Select(e => new ShelfItem(e.Path, e.IsFolder, e.AddedAt))
                .ToList();
            Log.Info("Shelf", $"Loaded {items.Count} item(s)");
            return items;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            Log.Warn("Shelf", "Shelf file was unreadable; starting with an empty shelf (a backup was kept).");
            try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); } catch { /* best effort */ }
            return Array.Empty<ShelfItem>();
        }
        catch (Exception ex)
        {
            Log.Warn("Shelf", "Shelf file could not be read: " + ex.GetType().Name);
            return Array.Empty<ShelfItem>();
        }
    }

    /// <summary>Writes every item except Preview lab samples. Returns false when the write failed.</summary>
    public bool Save(IEnumerable<ShelfItem> items)
    {
        try
        {
            var doc = new Document(1, items.Where(i => !i.IsPreview).Select(i => new Entry(i.Path, i.IsFolder, i.AddedAt)).ToList());
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
            File.Move(tmp, FilePath, overwrite: true); // atomic replace on NTFS: never a half-written shelf
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Shelf", "Saving the shelf failed: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Removes the saved shelf ("Remember shelf after restart" turned off).</summary>
    public void Delete()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            if (File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp");
        }
        catch (Exception ex) { Log.Warn("Shelf", "Deleting the saved shelf failed: " + ex.GetType().Name); }
    }
}
