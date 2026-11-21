// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2025-2026 Baba0bab — GoveeScreen

using System.Text.RegularExpressions;
using GoveeScreenV2.Core;
using Vortice.D3DCompiler;
using Xunit;

namespace GoveeScreenV2.Tests;

public class FrameAnalyzerTests
{
    /// <summary>BGRA thumbnail filled with one color.</summary>
    private static byte[] Fill(int width, int height, byte r, byte g, byte b)
    {
        var bgra = new byte[width * height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }
        return bgra;
    }

    private static void PaintRows(byte[] bgra, int width, int fromRow, int toRow, byte r, byte g, byte b)
    {
        for (int y = fromRow; y < toRow; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bgra[i] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
            }
    }

    [Fact]
    public void UniformFrameGivesItsColor()
    {
        var color = FrameAnalyzer.Analyze(Fill(160, 90, 200, 100, 50), 160, 90);

        Assert.Equal((byte)200, color.R);
        Assert.Equal((byte)100, color.G);
        Assert.Equal((byte)50, color.B);
    }

    [Fact]
    public void BlackFrameIsBlackWithZeroBrightness()
    {
        var color = FrameAnalyzer.Analyze(Fill(160, 90, 0, 0, 0), 160, 90);

        Assert.Equal(new FrameColor(0, 0, 0, 0), color);
    }

    [Fact]
    public void LetterboxBarsAreIgnored()
    {
        // 16:9 thumbnail showing a 2.39:1 film: black bars on top and bottom
        var bgra = Fill(160, 90, 0, 0, 0);
        PaintRows(bgra, 160, 12, 78, 0, 120, 240);

        var color = FrameAnalyzer.Analyze(bgra, 160, 90);

        Assert.Equal((byte)0, color.R);
        Assert.Equal((byte)120, color.G);
        Assert.Equal((byte)240, color.B);
    }

    [Fact]
    public void DarkSceneIsNotMistakenForBars()
    {
        // Bars are at most a third of the height each: a dark half stays in the average
        var bgra = Fill(160, 90, 0, 0, 0);
        PaintRows(bgra, 160, 45, 90, 200, 200, 200);

        var color = FrameAnalyzer.Analyze(bgra, 160, 90);

        Assert.InRange(color.R, 1, 199);
    }

    [Fact]
    public void BrightnessFollowsRec709()
    {
        Assert.Equal(100, FrameAnalyzer.Analyze(Fill(4, 4, 255, 255, 255), 4, 4).BrightnessPercent, 3);
        Assert.Equal(71.52, FrameAnalyzer.Analyze(Fill(4, 4, 0, 255, 0), 4, 4).BrightnessPercent, 2);
    }
}

public class PacingTests
{
    [Fact]
    public void FrameSlightlyEarlyIsStillDue()
    {
        // A frame aligned on a 60 Hz vsync arrives at 33.3 ms for a 33.3 ms (30 FPS) interval
        Assert.True(Pacing.MinInterval(1000.0 / 30) < TimeSpan.FromMilliseconds(1000.0 / 30));
        Assert.True(Pacing.MinInterval(1000.0 / 30) > TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public void FreshTimestampIsNotDue()
    {
        Assert.False(Pacing.IsDue(Pacing.Now, 1000));
    }
}

public class StaticScreenDetectorTests
{
    private const double Timeout = 10 * 60 * 1000;
    private static readonly FrameColor Blue = new(20, 40, 200, 30);

    private static StaticScreenDetector Started()
    {
        var detector = new StaticScreenDetector();
        detector.Reset(0);
        return detector;
    }

    [Fact]
    public void StillScreenGoesIdleAfterTheDelay()
    {
        var detector = Started();
        Assert.Equal(StaticScreenTransition.None, detector.Update(0, Blue, false, Timeout));
        // No frame at all (the compositor sends none for a still screen)
        Assert.Equal(StaticScreenTransition.None, detector.Update(Timeout - 1, null, false, Timeout));
        Assert.Equal(StaticScreenTransition.WentIdle, detector.Update(Timeout, null, false, Timeout));
        Assert.True(detector.IsIdle);
        Assert.Equal(StaticScreenTransition.None, detector.Update(Timeout + 500, null, false, Timeout));
    }

    [Fact]
    public void TinyColorChangesDoNotCount()
    {
        // Blinking caret, clock, video noise: the screen average moves by a few units at most
        var detector = Started();
        detector.Update(0, Blue, false, Timeout);
        var nearlyBlue = Blue with { R = (byte)(Blue.R + StaticScreenDetector.ColorTolerance) };
        detector.Update(Timeout / 2, nearlyBlue, false, Timeout);
        Assert.Equal(StaticScreenTransition.WentIdle, detector.Update(Timeout, Blue, false, Timeout));
    }

    [Fact]
    public void SlowDriftEventuallyCounts()
    {
        // Compared with the color at the start of the still period, not with the previous frame
        var detector = Started();
        detector.Update(0, Blue, false, Timeout);
        detector.Update(1000, Blue with { B = 202 }, false, Timeout);
        detector.Update(2000, Blue with { B = 204 }, false, Timeout);
        Assert.Equal(StaticScreenTransition.None, detector.Update(Timeout + 1000, null, false, Timeout));
        Assert.Equal(StaticScreenTransition.WentIdle, detector.Update(Timeout + 2000, null, false, Timeout));
    }

    [Fact]
    public void ColorChangeOrInputWakesUp()
    {
        var detector = Started();
        detector.Update(0, Blue, false, Timeout);
        detector.Update(Timeout, null, false, Timeout);
        Assert.Equal(StaticScreenTransition.WokeUp, detector.Update(Timeout + 1, Blue with { G = 90 }, false, Timeout));
        Assert.False(detector.IsIdle);

        detector.Update(3 * Timeout, null, false, Timeout);
        Assert.True(detector.IsIdle);
        Assert.Equal(StaticScreenTransition.WokeUp, detector.Update(3 * Timeout + 1, null, true, Timeout));
        // The delay starts again from the wake-up
        Assert.Equal(StaticScreenTransition.None, detector.Update(4 * Timeout, null, false, Timeout));
    }

    [Fact]
    public void InputKeepsTheLampOn()
    {
        // Reading a still page while scrolling: the screen barely changes but someone is there
        var detector = Started();
        detector.Update(0, Blue, false, Timeout);
        detector.Update(Timeout - 1000, Blue, true, Timeout);
        Assert.Equal(StaticScreenTransition.None, detector.Update(Timeout + 1000, Blue, false, Timeout));
    }

    [Fact]
    public void TurningTheOptionOffWakesUp()
    {
        var detector = Started();
        detector.Update(Timeout, null, false, Timeout);
        Assert.Equal(StaticScreenTransition.WokeUp, detector.Update(Timeout + 1, null, false, 0));
        Assert.Equal(StaticScreenTransition.None, detector.Update(10 * Timeout, null, false, 0));
    }
}

public class ShaderTests
{
    // The shader is compiled at runtime by Windows' d3dcompiler_47.dll: a syntax error would
    // only show up on the user's machine, as a capture that never starts.
    [Theory]
    [InlineData("VS", "vs_4_0")]
    [InlineData("PS", "ps_4_0")]
    public void DownscaleShaderCompiles(string entryPoint, string profile)
    {
        var bytecode = Compiler.Compile(ScreenCapturer.DownscaleShader, entryPoint, "Downscale", profile,
            ShaderFlags.OptimizationLevel3, EffectFlags.None);

        Assert.False(bytecode.IsEmpty);
    }
}

public class LocalizationTests
{
    public static IEnumerable<object[]> OtherLanguages() =>
        Loc.Languages.Where(l => l.Code != Loc.DefaultLanguage).Select(l => new object[] { l.Code });

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order().ToArray();

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void Language_HasSameKeysAndPlaceholdersAsEnglish(string code)
    {
        var english = Loc.Load(Loc.DefaultLanguage);
        var other = Loc.Load(code);

        Assert.Empty(english.Keys.Except(other.Keys));
        Assert.Empty(other.Keys.Except(english.Keys));
        foreach (var (key, text) in english)
        {
            Assert.True(Placeholders(text).SequenceEqual(Placeholders(other[key])), $"{code}: placeholders of {key}");
            Assert.True(text.Count(c => c == '|') == other[key].Count(c => c == '|'), $"{code}: '|' separators of {key}");
            Assert.False(string.IsNullOrWhiteSpace(other[key]), $"{code}: empty {key}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xx")]
    public void Resolve_FallsBackToASupportedLanguage(string? setting) =>
        Assert.Contains(Loc.Languages, l => l.Code == Loc.Resolve(setting));

    [Fact]
    public void Resolve_KeepsAChosenLanguage() => Assert.Equal("de", Loc.Resolve("de"));
}
