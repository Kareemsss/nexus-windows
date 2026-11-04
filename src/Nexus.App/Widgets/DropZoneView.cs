using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nexus.App.Shelf;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Shelf;

namespace Nexus.App.Widgets;

/// <summary>
/// The island while files are dragged over it: one large target that says "Drop to keep here", what the drop
/// will add, and the items already on the shelf. When the drag carries no real files (a browser image, a mail
/// attachment) it explains why it can't take them instead. Same glass, same type ramp as every other view.
/// </summary>
public sealed class DropZoneView : IWidgetView
{
    public const double Width = 392;
    private const int MaxIcons = 8;
    private const double IconSize = 26;

    private readonly Grid _root;

    public DropZoneView(WidgetContext ctx, DropInfo info, IReadOnlyList<ShelfItem> items)
    {
        var p = ctx.Palette;
        bool files = info.Kind == DropKind.Files;

        var badge = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(12),
            Background = p.ControlFill,
            Child = Kit.Glyph(files ? Glyphs.Shelf : Glyphs.Info, 18, files ? p.Accent : p.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
        };

        string title = files ? "Drop to keep here" : "Only files and folders can be shelved";
        var titleText = Kit.Text(title, Kit.Type.Title, p.TextPrimary, FontWeights.SemiBold);
        var detailText = Kit.Text(Detail(info, items), Kit.Type.Caption, p.TextSecondary).WithMargin(0, 3, 0, 0);
        var header = Kit.Columns((badge.WithMargin(0, 0, 14, 0), Kit.Auto), (Kit.Column(titleText, detailText), Kit.Star));

        _root = new Grid { Width = Width, Margin = new Thickness(Layout.ExpandedPad, 14, Layout.ExpandedPad, 14) };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(header);

        if (files)
        {
            var strip = Strip(items, p);
            strip.Margin = new Thickness(54, 12, 0, 0); // aligned with the title, under the badge's column
            Grid.SetRow(strip, 1);
            _root.Children.Add(strip);
        }
    }

    public FrameworkElement Element => _root;

    private static string Detail(DropInfo info, IReadOnlyList<ShelfItem> items)
    {
        if (info.Kind == DropKind.Unsupported)
            return "Save browser images and attachments as files first.";
        if (info.Incoming == 0)
            return items.Count == 0 ? "Your shelf is empty" : ShelfModel.Describe(items) + " on the shelf";
        var adding = "Adding " + ShelfModel.Describe(info.Files, info.Folders);
        return info.WouldEvict > 0 ? $"{adding} · Shelf full: oldest {info.WouldEvict} removed" : adding;
    }

    private static FrameworkElement Strip(IReadOnlyList<ShelfItem> items, Palette p)
    {
        if (items.Count == 0)
            return Kit.Text("Files stay here until you drag them out", Kit.Type.Caption, p.TextSecondary);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = IconSize };
        foreach (var item in items.Take(MaxIcons))
        {
            var icon = ShelfIcon.Create(item, IconSize, p);
            icon.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(icon);
        }
        if (items.Count > MaxIcons)
            row.Children.Add(Kit.Numeric($"+{items.Count - MaxIcons}", Kit.Type.Caption, p.TextSecondary, FontWeights.SemiBold));
        return row;
    }

    public void Update(TrackedActivity? activity) { }
    public void OnShown() { }
    public void OnHidden() { }
}
