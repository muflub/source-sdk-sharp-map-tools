//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.AssetDb;
using SourceSharp.AssetDb.Maps;
using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.Tests.MapFormats;

using Xunit;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// The map usage scan: faces to materials with area, the compiler's per-map
/// renames undone, entities, static props, and the compressed-lump reader.
/// </summary>
public sealed class MapUsageScannerTests
{
    private static byte[] StringData(out int[] offsets, params string[] names)
    {
        List<byte> data = [];
        offsets = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            offsets[i] = data.Count;
            data.AddRange(Encoding.ASCII.GetBytes(names[i] + "\0"));
        }

        return [.. data];
    }

    private static DFace Face(short texInfo, float area) => new() { TexInfo = texInfo, Area = area };

    /// <summary>A small map: three faces over two materials, a sky, entities and three static props.</summary>
    internal static byte[] SampleMap()
    {
        byte[] strings = StringData(out int[] offsets, "BRICK/WallA", "maps/test_map/metal/floor_1_-2_3");
        const string entities = """
            {
            "classname" "worldspawn"
            "skyname" "Sky_Day01"
            }
            {
            "classname" "info_overlay"
            "material" "Overlays/Sign01"
            }
            {
            "classname" "infodecal"
            "texture" "decals/Stain"
            }
            {
            "classname" "prop_dynamic"
            "model" "models/Props/Door.mdl"
            }
            {
            "classname" "prop_physics"
            "model" "models\props\barrel.mdl"
            }
            {
            "classname" "prop_physics"
            "model" "models/props/barrel.mdl"
            }
            {
            "classname" "func_brush"
            "model" "*1"
            }
            """;

        StaticPropLump props = new();
        props.ModelNames.Add("models/props/crate.mdl");
        props.ModelNames.Add("models/props/Barrel.mdl");
        props.Props.Add(new StaticProp { PropType = 0 });
        props.Props.Add(new StaticProp { PropType = 0 });
        props.Props.Add(new StaticProp { PropType = 1 });
        GameLumpEntry sprp = props.Write();

        return new BspTestBuilder()
            .Lump(BspLump.Entities, Encoding.ASCII.GetBytes(entities + "\0"))
            .Structs(BspLump.TexData, new DTexData { NameStringTableId = 0 }, new DTexData { NameStringTableId = 1 })
            .Structs(BspLump.TexInfo, new TexInfo { TexData = 0 }, new TexInfo { TexData = 1 }, new TexInfo { TexData = 9 })
            .Structs(BspLump.Faces, Face(0, 100f), Face(0, 50.5f), Face(1, 64f), Face(2, 1f), Face(7, 1f))
            .Structs(BspLump.TexDataStringTable, offsets)
            .Lump(BspLump.TexDataStringData, strings)
            .GameLump(sprp.Id, sprp.Flags, sprp.Version, sprp.Data.ToArray())
            .Build();
    }

    [Fact]
    public async Task FacesCountByMaterialWithTheirAreaAndTheCompilersRenamesUndone()
    {
        MapUsage usage = await MapUsageScanner.ReadAsync("test_map", SampleMap(), CancellationToken.None);

        Assert.Equal(5, usage.Faces);
        Assert.Contains(new MapMaterialUse("brick/walla", "face", 2, 150.5), usage.Materials);
        Assert.Contains(new MapMaterialUse("metal/floor", "face", 1, 64), usage.Materials);

        // Faces whose texinfo or texdata index is out of range are skipped, not guessed.
        Assert.Equal(3, usage.Materials.Where(m => m.Kind == "face").Sum(m => m.Count));
    }

    [Fact]
    public async Task EntitiesGiveTheSkyOverlaysDecalsAndModels()
    {
        MapUsage usage = await MapUsageScanner.ReadAsync("test_map", SampleMap(), CancellationToken.None);

        Assert.Equal("sky_day01", usage.SkyName);
        Assert.Equal(7, usage.Entities);
        Assert.Contains(new MapMaterialUse("overlays/sign01", "overlay", 1, 0), usage.Materials);
        Assert.Contains(new MapMaterialUse("decals/stain", "decal", 1, 0), usage.Materials);
        Assert.Contains(new MapModelUse("models/props/door.mdl", "entity", 1), usage.Models);
        Assert.Contains(new MapModelUse("models/props/barrel.mdl", "entity", 2), usage.Models);
        Assert.DoesNotContain(usage.Models, m => m.Model == "*1");
    }

    [Fact]
    public async Task StaticPropsCountPerModel()
    {
        MapUsage usage = await MapUsageScanner.ReadAsync("test_map", SampleMap(), CancellationToken.None);

        Assert.Equal(3, usage.StaticProps);
        Assert.Contains(new MapModelUse("models/props/crate.mdl", "static", 2), usage.Models);
        Assert.Contains(new MapModelUse("models/props/barrel.mdl", "static", 1), usage.Models);
        Assert.Null(usage.StaticPropsIssue);
    }

    [Fact]
    public async Task UsageIsSortedAndHashed()
    {
        MapUsage usage = await MapUsageScanner.ReadAsync("test_map", SampleMap(), CancellationToken.None);

        Assert.Equal(usage.Materials.OrderBy(m => m.Material, StringComparer.Ordinal).ThenBy(m => m.Kind, StringComparer.Ordinal), usage.Materials);
        Assert.Equal(usage.Models.OrderBy(m => m.Model, StringComparer.Ordinal).ThenBy(m => m.Kind, StringComparer.Ordinal), usage.Models);
        Assert.Equal(64, usage.Sha256.Length);
    }

    [Fact]
    public async Task AnUnreadableStaticPropLumpKeepsTheRestOfTheMap()
    {
        byte[] strings = StringData(out int[] offsets, "a/b");
        byte[] map = new BspTestBuilder()
            .Lump(BspLump.Entities, "{\n\"classname\" \"worldspawn\"\n}\n\0"u8.ToArray())
            .Structs(BspLump.TexData, new DTexData { NameStringTableId = 0 })
            .Structs(BspLump.TexInfo, new TexInfo { TexData = 0 })
            .Structs(BspLump.Faces, Face(0, 1f))
            .Structs(BspLump.TexDataStringTable, offsets)
            .Lump(BspLump.TexDataStringData, strings)
            .GameLump(GameLumpId.MakeId(GameLumpId.StaticProps), 0, 10, BitConverter.GetBytes(1000))
            .Build();

        MapUsage usage = await MapUsageScanner.ReadAsync("m", map, CancellationToken.None);

        Assert.Single(usage.Materials);
        Assert.Equal(0, usage.StaticProps);
        Assert.Contains("static props not counted", usage.StaticPropsIssue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHdrFacesStandInWhenThereAreNoLdrFaces()
    {
        byte[] strings = StringData(out int[] offsets, "a/b");
        byte[] map = new BspTestBuilder()
            .Lump(BspLump.Entities, "\0"u8.ToArray())
            .Structs(BspLump.TexData, new DTexData { NameStringTableId = 0 })
            .Structs(BspLump.TexInfo, new TexInfo { TexData = 0 })
            .Structs(BspLump.FacesHdr, Face(0, 3f), Face(0, 4f))
            .Structs(BspLump.TexDataStringTable, offsets)
            .Lump(BspLump.TexDataStringData, strings)
            .Build();

        Assert.Equal(new MapMaterialUse("a/b", "face", 2, 7), Assert.Single((await MapUsageScanner.ReadAsync("m", map, CancellationToken.None)).Materials));
    }

    /// <summary>A static prop lump of any record size: dictionary, no leaves, props whose model index is at byte 24.</summary>
    private static byte[] StaticProps(int stride, string[] names, params ushort[] types)
    {
        List<byte> bytes = [.. BitConverter.GetBytes(names.Length)];
        foreach (string name in names)
        {
            byte[] field = new byte[128];
            Encoding.ASCII.GetBytes(name).CopyTo(field, 0);
            bytes.AddRange(field);
        }

        bytes.AddRange(BitConverter.GetBytes(0));
        bytes.AddRange(BitConverter.GetBytes(types.Length));
        foreach (ushort type in types)
        {
            byte[] record = new byte[stride];
            BitConverter.GetBytes(type).CopyTo(record, 24);
            bytes.AddRange(record);
        }

        return [.. bytes];
    }

    [Theory]
    [InlineData(56)]
    [InlineData(64)]
    [InlineData(72)]
    [InlineData(76)]
    public void StaticPropsReadAtAnyRecordSize(int stride)
    {
        byte[] lump = StaticProps(stride, ["models/a.mdl", "models/b.mdl"], 1, 0, 1);

        Assert.True(MapUsageScanner.TryReadStaticPropModels(lump, out List<string> models, out string? problem));
        Assert.Null(problem);
        Assert.Equal(["models/b.mdl", "models/a.mdl", "models/b.mdl"], models);
    }

    [Fact]
    public async Task AVersionSevenLumpWithTheVersionTenLayoutIsCounted()
    {
        // What Team Fortress 2's pl_cactuscanyon ships: labelled 7, 72-byte records.
        byte[] strings = StringData(out int[] offsets, "a/b");
        byte[] map = new BspTestBuilder()
            .Lump(BspLump.Entities, "\0"u8.ToArray())
            .Structs(BspLump.TexData, new DTexData { NameStringTableId = 0 })
            .Structs(BspLump.TexInfo, new TexInfo { TexData = 0 })
            .Structs(BspLump.Faces, Face(0, 1f))
            .Structs(BspLump.TexDataStringTable, offsets)
            .Lump(BspLump.TexDataStringData, strings)
            .GameLump(GameLumpId.MakeId(GameLumpId.StaticProps), 0, 7, StaticProps(72, ["models/Cactus.mdl"], 0, 0))
            .Build();

        MapUsage usage = await MapUsageScanner.ReadAsync("pl_test", map, CancellationToken.None);

        Assert.Null(usage.StaticPropsIssue);
        Assert.Equal(new MapModelUse("models/cactus.mdl", "static", 2), Assert.Single(usage.Models));
    }

    [Fact]
    public void AnEmptyStaticPropLumpHasNoProps()
    {
        Assert.True(MapUsageScanner.TryReadStaticPropModels(StaticProps(72, ["models/a.mdl"]), out List<string> models, out _));
        Assert.Empty(models);
    }

    [Fact]
    public void MalformedStaticPropLumpsSayWhy()
    {
        byte[] uneven = [.. StaticProps(72, ["models/a.mdl"], 0, 0), 1];
        byte[] badIndex = StaticProps(72, ["models/a.mdl"], 0, 3);
        byte[] tiny = StaticProps(26, ["models/a.mdl"], 0)[..^6]; // one 20-byte record: too short to hold the model index
        byte[] noLeaves = [.. BitConverter.GetBytes(0)];
        byte[] noCount = [.. BitConverter.GetBytes(0), .. BitConverter.GetBytes(0)];

        Assert.False(MapUsageScanner.TryReadStaticPropModels(uneven, out _, out string? a));
        Assert.Contains("do not divide", a, StringComparison.Ordinal);
        Assert.False(MapUsageScanner.TryReadStaticPropModels(badIndex, out List<string> none, out string? b));
        Assert.Contains("names model 3", b, StringComparison.Ordinal);
        Assert.Empty(none);
        Assert.False(MapUsageScanner.TryReadStaticPropModels(tiny, out _, out _));
        Assert.False(MapUsageScanner.TryReadStaticPropModels(noLeaves, out _, out string? c));
        Assert.Contains("leaf list", c, StringComparison.Ordinal);
        Assert.False(MapUsageScanner.TryReadStaticPropModels(noCount, out _, out string? d));
        Assert.Contains("prop count", d, StringComparison.Ordinal);
        Assert.False(MapUsageScanner.TryReadStaticPropModels([], out _, out string? e));
        Assert.Contains("dictionary", e, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BRICK/WallA", "x", "brick/walla")]
    [InlineData("maps/cp_well/metal/floor_1_-2_3", "cp_well", "metal/floor")]
    [InlineData("maps/cp_well/nature/water_well_depth_128", "cp_well", "nature/water_well")]
    [InlineData("maps/cp_well/nature/blend_wvt_patch", "cp_well", "nature/blend")]
    [InlineData("maps/cp_well/custom/thing", "cp_well", "custom/thing")]
    [InlineData("maps/other/metal/floor_1_2_3", "cp_well", "maps/other/metal/floor_1_2_3")]
    [InlineData("metal/floor_1_2_3", "cp_well", "metal/floor_1_2_3")]
    public void PerMapRenamesFoldBackToTheAuthorsMaterial(string stored, string map, string expected) =>
        Assert.Equal(expected, MapUsageScanner.MaterialName(stored, map));

    [Theory]
    [InlineData("maps/CP_Well.bsp", "cp_well")]
    [InlineData("maps\\sub\\koth_x.BSP", "koth_x")]
    [InlineData("plain", "plain")]
    public void MapNamesAreTheFileName(string path, string expected) =>
        Assert.Equal(expected, MapUsageScanner.MapName(path));

    [Fact]
    public async Task ScanningAGameReadsEveryMapAndRecordsTheOnesItCannot()
    {
        AssetTestGame game = new AssetTestGame()
            .AddRaw("maps/test_map.bsp", SampleMap())
            .AddRaw("maps/broken.bsp", [1, 2, 3, 4]);

        (IReadOnlyList<MapUsage> maps, IReadOnlyList<ImportIssue> issues) = await MapUsageScanner.ScanAsync(
            game.Mount(), AssetTestGame.Options(), CancellationToken.None);

        MapUsage map = Assert.Single(maps);
        Assert.Equal("test_map", map.Name);
        Assert.False(string.IsNullOrEmpty(map.Source));
        Assert.Equal(("map", "maps/broken.bsp"), (Assert.Single(issues).Kind, issues[0].Path));
    }
}
