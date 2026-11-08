//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Compression;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Compression;

/// <summary>
/// The LZMA decoder against streams from an independent encoder (liblzma, via
/// Python's <c>lzma</c> module, raw LZMA1), and the engine's lump container.
/// </summary>
/// <remarks>
/// The input is generated here and in the generator alike: repeated text for
/// long matches and repeat distances, a linear-congruential run for literals
/// that do not compress, a long zero run for maximum-length matches, and a
/// far copy of the random run for a long distance. Each vector uses
/// different lc/lp/pb, so every literal-context and position-state path is
/// taken.
/// </remarks>
public sealed class LzmaDecoderTests
{
    /// <summary>The generator's input, rebuilt.</summary>
    internal static byte[] Sample()
    {
        List<byte> data = [.. System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 40)))];
        uint x = 1;
        List<byte> random = [];
        for (int i = 0; i < 1024; i++)
        {
            x = ((x * 1103515245) + 12345) & 0x7fffffff;
            random.Add((byte)((x >> 16) & 0xff));
        }

        data.AddRange(random);
        data.AddRange(new byte[2000]);
        data.AddRange(random.Take(300));
        data.AddRange(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcabcabd", 50))));
        return [.. data];
    }

    [Theory]
    [InlineData(3, 0, 2, "5d00000100", "ACoaCKIDJWbxS3jFogX/LubZ0iAarTT44h3oQTb63AZpuzzkEDQnCeuzZuPtN5jtkq3VJ0UIMF5dcTGRFfwYeBJn3pat40/i3A02CMjda1l8evnfN6KBXJ4CLgTC7B5rZmuB5bnnPXEkSh+VMMZXqUAjLkvfWAJ+zEuMNrFia1n9yUkvmNJX4UyxU3u3QOUMYz7WWb7zBEE0IUgNjCfLGISKHq31kBT82OF3QwU26ciFgZIx69HdPHiQhRPux/JInI9oVlWrN/GpQtC79KNYDRDegF9y6KYYHLczgMyQLjVzSU4J4l4RYbCoMjrYmv/dS/CN0jKUJ5AmHkc4VxxW/8+hY7QTBwY8f3TH9Eskn87nhYN6DjVyM5Rqh+SucPjaH3u8QqGXT7BXN9xzPKNM2ImnozqZcCS25/TjhCzwbEoXcDFLHKwCKVeZas2q76TIwzR7JxhwtydRgpYg6Zw6mCER81v/r1O1CQ+lKZuKaKXFqc7RvJ0DAPAyLNeXUsm2ltkJh+8kIEDodwU0bu5UUH/cAP11Vs/aMKxy2jLbKBp6IdDa8cTwxcXGIrRD/evwDQJWbUGdDmrYsWKa11mv8BoDsYfDEXrMG1bYqECEBhIk1zFe+H/WQ2em00qF1vbJD42PVvRwni8GsLs71RCubPRF+lD2DtYHvECoq78rOX4YbeapcTcDzbzkpySW4Kb4ncl7bciq+MNEo7uqdywlFzHI/0/AQPcHJcwJvW85kJ5E5w8LQoXv0xmErcEwebtyp8sjT12eRBLGJ4GhpjC7NzEoUDksmYmjWJFeYt0y8JaxAJIxAfgH5kFh4+O1rwt9RhVuv42VwT7vm/UyoUytNxqTnu/84jZV41+FFN093ok1h5/EzDHczsTGhc5c3QB/tF3hbV/tckakl5Qkv7F3PzjuozWJg6KzQWQTJCT/aEI5r8UsCIKjTYe8xG1FsAdiUn8JVxL9j7PiBMKbKsUY7QLvZ3L/59+Y+Evcf+XolhiKEH64j526MMu1dAzIzGhVg6bYvawsnELlTSXsBXBEZsR7NO7RrH4nrfQ+NCrM0De9+kOkbqNdZ74Rd4fgAZWJmbx0fK8lOPUi5rsDP10X/nhmMFYT7g5Vr0D+ET61S7+p8piaLJ4WqDvx+4zWr2/AbBahEPltlCUnfDQ6JvhwzO6xXZCP/bcRe0zSB29hDphas9p7iPT4L+OgYixcsUt8ySM444FwvdzaVBfXV2rX6teLAscwq9IQ9KEpea56YSj1xkE0cTfeUQOkS8wzacjaBb8LWd639aHAFjuuaTclg5VchwySN2UE5W+DsB2LbZPtKiM4JmjSIR0jWkSJyObUgUUs/0Ikctyc2rF9Kzkj64CsVgzTzqvNSJzqxYfx8vVdpDQGK5W6Wr7AG7INnko60PPQQk41Mm4iVr3X/D3m+um0+DdHj85tOaHtUn4tT1tbYOYMQoS1uNHRLb5OaUuuC8IGbQjVvZ7IBj+CzUoTTWHByO1avf8cdJK3OMf//umO4A==")]
    [InlineData(0, 2, 0, "1200000100", "ACoa2dR0RO8pRtClfuS4ZIEJWYn+mQ7JesAuCfaxbTbbuzZJTMyB+hhgJYdoGgAdVAatZTQVjBXyJRw6YwB4fv5+W0L0/Y/vbwLjSChJhmSXsM4lYqJIWIHarCgATfUXvkNszmL2Tkwh/uKr/1ux6uCR/mWUR3zCTGIUjlNcElqcxhg6bQ7UBb/5qohpGirWpP1Fgl7pKOzuc7DBgn2xo8xZKJGW+1n2mrBJLXNzoPwfbDwY1LgT/cyaBk8EHgNyUlEW0Ns75XA7r9JDkqIy+kMJdv78iL+mBTqsRmE1DYu3pi6i8i4opCajV0CZl+X1bCktZ3msiV+sF8sAT3x0JMS6NaDlihDTnceUSWfTjB9u1lCxO233s4W6Rj89O2e86Ou1uplkHcblwDAC0+1f2lch55YLylXozPuWT8+4bQPx/plkkRRIZtPoJF6XEFsSYx5yLapZErxiuNZZkUZcv/qUPRewvtebtLuQFW6dmdUMheIAiy93D79q73LMFoYO6jm6C4rcJdTNK7alrxiF9r2YLs4Dr0ep9baWZgzU74xzrw/wpgOpZxQx8zRR2lvl+dr+Z1e3PRZmDgUmBs337haJajX0y9z+FUmRrkwaPoNlw4QvpTR2f7owl6mrEnSiCW2DGAH7hmvugBwMjeiC6viocZxYeAGR9Kxxu3O7x9ek667AD3E//zQRKvQ2cnbTWnlfdV6q/htTvm61gEKzypscBaq65hXLZ8CJYTQ0dKLclzrTpr5SFmZrxwJH0hAVyE8YiQDSoIqBYO+nx2d0sDTuCkutqWia0HpwiLQJoaOuahpumzFlyQ+Pov+Uo7VixrpmPsuyYlvkWuQNrWuJvt3YjQeOC/IVXjW9lAjhHr3AF7SfefzTf9Y6FHirrAMpXLzZfLQq+PE4QDqJGQEvLDuqAGXnIxJbL13GVDGQ26ny2b3OuIGdVF0W8UpWV/w1VopOkEDg57o3dzDdGgJccIAfnUxD5CTnDCjHReZPQuP+moYJYuKrf13rNBP4bcim6GhW2gZVk0S7fw6cqnGpG0O3V7pJ5Dxq0jqytM8zWaIw/pfF2G3+XL0L3KstAIj/tCF3tyucEfjle0q04O203sNZiG12pV556Nw0JBwp3V4LqlLxgki+Xfg/OpSqkPVZDMuFHuxzimjdeP00Dx9z1DANwfLrXToNsoV20v78IxFMYbys36NwVEeFVeAAC3zA/NGRwtxgdStRqeHH3j2H0cAgk/41qg9kCfOVn7miOhJdLy9M36ZbtdSLiGFW7fjyIPhtW4YBW+Ng5cVF2UoLn9+rK3y/wHIEoNw4JoopGahTeQ31RpRyy92Z/KtlaQFkZeGtk/tvBOFJOshnjDsfeTOuftCa8tgNSko72svyrehqQoPyXfeW1+eTY1ShFIvUnHbs9fn2ntZAhGNiwif3arVOGkJfUw1IGVvLBt4N5ua6TYTzSTQQ08Bts49jMzuy1p9rk/5L/yW9ONuoIk2Dsakw2666uF/51OqA")]
    [InlineData(1, 3, 4, "d000000100", "ACoaCKIDidTSYzc2/LpuAEBOIRnDNNtYR00Ry8nw1XjTujsmWCbTq1pUO1jAw+92f2i5ct2ddnI00S7C8Cv0gB8cu9/zbkZA79rYeONyB3+HhoYM4ItukOY2pjITCiq1XzILL/dh6rznfxoYdzmu7yA6SKkJespJ+CTHTZH/uJl9wceMo9mtEBpOxdev1fcH6nta2qwbNNOzOcRmq4GkGi3BcPKvnUwkfjfHT6iCi6vatbBJdIjY+fKPu+RmIII4DKZ/JHvqM8aPHk4tk6nc4Iff/wvkuFS9q7PTg93hrcctYbXMsVDpib0b05vYDTTLx9CwWD6i3ynRfSVHWGBZ8XuGAfkf8AEbPZYR8oIxgDlbhgJXfv1smW5q0rEGf2EU5nm9sQvdrYNn3OY0dl77URRrHuMnob9zIw8ROwObanGuT2KReYRE2h9FVzwee9RzjFODkLbnz/oJEwiXmdb8wfgZENxpDSiAnwhzb5L1UuFFCcf3b25CjEDIwtvJRXSFtjyjFW/IRa1XIhnsOyTAyOfTLI05ue4Rx4cclBQtRlYVJDFeIwHKZBZnVyXJ/XFV4ASgEwvcpTklhv0cw3j0BSqxbkGMinFH0ubqLJ2162zNtfaj1b1HdcB8C+D2RrlHG5wZS7KRSDvl5zIhN3UNAbU0VRg+G6VKRX6EMv2v5EjSTLCowz850eCrzmMr+1b09vKkDMY5YppsFZXWBml0lGyXaUZrA9vXt/LhT0gFIkFA+qvh4JeMQueMLQg7H7P6HZYmWWZNGM3FTCEMc42d8SfD+YTtyx+viEDrCBRGcLxqZNp/b1a7fRst7NuKNQxG+ZE1W09wPouu3P2/CVpmg0xEPrtjhlo7FxF7l6awMsuBzSCVi6P1biLbgjMmYgqs9GCgifIqvFmL05qlNx+nPcrF67xBeEKYf7k4xsVdf8GzyiFaN8BAr7cCNsVQ2IEFNjR+0bxxTaoB0sj4GiFk7faP4Wp07gWr2jLBPfrMcJIhJBHaW6Lqe0UIzPJKhnkCBTJH+WvvCAx/PwV5LMMwkdrmS7Q/COOl+S/nFH0wMrmT03m+dXcrlyJJ04Ctx6I8L8hn/HJDA/u/2CITxziPlZB5tKRA4kmk38DTUnfBK33paLwI/Sz13PqXVw88T7d9aXV3Smkedd1693K9uTMfXn3nFL6kw1uvdgS6T/6UDAIFzkCi0DP5I/3rBB4Dkm1OMggJROFsVPUDLWgoMHmNkTkBaoeCd2Bo6sFafqeZrzxJ9VgfWQm53imy01uVnSVI3AokeLfURPJxGfUIofqA41Q7myTgjmd6nxW8PNUwizGY+QpG3Zlyc9p18sicpg9bsWtqRloR1tasy0geNHhiBThzcpfztpxI9MWZ1gB6I2CUwJSdAm2S80vJWDyc9rN4YjtMJmz8EH7z9OVyKwiCYTGqulRIGODIxC7QBQckhL60UjMpk22uOJbK02Mofk9K5a6JlK9ApmATYxj6fOI+WBLQkEcnyu/ei1muxc43Dl9IjrIPog3jbONo/q7xqM3k+Gj6WML+V+znBcKjGkL///AmTsA=")]
    public void DecodesAnIndependentEncodersStream(int lc, int lp, int pb, string properties, string stream)
    {
        byte[] expected = Sample();
        byte[] props = Convert.FromHexString(properties);

        byte[] decoded = LzmaDecoder.Decode(props, Convert.FromBase64String(stream), expected.Length);

        Assert.Equal((pb * 5 + lp) * 9 + lc, props[0]);
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void DecodingStopsAtTheDeclaredSize()
    {
        byte[] expected = Sample();

        byte[] prefix = LzmaDecoder.Decode(Convert.FromHexString("5d00000100"), Convert.FromBase64String(Stream0), 100);

        Assert.Equal(expected[..100], prefix);
    }

    [Fact]
    public void ATruncatedStreamThrowsRatherThanReturningShort()
    {
        byte[] stream = Convert.FromBase64String(Stream0);

        Assert.Throws<InvalidDataException>(() =>
            LzmaDecoder.Decode(Convert.FromHexString("5d00000100"), stream.AsSpan(0, stream.Length / 2), Sample().Length));
    }

    [Fact]
    public void BadPropertiesOrAMissingRangeHeaderThrow()
    {
        Assert.Throws<InvalidDataException>(() => LzmaDecoder.Decode([225, 0, 0, 1, 0], Convert.FromBase64String(Stream0), 10));
        Assert.Throws<InvalidDataException>(() => LzmaDecoder.Decode([93, 0, 0], Convert.FromBase64String(Stream0), 10));
        Assert.Throws<InvalidDataException>(() => LzmaDecoder.Decode(Convert.FromHexString("5d00000100"), [1, 2, 3, 4, 5, 6], 10));
        Assert.Throws<InvalidDataException>(() => LzmaDecoder.Decode(Convert.FromHexString("5d00000100"), [0, 1], 10));
    }

    [Fact]
    public void ZeroBytesDecodeToNothing() =>
        Assert.Empty(LzmaDecoder.Decode(Convert.FromHexString("5d00000100"), Convert.FromBase64String(Stream0), 0));

    [Fact]
    public void TheEngineContainerUnpacksToItsDeclaredSize()
    {
        byte[] expected = Sample();

        byte[] container = Container(Convert.FromBase64String(Stream0), expected.Length);

        Assert.True(ValveLzma.IsCompressed(container));
        Assert.Equal(expected, ValveLzma.Decompress(container));
    }

    [Fact]
    public void AContainerWithoutItsHeaderOrWithAnOversizedPayloadIsRefused()
    {
        byte[] good = Container(Convert.FromBase64String(Stream0), Sample().Length);
        byte[] lying = (byte[])good.Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(lying, 8);

        Assert.False(ValveLzma.IsCompressed([1, 2, 3, 4]));
        Assert.False(ValveLzma.IsCompressed("LZ"u8));
        Assert.Throws<InvalidDataException>(() => ValveLzma.Decompress([1, 2, 3, 4]));
        Assert.Throws<InvalidDataException>(() => ValveLzma.Decompress(good.AsSpan(0, 10)));
        Assert.Throws<InvalidDataException>(() => ValveLzma.Decompress(lying));
    }

    [Fact]
    public void AZipEntryUnpacksThroughItsFramingAndOnlyFivePropertiesBytesAreAccepted()
    {
        byte[] expected = Sample();
        byte[] payload = ZipPayload(Convert.FromBase64String(Stream0));
        byte[] sevenProps = (byte[])payload.Clone();
        sevenProps[2] = 7;

        Assert.Equal(expected, ZipLzma.Decompress(payload, expected.Length));
        Assert.Throws<InvalidDataException>(() => ZipLzma.Decompress(sevenProps, expected.Length));
        Assert.Throws<InvalidDataException>(() => ZipLzma.Decompress(payload.AsSpan(0, 6), expected.Length));
    }

    /// <summary>Frames a raw stream as a method-14 zip entry: SDK version, properties size, properties, stream.</summary>
    internal static byte[] ZipPayload(byte[] raw) =>
        [9, 20, 5, 0, .. Convert.FromHexString("5d00000100"), .. raw];

    /// <summary>Wraps a raw stream in the engine's lump container.</summary>
    internal static byte[] Container(byte[] raw, int unpacked)
    {
        List<byte> bytes = [.. "LZMA"u8.ToArray()];
        bytes.AddRange(BitConverter.GetBytes(unpacked));
        bytes.AddRange(BitConverter.GetBytes(raw.Length));
        bytes.AddRange(Convert.FromHexString("5d00000100"));
        bytes.AddRange(raw);
        return [.. bytes];
    }

    /// <summary>The first vector's stream (lc 3, lp 0, pb 2, 64 KiB dictionary).</summary>
    internal const string Stream0 = "ACoaCKIDJWbxS3jFogX/LubZ0iAarTT44h3oQTb63AZpuzzkEDQnCeuzZuPtN5jtkq3VJ0UIMF5dcTGRFfwYeBJn3pat40/i3A02CMjda1l8evnfN6KBXJ4CLgTC7B5rZmuB5bnnPXEkSh+VMMZXqUAjLkvfWAJ+zEuMNrFia1n9yUkvmNJX4UyxU3u3QOUMYz7WWb7zBEE0IUgNjCfLGISKHq31kBT82OF3QwU26ciFgZIx69HdPHiQhRPux/JInI9oVlWrN/GpQtC79KNYDRDegF9y6KYYHLczgMyQLjVzSU4J4l4RYbCoMjrYmv/dS/CN0jKUJ5AmHkc4VxxW/8+hY7QTBwY8f3TH9Eskn87nhYN6DjVyM5Rqh+SucPjaH3u8QqGXT7BXN9xzPKNM2ImnozqZcCS25/TjhCzwbEoXcDFLHKwCKVeZas2q76TIwzR7JxhwtydRgpYg6Zw6mCER81v/r1O1CQ+lKZuKaKXFqc7RvJ0DAPAyLNeXUsm2ltkJh+8kIEDodwU0bu5UUH/cAP11Vs/aMKxy2jLbKBp6IdDa8cTwxcXGIrRD/evwDQJWbUGdDmrYsWKa11mv8BoDsYfDEXrMG1bYqECEBhIk1zFe+H/WQ2em00qF1vbJD42PVvRwni8GsLs71RCubPRF+lD2DtYHvECoq78rOX4YbeapcTcDzbzkpySW4Kb4ncl7bciq+MNEo7uqdywlFzHI/0/AQPcHJcwJvW85kJ5E5w8LQoXv0xmErcEwebtyp8sjT12eRBLGJ4GhpjC7NzEoUDksmYmjWJFeYt0y8JaxAJIxAfgH5kFh4+O1rwt9RhVuv42VwT7vm/UyoUytNxqTnu/84jZV41+FFN093ok1h5/EzDHczsTGhc5c3QB/tF3hbV/tckakl5Qkv7F3PzjuozWJg6KzQWQTJCT/aEI5r8UsCIKjTYe8xG1FsAdiUn8JVxL9j7PiBMKbKsUY7QLvZ3L/59+Y+Evcf+XolhiKEH64j526MMu1dAzIzGhVg6bYvawsnELlTSXsBXBEZsR7NO7RrH4nrfQ+NCrM0De9+kOkbqNdZ74Rd4fgAZWJmbx0fK8lOPUi5rsDP10X/nhmMFYT7g5Vr0D+ET61S7+p8piaLJ4WqDvx+4zWr2/AbBahEPltlCUnfDQ6JvhwzO6xXZCP/bcRe0zSB29hDphas9p7iPT4L+OgYixcsUt8ySM444FwvdzaVBfXV2rX6teLAscwq9IQ9KEpea56YSj1xkE0cTfeUQOkS8wzacjaBb8LWd639aHAFjuuaTclg5VchwySN2UE5W+DsB2LbZPtKiM4JmjSIR0jWkSJyObUgUUs/0Ikctyc2rF9Kzkj64CsVgzTzqvNSJzqxYfx8vVdpDQGK5W6Wr7AG7INnko60PPQQk41Mm4iVr3X/D3m+um0+DdHj85tOaHtUn4tT1tbYOYMQoS1uNHRLb5OaUuuC8IGbQjVvZ7IBj+CzUoTTWHByO1avf8cdJK3OMf//umO4A==";
}
