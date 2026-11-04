using System.IO;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Plugins;
using Nexus.Core.Settings;
using Nexus.Core.Shelf;
using Nexus.Platform.Services;

namespace Nexus.App.Shelf;

/// <summary>
/// The File Shelf module. Owns the shelf's contents and persistence, publishes the satellite indicator
/// ("Shelf · 3 files"), and appears in Settings › Modules so the whole feature can be switched off. While it is
/// off the island does not accept drops and Island Center hides the Shelf section.
/// </summary>
public sealed class ShelfProvider : ProviderBase
{
    public const string ProviderId = "shelf";
    public const string IndicatorId = "shelf.items";

    private readonly ShelfStore _store;
    private readonly SettingsStore _settings;

    public ShelfProvider(ShelfModel model, ShelfStore store, SettingsStore settings)
    {
        Model = model;
        _store = store;
        _settings = settings;
        // Persistence does not depend on the module running: "Clear shelf" in Settings works while it is off.
        Model.Changed += (_, _) => { Persist(); Publish(); };
    }

    public override string Id => ProviderId;
    public override string DisplayName => "File Shelf";
    public override string Description => "Drag files onto the island to keep them there, then drag them out into any app.";

    public ShelfModel Model { get; }
    public bool IsActive => Status.State == ProviderState.Running;

    protected override Task OnStartAsync(CancellationToken ct)
    {
        // Mark running first so the indicator is published by this start, not by the next change.
        SetStatus(new ProviderStatus(ProviderState.Running));
        Publish();
        return Task.CompletedTask;
    }

    /// <summary>Writes the shelf to disk when "Remember shelf after restart" is on.</summary>
    public void Persist()
    {
        if (_settings.Current.Shelf.Remember) _store.Save(Model.Items);
    }

    /// <summary>"Remember shelf after restart" turned off: the saved copy is deleted, the shelf stays in memory.</summary>
    public void ForgetSaved() => _store.Delete();

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public ShelfAddResult AddPaths(IEnumerable<string> paths, DateTimeOffset now, bool preview = false) =>
        Model.Add(paths.Select(p => new ShelfCandidate(p, Directory.Exists(p))), now, preview);

    /// <summary>
    /// Applies the result of dragging items out. Explorer's "optimized move" reports no effect at all, so the
    /// disk is the judge: an item whose file is gone was moved and always leaves; an item the target accepted
    /// leaves unless the user keeps items after dragging them out; a cancelled drag keeps everything.
    /// </summary>
    public void AfterDragOut(IReadOnlyList<string> paths, bool accepted)
    {
        bool keep = _settings.Current.Shelf.KeepAfterDragOut;
        foreach (var path in paths)
        {
            if (!Exists(path)) Model.Remove(path);
            else if (accepted && !keep) Model.Remove(path);
        }
    }

    /// <summary>Preview lab: paths that exist on every Windows PC, marked so they are never saved.</summary>
    public ShelfAddResult AddSamples(DateTimeOffset now)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Path.Combine(windows, "Web", "Wallpaper", "Windows", "img0.jpg"),
            Path.Combine(windows, "win.ini"),
            KnownFolders.Downloads ?? "",
        };
        return AddPaths(candidates.Where(p => p.Length > 0 && Exists(p)), now, preview: true);
    }

    private void Publish()
    {
        if (!IsActive) return;
        if (Model.Count == 0) { Remove(IndicatorId); return; }
        var label = ShelfModel.Describe(Model.Items);
        Post(new Activity
        {
            Id = IndicatorId,
            SourceId = Id,
            Priority = ActivityPriority.Low,
            Category = ActivityCategory.System,
            CanBeForeground = false, // only a satellite indicator; the items live in Island Center
            Content = new GenericContent(Glyphs.Shelf, "File Shelf", label),
            Indicator = new IndicatorSpec(Glyphs.Shelf, SemanticColors.Neutral, "Shelf · " + label),
        });
    }
}
