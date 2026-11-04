using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Plugins;
using Nexus.Platform.Services;

namespace Nexus.Platform.Providers;

/// <summary>
/// Browser download activity in the user's Downloads folder, detected from the temporary files browsers use
/// (.crdownload, .part, .partial, .download) via FileSystemWatcher. In-progress downloads appear as an
/// indeterminate satellite capsule (browsers do not expose the total size to other apps); completion shows a
/// card with Open / Show in folder.
/// </summary>
public sealed class DownloadsProvider : ProviderBase
{
    private static readonly string[] TempExtensions = { ".crdownload", ".part", ".partial", ".download", ".opdownload" };
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;

    public override string Id => "downloads";
    public override string DisplayName => "Downloads";
    public override string Description => "Browser downloads in progress and completed, from your Downloads folder.";

    protected override Task OnStartAsync(CancellationToken ct)
    {
        var folder = KnownFolders.Downloads;
        if (folder is null)
        {
            SetStatus(new ProviderStatus(ProviderState.Unavailable, "Downloads folder not found."));
            return Task.CompletedTask;
        }
        _watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName,
            IncludeSubdirectories = false,
            InternalBufferSize = 16 * 1024,
        };
        _watcher.Created += (_, e) => Guard("created", () => OnCreated(e.FullPath));
        _watcher.Deleted += (_, e) => Guard("deleted", () => OnGone(e.FullPath));
        _watcher.Renamed += (_, e) => Guard("renamed", () => OnRenamed(e.OldFullPath, e.FullPath));
        _watcher.Error += (_, e) => Guard("watcher error", () => { lock (_active) _active.Clear(); Publish(); });
        _watcher.EnableRaisingEvents = true;
        return Task.CompletedTask;
    }

    protected override ValueTask OnStopAsync()
    {
        _watcher?.Dispose();
        _watcher = null;
        return ValueTask.CompletedTask;
    }

    private static bool IsTemp(string path) => TempExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    private void OnCreated(string path)
    {
        if (!IsTemp(path)) return;
        lock (_active) _active.Add(path);
        Publish();
    }

    private void OnGone(string path)
    {
        bool removed;
        lock (_active) removed = _active.Remove(path);
        if (removed) Publish();
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        // Chromium renames "Unconfirmed 1234.crdownload" → "file.zip.crdownload" → "file.zip".
        if (IsTemp(newPath))
        {
            lock (_active) { _active.Remove(oldPath); _active.Add(newPath); }
            Publish();
            return;
        }
        bool wasDownload;
        lock (_active) wasDownload = _active.Remove(oldPath) || IsTemp(oldPath);
        if (!wasDownload) return;
        Publish();
        Completed(newPath);
    }

    private void Completed(string path)
    {
        var name = Path.GetFileName(path);
        Pulse(new PulseSignal(SemanticColors.Transfer));
        Post(new Activity
        {
            Id = "download.done." + name,
            SourceId = Id,
            Priority = ActivityPriority.Medium,
            Category = ActivityCategory.Transfer,
            Duration = TimeSpan.FromSeconds(4.5),
            Content = new GenericContent(Glyphs.Download, "Download complete", name, 1.0, false, SemanticColors.Transfer),
            Accent = SemanticColors.Transfer,
            Actions = new[]
            {
                new ActivityAction("open", "Open", Glyphs.Open, () => { ShellService.Open(path); return Task.CompletedTask; }, IsPrimary: true),
                new ActivityAction("folder", "Show in folder", Glyphs.Folder, () => { ShellService.ShowInFolder(path); return Task.CompletedTask; }),
            },
        });
    }

    private void Publish()
    {
        int count;
        string? first;
        lock (_active)
        {
            _active.RemoveWhere(p => !File.Exists(p)); // self-heal if an event was missed
            count = _active.Count;
            first = _active.Select(p => Path.GetFileName(p)).FirstOrDefault();
        }
        if (count == 0) { Remove("downloads.active"); return; }
        var label = count == 1 ? $"Downloading {CleanName(first!)}" : $"Downloading {count} files";
        Post(new Activity
        {
            Id = "downloads.active",
            SourceId = Id,
            Priority = ActivityPriority.Low,
            Category = ActivityCategory.Transfer,
            CanBeForeground = false,
            Content = new GenericContent(Glyphs.Download, label, null, null, true, SemanticColors.Transfer),
            Indicator = new IndicatorSpec(Glyphs.Download, SemanticColors.Transfer, label, null, Indeterminate: true),
        });
    }

    private static string CleanName(string fileName)
    {
        foreach (var x in TempExtensions)
            if (fileName.EndsWith(x, StringComparison.OrdinalIgnoreCase)) return fileName[..^x.Length];
        return fileName;
    }
}
