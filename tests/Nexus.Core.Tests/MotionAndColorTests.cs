using System;
using Nexus.Core.Color;
using Nexus.Core.Motion;
using Xunit;

namespace Nexus.Core.Tests;

public class MotionAndColorTests
{
    [Fact]
    public void Morph_spring_overshoots_slightly_then_settles_on_target()
    {
        var s = new SpringState(0) { Target = 100 };
        double max = 0, t = 0;
        while (!s.Step(1 / 60.0, SpringConfig.Morph, 0.01) && t < 5) { t += 1 / 60.0; max = Math.Max(max, s.Value); }
        Assert.Equal(100, s.Value);
        Assert.InRange(max, 100.5, 115); // controlled overshoot, not a wobble
        Assert.InRange(t, 0.2, 1.5);     // settles quickly
    }

    [Fact]
    public void Critically_damped_spring_never_overshoots()
    {
        var s = new SpringState(0) { Target = 1 };
        var cfg = new SpringConfig(300, 1.0);
        for (int i = 0; i < 300; i++) { s.Step(1 / 60.0, cfg, 0.0001); Assert.True(s.Value <= 1.002); }
    }

    [Fact]
    public void Huge_frame_gaps_do_not_explode()
    {
        var s = new SpringState(0) { Target = 1 };
        s.Step(10, SpringConfig.Morph, 0.001);
        Assert.InRange(s.Value, -1, 3);
    }

    [Fact]
    public void Speed_scales_stiffness_quadratically()
    {
        Assert.Equal(SpringConfig.Morph.Stiffness * 4, SpringConfig.Morph.WithSpeed(2).Stiffness, 6);
    }

    [Fact]
    public void Hsv_round_trip()
    {
        var c = NexusColor.FromHex("#3D7BE0");
        var (h, s, v) = c.ToHsv();
        var back = NexusColor.FromHsv(h, s, v);
        Assert.InRange(Math.Abs(back.R - c.R) + Math.Abs(back.G - c.G) + Math.Abs(back.B - c.B), 0, 3);
    }

    [Fact]
    public void Dominant_colour_of_a_red_cover_is_red_and_tamed()
    {
        const int w = 8, h = 8;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bool red = i % 4 != 0; // 75% saturated red, 25% near-black
            px[i * 4 + 0] = (byte)(red ? 30 : 10);
            px[i * 4 + 1] = (byte)(red ? 25 : 10);
            px[i * 4 + 2] = (byte)(red ? 220 : 12);
            px[i * 4 + 3] = 255;
        }
        var dominant = ColorExtractor.ExtractDominant(px, w, h, w * 4);
        Assert.NotNull(dominant);
        var (hue, _, _) = dominant!.Value.ToHsv();
        Assert.True(hue < 15 || hue > 345);
        var (_, sat, val) = dominant.Value.Ambientize().ToHsv();
        Assert.True(sat <= 0.63);
        Assert.InRange(val, 0.71, 0.95);
    }

    [Fact]
    public void Greyscale_artwork_has_no_dominant_colour()
    {
        var px = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) { px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = (byte)(i * 15); px[i * 4 + 3] = 255; }
        Assert.Null(ColorExtractor.ExtractDominant(px, 4, 4, 16));
    }
}
