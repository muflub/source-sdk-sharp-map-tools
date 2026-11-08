//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.AssetDb.Imaging;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapGen.Content;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>The small imaging pieces: the image buffer, its statistics, the mip choice and the label font.</summary>
public sealed class ImagingTests
{
    private static readonly Rgba Red = new(255, 0, 0);
    private static readonly Rgba Blue = new(0, 0, 255);

    [Fact]
    public void AnImageRejectsAPixelBufferOfTheWrongLength()
    {
        Assert.Throws<ArgumentException>(() => new RgbaImage(2, 2, new byte[15]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbaImage(0, 2, []));
    }

    [Fact]
    public void FilledSetsEveryPixel()
    {
        RgbaImage image = RgbaImage.Filled(3, 2, Red);

        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                Assert.Equal(Red, image.Get(x, y));
            }
        }
    }

    [Fact]
    public void SetOutsideTheImageIsIgnored()
    {
        RgbaImage image = RgbaImage.Filled(2, 2, Red);

        image.Set(-1, 0, Blue);
        image.Set(0, 2, Blue);
        image.Set(1, 1, Blue);

        Assert.Equal(Blue, image.Get(1, 1));
        Assert.Equal(3, Enumerable.Range(0, 4).Count(i => image.Get(i % 2, i / 2) == Red));
    }

    [Fact]
    public void FillRectClipsToTheImage()
    {
        RgbaImage image = RgbaImage.Filled(4, 4, Red);

        image.FillRect(-2, 2, 4, 10, Blue);

        Assert.Equal(Blue, image.Get(0, 3));
        Assert.Equal(Blue, image.Get(1, 2));
        Assert.Equal(Red, image.Get(2, 2));
        Assert.Equal(Red, image.Get(0, 1));
    }

    [Fact]
    public void BlitCopiesAndClips()
    {
        RgbaImage image = RgbaImage.Filled(4, 4, Red);
        RgbaImage patch = RgbaImage.Filled(3, 3, Blue);

        image.Blit(patch, 2, -1);

        Assert.Equal(Blue, image.Get(2, 0));
        Assert.Equal(Blue, image.Get(3, 1));
        Assert.Equal(Red, image.Get(3, 2));
        Assert.Equal(Red, image.Get(1, 0));
    }

    [Fact]
    public void ToPngIsTheEncoderOverThePixels()
    {
        RgbaImage image = RgbaImage.Filled(3, 3, Blue);

        Assert.Equal(PngEncoder.Encode(image.Pixels, 3, 3), image.ToPng());
    }

    [Fact]
    public void StatsAverageWithHalfUpRoundingAndSpotAlpha()
    {
        RgbaImage image = new(2, 1, [10, 0, 255, 255, 11, 1, 0, 254]);

        ImageStats stats = ImageStats.Measure(image);

        // 10.5 rounds up, 0.5 rounds up, 127.5 rounds up.
        Assert.Equal(new ImageStats(11, 1, 128, true), stats);
        Assert.Equal("#0b0180", stats.Hex);
    }

    [Fact]
    public void AnOpaqueImageHasNoAlpha() =>
        Assert.False(ImageStats.Measure(RgbaImage.Filled(2, 2, Red)).HasAlpha);

    [Fact]
    public void TheMipChoiceIsTheLargestWithinTheLimit()
    {
        VtfFile texture = VtfFile.Parse(VtfWriter.Write(64, 16, ImageFormat.Bgra8888, 0, (_, _) => (0, 0, 0, 255)));

        Assert.Equal(0, MipChoice.For(texture, 0));
        Assert.Equal(0, MipChoice.For(texture, 64));
        Assert.Equal(1, MipChoice.For(texture, 63));
        Assert.Equal(2, MipChoice.For(texture, 16));
    }

    [Fact]
    public void WhenNoMipFitsTheSmallestIsChosen()
    {
        VtfFile texture = VtfFile.Parse(VtfWriter.Write(64, 64, ImageFormat.Bgra8888, 0x100, (_, _) => (0, 0, 0, 255)));

        Assert.Equal(0, MipChoice.For(texture, 8));
    }

    [Fact]
    public void TextMeasuresSixPixelsPerGlyphLessTheTrailingGap()
    {
        Assert.Equal(0, BitmapFont.Measure(string.Empty, 2));
        Assert.Equal(5, BitmapFont.Measure("A", 1));
        Assert.Equal(34, BitmapFont.Measure("TOP", 2));
    }

    [Fact]
    public void AGlyphDrawsItsRowsInInkOnTheBacking()
    {
        Rgba ink = new(1, 1, 1);
        Rgba backing = new(200, 200, 200);
        RgbaImage image = RgbaImage.Filled(9, 11, Red);

        BitmapFont.Draw(image, "a", 2, 2, 1, ink, backing);

        // 'A' is ".###." on top and "#...#" below; lower case draws upper case.
        Assert.Equal(backing, image.Get(2, 2));
        Assert.Equal(ink, image.Get(3, 2));
        Assert.Equal(ink, image.Get(5, 2));
        Assert.Equal(backing, image.Get(6, 2));
        Assert.Equal(ink, image.Get(2, 3));
        Assert.Equal(ink, image.Get(6, 3));

        // The backing is one font pixel wider all round; past it is untouched.
        Assert.Equal(backing, image.Get(1, 1));
        Assert.Equal(backing, image.Get(7, 9));
        Assert.Equal(Red, image.Get(0, 0));
        Assert.Equal(Red, image.Get(8, 10));
    }

    [Fact]
    public void ACharacterWithoutAGlyphDrawsAsSpace()
    {
        Rgba ink = new(1, 1, 1);
        Rgba backing = new(200, 200, 200);
        RgbaImage image = RgbaImage.Filled(8, 10, Red);

        BitmapFont.Draw(image, "~", 1, 1, 1, ink, backing);

        Assert.False(BitmapFont.HasGlyph('~'));
        Assert.DoesNotContain(ink, Enumerable.Range(0, 80).Select(i => image.Get(i % 8, i / 8)));
    }

    [Fact]
    public void EmptyTextDrawsNothing()
    {
        RgbaImage image = RgbaImage.Filled(4, 4, Red);

        BitmapFont.Draw(image, string.Empty, 0, 0, 1, Blue, Blue);

        Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(Red, image.Get(i % 4, i / 4)));
    }

    [Theory]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("0123456789")]
    [InlineData(".-+/:()")]
    public void EveryLabelCharacterHasAGlyph(string characters) =>
        Assert.All(characters, c => Assert.True(BitmapFont.HasGlyph(c), $"no glyph for '{c}'"));

    [Fact]
    public void EveryGlyphIsAFullFiveBySevenCellOfInkAndBlank()
    {
        // A glyph one cell short shifts every row after the gap, which still
        // draws something; only the cell count catches it.
        Assert.NotEmpty(BitmapFont.Characters);
        foreach (char c in BitmapFont.Characters)
        {
            string rows = BitmapFont.GlyphRows(c)!;
            Assert.True(rows.Length == BitmapFont.GlyphWidth * BitmapFont.GlyphHeight, $"'{c}' has {rows.Length} cells");
            Assert.True(rows.All(cell => cell is '#' or '.'), $"'{c}' has a cell that is neither ink nor blank");
            Assert.Contains('#', rows);
        }
    }

    [Fact]
    public void GlyphLookupFoldsCaseAndAnswersNullForMissing()
    {
        Assert.Equal(BitmapFont.GlyphRows('S'), BitmapFont.GlyphRows('s'));
        Assert.Null(BitmapFont.GlyphRows('~'));
    }
}
