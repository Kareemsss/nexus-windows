using System.Windows;
using System.Windows.Controls;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Content;

namespace Nexus.App.Widgets;

public sealed class VolumeWidget : Widget<VolumeContent>
{
    protected override IWidgetView CreateCompact(TrackedActivity a, WidgetContext ctx) => new View(ctx, a, expanded: false);
    protected override IWidgetView CreateExpanded(TrackedActivity a, WidgetContext ctx) => new View(ctx, a, expanded: true);

    internal static string GlyphFor(VolumeContent v) =>
        v.Muted || v.Level <= 0.001f ? Glyphs.Mute
        : v.DeviceKind is AudioDeviceKind.Headphones or AudioDeviceKind.Headset or AudioDeviceKind.Bluetooth ? Glyphs.Headphones
        : Glyphs.VolumeFor(v.Level, false);

    internal static string KindLabel(AudioDeviceKind k) => k switch
    {
        AudioDeviceKind.Bluetooth => "Bluetooth audio",
        AudioDeviceKind.Headphones => "Headphones",
        AudioDeviceKind.Headset => "Headset",
        AudioDeviceKind.Speakers => "Speakers",
        AudioDeviceKind.Display => "Display audio",
        _ => "Audio output",
    };

    /// <summary>Compact: [icon] ━━━━━━━━──── 64%   Expanded: device header + draggable slider + mute.</summary>
    private sealed class View : WidgetView<VolumeContent>
    {
        private readonly FrameworkElement _root;
        private readonly TextBlock _glyph, _percent;
        private readonly TextBlock? _device, _kind;
        private readonly ProgressTrack _track;
        private VolumeContent _v;

        public View(WidgetContext ctx, TrackedActivity a, bool expanded) : base(ctx, a)
        {
            _v = (VolumeContent)a.Activity.Content;
            _glyph = Kit.Glyph(Glyphs.Volume3, 15, P.TextPrimary);
            _glyph.Width = 20;
            _percent = Kit.Numeric("0", Kit.Type.BodyStrong, P.TextPrimary, FontWeights.SemiBold);
            _percent.Width = 34;
            _percent.TextAlignment = TextAlignment.Right;
            _track = new ProgressTrack(interactive: expanded) { TrackBrush = P.Track, FillBrush = P.TextPrimary, Thickness = expanded ? 5 : 4 };
            _track.ValueChanged += (v, _) => _v.Controls.SetLevel((float)v);

            if (!expanded)
            {
                var g = Kit.Columns(
                    (_glyph.WithMargin(Layout.CompactPadX - 2, 0, 10, 0), Kit.Auto),
                    (_track.WithWidth(150), Kit.Star),
                    (_percent.WithMargin(8, 0, Layout.CompactPadX, 0), Kit.Auto));
                g.Height = Layout.CompactHeight;
                _root = g;
            }
            else
            {
                _device = Kit.Text("", Kit.Type.BodyStrong, P.TextPrimary, FontWeights.SemiBold).WithMaxWidth(240);
                _kind = Kit.Text("", Kit.Type.Caption, P.TextTertiary);
                var header = Kit.Row(Kit.Glyph(Glyphs.Speakers, 14, P.TextSecondary).WithMargin(0, 0, 10, 0), Kit.Column(_device, _kind));
                var mute = new NexusButton(_glyph, P.Theme.ControlFill, P.Theme.ControlHover, 17) { Width = 34, Height = 34 };
                mute.Click += (_, _) => _v.Controls.SetMuted(!_v.Muted);
                var slider = Kit.Columns((mute, Kit.Auto), (_track.WithMargin(12, 0, 4, 0), Kit.Star), (_percent.WithMargin(6, 0, 0, 0), Kit.Auto));
                var root = new StackPanel { Width = 320, Margin = new Thickness(Layout.ExpandedPad, 14, Layout.ExpandedPad, 14) };
                root.Children.Add(header);
                root.Children.Add(slider.WithMargin(0, 12, 0, 0));
                _root = root;
            }
            Apply(_v);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(VolumeContent v)
        {
            _v = v;
            _glyph.Text = GlyphFor(v);
            int pct = (int)Math.Round(v.Level * 100);
            _percent.Text = v.Muted ? "Off" : pct.ToString();
            _track.SetProgress(v.Muted ? 0 : v.Level);
            _track.FillBrush = v.Muted ? P.TextTertiary : P.TextPrimary;
            if (_device is not null) _device.Text = v.DeviceName;
            if (_kind is not null) _kind.Text = KindLabel(v.DeviceKind);
        }
    }
}
