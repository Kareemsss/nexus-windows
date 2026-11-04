using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nexus.App.Shelf;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.App.Widgets;
using Nexus.Core.Activities;
using Nexus.Core.Diagnostics;
using Nexus.Core.Shelf;
using Nexus.Platform.Services;

namespace Nexus.App.Center;

/// <summary>
/// Island Center's Shelf section. Each item can be opened (click or Enter), dragged out into any app, shown in
/// its folder, copied as a path or removed. "Drag all" drags every item at once and, clicked, copies them all
/// as files. Three rows are visible; the rest scroll. Every row and button works from the keyboard.
/// </summary>
internal sealed class ShelfCard
{
    // Island Center already holds six sections: three rows keep it within the overlay canvas; the rest scroll.
    private const int VisibleRows = 3;
    private const double RowHeight = 38;

    private readonly Palette _p;
    private readonly WidgetContext _ctx;
    private readonly ShelfProvider _shelf;
    private readonly Action _resized;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroller;
    private readonly FrameworkElement _empty;
    private readonly TextBlock _count, _more, _dragAllLabel;
    private readonly NexusButton _dragAll, _clear;
    private readonly DispatcherTimer _copiedReset = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.6) };
    private readonly Dictionary<string, ShelfRow> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private (int Rows, bool Overflow, bool Visible) _shape = (-1, false, true);
    private bool _hideEmpty;
    private int _existenceCheck;

    public ShelfCard(Palette p, WidgetContext ctx, ShelfProvider shelf, Action resized)
    {
        _p = p;
        _ctx = ctx;
        _shelf = shelf;
        _resized = resized;

        _count = Kit.Text("", 10.5, p.TextSecondary).WithMargin(0, 0, 8, 8);
        _count.VerticalAlignment = VerticalAlignment.Top;
        _dragAllLabel = Kit.Text("Drag all", 10.5, p.TextPrimary, FontWeights.SemiBold);
        _dragAll = HeaderButton(_dragAllLabel, "Drag all into an app, or press to copy every file");
        _dragAll.ToolTip = "Drag into any app, or click to copy every file";
        _dragAll.Click += (_, _) => CopyAll();
        AttachDragSource(_dragAll, () => _shelf.Model.Items.Select(i => i.Path).ToList(), _dragAll);
        _clear = HeaderButton(Kit.Text("Clear", 10.5, p.TextSecondary, FontWeights.SemiBold), "Clear shelf");
        _clear.ToolTip = "Remove every item from the shelf. Your files are not touched.";
        _clear.Click += (_, _) => _shelf.Model.Clear();
        _copiedReset.Tick += (_, _) => { _copiedReset.Stop(); _dragAllLabel.Text = "Drag all"; };

        var header = Kit.Columns(
            (CenterView.Kicker("SHELF", p), Kit.Star),
            (_count, Kit.Auto),
            (_dragAll.WithMargin(0, -4, 4, 0), Kit.Auto),
            (_clear.WithMargin(0, -4, -6, 0), Kit.Auto));

        _scroller = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = VisibleRows * RowHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // scrolls with the wheel and with keyboard focus
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Margin = new Thickness(-6, 0, -6, 0), // rows paint their hover/focus background slightly wider than the text
        };
        _more = Kit.Text("", Kit.Type.Caption, p.TextSecondary).WithMargin(0, 6, 0, 0);

        var emptyIcon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Background = p.ControlFill, Child = Kit.Glyph(Glyphs.Shelf, 16, p.TextSecondary) };
        var emptyTitle = Kit.Text("Shelf is empty", Kit.Type.BodyStrong, p.TextSecondary, FontWeights.SemiBold);
        var emptyHint = Kit.Text("Drag files onto the island to keep them here.", Kit.Type.Caption, p.TextSecondary);
        emptyHint.TextWrapping = TextWrapping.Wrap;
        _empty = Kit.Row(emptyIcon.WithMargin(0, 0, 12, 0), Kit.Column(emptyTitle, emptyHint).WithMaxWidth(320));

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_scroller);
        stack.Children.Add(_more);
        stack.Children.Add(_empty);
        Element = CenterView.Card(p, stack);

        Rebuild(notify: false);
    }

    public FrameworkElement Element { get; }

    public void Start()
    {
        _shelf.Model.Changed -= OnModelChanged;
        _shelf.Model.Changed += OnModelChanged;
        Rebuild(notify: true);
    }

    public void Stop()
    {
        _shelf.Model.Changed -= OnModelChanged;
        _copiedReset.Stop();
    }

    public void Refresh(bool hideEmpty)
    {
        if (_hideEmpty == hideEmpty) return;
        _hideEmpty = hideEmpty;
        ApplyShape(notify: true);
    }

    private void OnModelChanged(object? sender, EventArgs e) => Rebuild(notify: true);

    private NexusButton HeaderButton(UIElement content, string name) =>
        new NexusButton(content, Colors.Transparent, _p.Theme.ControlHover, 11)
        {
            Height = 24,
            Padding = new Thickness(9, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Top,
        }.Accessible(name);

    private void Rebuild(bool notify)
    {
        var items = _shelf.Model.Items;
        bool hadFocus = _rows.IsKeyboardFocusWithin;
        int focusedIndex = hadFocus ? _rows.Children.IndexOf(_rows.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.IsKeyboardFocusWithin)) : -1;

        _rows.Children.Clear();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!_byPath.TryGetValue(item.Path, out var row) || row.Item != item)
                _byPath[item.Path] = row = new ShelfRow(this, item);
            keep.Add(item.Path);
            _rows.Children.Add(row.Element);
        }
        foreach (var gone in _byPath.Keys.Where(k => !keep.Contains(k)).ToList()) _byPath.Remove(gone);

        // Removing the focused row moves focus to its neighbour instead of losing it.
        if (hadFocus && _rows.Children.Count > 0)
        {
            int next = Math.Clamp(focusedIndex, 0, _rows.Children.Count - 1);
            _rows.Children[next].Focus();
        }

        _count.Text = items.Count == 0 ? "" : ShelfModel.Describe(items);
        _dragAll.Visibility = _clear.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _more.Text = items.Count > VisibleRows ? $"Showing {VisibleRows} of {items.Count}. Scroll for more." : "";
        _more.Visibility = items.Count > VisibleRows ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyShape(notify);
        _ = CheckExistenceAsync(items);
    }

    private void ApplyShape(bool notify)
    {
        int count = _shelf.Model.Count;
        bool visible = !(_hideEmpty && count == 0);
        Element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var shape = (Math.Min(count, VisibleRows), count > VisibleRows, visible);
        if (shape == _shape) return;
        _shape = shape;
        if (notify) _resized();
    }

    /// <summary>
    /// Marks items whose file was moved or deleted. Runs off the UI thread: a path on a disconnected network
    /// share can take seconds to answer.
    /// </summary>
    private async Task CheckExistenceAsync(IReadOnlyList<ShelfItem> items)
    {
        int ticket = ++_existenceCheck;
        var snapshot = items.ToList();
        Dictionary<string, bool> exists;
        try
        {
            exists = await Task.Run(() => snapshot.ToDictionary(i => i.Path, i => ShelfProvider.Exists(i.Path), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) { Log.Debug("Shelf", "Existence check failed: " + ex.GetType().Name); return; }
        if (ticket != _existenceCheck) return; // a newer rebuild started meanwhile
        foreach (var (path, ok) in exists)
            if (_byPath.TryGetValue(path, out var row)) row.SetMissing(!ok);
    }

    private void CopyAll()
    {
        var paths = _shelf.Model.Items.Select(i => i.Path).Where(ShelfProvider.Exists).ToList();
        if (paths.Count == 0) return;
        if (!ShellService.CopyFiles(paths)) return;
        _dragAllLabel.Text = paths.Count == 1 ? "Copied 1 file" : $"Copied {paths.Count}";
        _copiedReset.Stop();
        _copiedReset.Start();
    }

    private void Open(ShelfItem item)
    {
        if (!ShelfProvider.Exists(item.Path)) return;
        _ctx.Commands.Collapse();
        ShellService.Open(item.Path);
    }

    #region Drag out

    /// <summary>
    /// Press and move past the system drag distance starts a drag of <paramref name="paths"/>. A plain click
    /// stays a click. Works for a row (its own capture) and for a <see cref="NexusButton"/> (which captures itself).
    /// </summary>
    private void AttachDragSource(FrameworkElement source, Func<IReadOnlyList<string>> paths, NexusButton? button)
    {
        Point? pressedAt = null;
        source.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressedAt = button is null && IsInsideButton(e.OriginalSource as DependencyObject, source) ? null : e.GetPosition(source);
            // A row captures the mouse so a quick flick still reaches the drag threshold (buttons capture themselves).
            if (pressedAt is not null && button is null) source.CaptureMouse();
        };
        source.PreviewMouseLeftButtonUp += (_, _) => pressedAt = null;
        source.PreviewMouseMove += (_, e) =>
        {
            if (pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(source) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressedAt = null;
            button?.CancelPress(); // the drag loop swallows the mouse-up, so the button would stay pressed
            if (source.IsMouseCaptured) source.ReleaseMouseCapture();
            e.Handled = true;
            DragOut(source, paths());
        };
    }

    private void DragOut(DependencyObject source, IReadOnlyList<string> paths)
    {
        var existing = paths.Where(ShelfProvider.Exists).ToArray();
        if (existing.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, existing);
        // Explorer moves by default within one drive. Preview lab samples live in C:\Windows, so they may only be
        // copied: moving them would ask for administrator rights and take a system file away.
        bool samples = _shelf.Model.Items.Any(i => i.IsPreview && existing.Contains(i.Path, StringComparer.OrdinalIgnoreCase));
        var allowed = samples ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
        var effect = DragDropEffects.None;
        _ctx.Commands.DragOutStarted();
        try { effect = DragDrop.DoDragDrop(source, data, allowed); }
        catch (Exception ex) { Log.Warn("Shelf", "Drag out failed: " + ex.GetType().Name); }
        finally { _ctx.Commands.DragOutEnded(); }
        _shelf.AfterDragOut(existing, effect != DragDropEffects.None);
        Log.Info("Shelf", $"Dragged out {existing.Length} item(s): {effect}");
    }

    private static bool IsInsideButton(DependencyObject? d, DependencyObject stop)
    {
        while (d is not null && d != stop)
        {
            if (d is NexusButton) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    #endregion

    /// <summary>[thumbnail] name / folder   [show in folder] [copy path] [remove]</summary>
    private sealed class ShelfRow
    {
        private readonly ShelfCard _card;
        private readonly Border _root;
        private readonly FrameworkElement _icon;
        private readonly TextBlock _name, _where;
        private readonly NexusButton _show, _copy;
        private bool _missing;

        public ShelfRow(ShelfCard card, ShelfItem item)
        {
            _card = card;
            Item = item;
            var p = card._p;

            _icon = ShelfIcon.Create(item, 28, p);
            _name = Kit.Text(MiddleEllipsis(item.Name, 40), Kit.Type.Body, p.TextPrimary);
            _where = Kit.Text(Where(item), Kit.Type.Caption, p.TextSecondary).WithMargin(0, 1, 0, 0);
            _show = RowButton(Glyphs.Folder, "Show in folder", () => ShellService.ShowInFolder(item.Path));
            _copy = RowButton(Glyphs.Copy, "Copy path", () => ShellService.CopyPath(item.Path));
            var remove = RowButton(Glyphs.Close, "Remove from shelf", () => card._shelf.Model.Remove(item.Path));

            var grid = Kit.Columns(
                (_icon.WithMargin(0, 0, 10, 0), Kit.Auto),
                (Kit.Column(_name, _where), Kit.Star),
                (Kit.Row(_show, _copy, remove), Kit.Auto));
            _root = new Border
            {
                Height = RowHeight,
                Padding = new Thickness(6, 0, 2, 0),
                CornerRadius = new CornerRadius(10),
                Background = Brushes.Transparent, // the whole row is a click and drag target
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
                Child = grid,
                Focusable = true,
                FocusVisualStyle = null,
                Cursor = Cursors.Hand,
                ToolTip = item.Path,
            };
            AutomationProperties.SetName(_root, item.Name);
            AutomationProperties.SetHelpText(_root, "Enter opens, Delete removes, Control C copies the file. Drag to move it into another app.");
            KeyboardNavigation.SetIsTabStop(_root, true);

            _root.MouseEnter += (_, _) => _root.Background = p.ControlFill;
            _root.MouseLeave += (_, _) => _root.Background = Brushes.Transparent;
            _root.GotKeyboardFocus += (_, e) => { if (e.NewFocus == _root) _root.BorderBrush = p.TextPrimary; };
            _root.LostKeyboardFocus += (_, _) => _root.BorderBrush = Brushes.Transparent;
            _root.MouseLeftButtonUp += (_, e) =>
            {
                bool captured = _root.IsMouseCaptured;
                if (captured) _root.ReleaseMouseCapture();
                if (e.Handled) return; // a button inside the row handled it
                e.Handled = true;
                var at = e.GetPosition(_root);
                bool inside = at.X >= 0 && at.Y >= 0 && at.X <= _root.ActualWidth && at.Y <= _root.ActualHeight;
                if (inside) card.Open(Item); // released elsewhere: not a click
            };
            _root.MouseRightButtonUp += (_, e) => e.Handled = true; // keep the island's menu away from item rows
            _root.KeyDown += OnKeyDown;
            card.AttachDragSource(_root, () => new[] { Item.Path }, null);
        }

        public ShelfItem Item { get; }
        public FrameworkElement Element => _root;

        public void SetMissing(bool missing)
        {
            if (_missing == missing) return;
            _missing = missing;
            _icon.Opacity = _name.Opacity = missing ? 0.5 : 1;
            _where.Text = missing ? "Moved or deleted" : Where(Item);
            _show.Visibility = _copy.Visibility = missing ? Visibility.Collapsed : Visibility.Visible;
            _root.Cursor = missing ? Cursors.Arrow : Cursors.Hand;
            AutomationProperties.SetItemStatus(_root, missing ? "Moved or deleted" : "");
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource != _root) return; // keys on the row's own buttons belong to them
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            switch (e.Key)
            {
                case Key.Enter:
                    _card.Open(Item);
                    break;
                case Key.Delete:
                case Key.Back:
                    _card._shelf.Model.Remove(Item.Path);
                    break;
                case Key.C when ctrl && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                    ShellService.CopyPath(Item.Path);
                    break;
                case Key.C when ctrl:
                    if (!_missing) ShellService.CopyFiles(new[] { Item.Path });
                    break;
                case Key.Down:
                case Key.Up:
                    var rows = _card._rows.Children;
                    int i = rows.IndexOf(_root) + (e.Key == Key.Down ? 1 : -1);
                    if (i >= 0 && i < rows.Count) rows[i].Focus();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        private NexusButton RowButton(string glyph, string name, Action onClick)
        {
            var p = _card._p;
            var b = new NexusButton(Kit.Glyph(glyph, 12, p.TextSecondary), Colors.Transparent, p.Theme.ControlHover, 14)
            {
                Width = 28,
                Height = 28,
                ToolTip = name,
            }.Accessible(name);
            b.Click += (_, _) => onClick();
            return b;
        }

        private static string Where(ShelfItem item)
        {
            var folder = item.Folder;
            if (string.IsNullOrEmpty(folder)) return item.IsFolder ? "Drive" : "";
            var name = Path.GetFileName(folder.TrimEnd('\\'));
            return "in " + (string.IsNullOrEmpty(name) ? folder : name);
        }

        /// <summary>"Quarterly-infrastructure-rev…-final.pdf": long names keep their end, so the extension stays visible.</summary>
        internal static string MiddleEllipsis(string name, int max)
        {
            if (name.Length <= max) return name;
            int tail = Math.Min(14, max / 3);
            return name[..(max - tail - 1)] + "…" + name[^tail..];
        }
    }
}
