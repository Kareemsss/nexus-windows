using System.IO;
using System.Windows;
using Nexus.Core.Shelf;

namespace Nexus.App.Shelf;

public enum DropKind
{
    /// <summary>Real files or folders (a file drop list with paths).</summary>
    Files,
    /// <summary>Anything else: browser images, mail attachments, text, links. They have no path to keep.</summary>
    Unsupported,
}

/// <summary>What is being dragged over the island, worked out once when the drag enters.</summary>
/// <param name="Files">Files in the drag (0 for the Preview lab, which drags nothing).</param>
/// <param name="WouldEvict">Existing shelf items this drop would push off the end.</param>
public sealed record DropInfo(DropKind Kind, int Files, int Folders, int WouldEvict)
{
    public static DropInfo Preview { get; } = new(DropKind.Files, 0, 0, 0);

    public int Incoming => Files + Folders;

    /// <summary>Paths in a drag, or null when it carries no file drop list.</summary>
    public static string[]? ReadPaths(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
            return data.GetData(DataFormats.FileDrop) as string[];
        }
        catch (Exception) { return null; } // a source process can die or refuse mid-drag
    }

    public static DropInfo Inspect(IDataObject data, ShelfModel model)
    {
        var paths = ReadPaths(data);
        if (paths is null || paths.Length == 0) return new DropInfo(DropKind.Unsupported, 0, 0, 0);
        int folders = paths.Count(Directory.Exists);
        return new DropInfo(DropKind.Files, paths.Length - folders, folders, model.WouldEvict(paths));
    }
}
