// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2025-2026 Baba0bab — GoveeScreen

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
