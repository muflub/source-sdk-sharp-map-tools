//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;

using Xunit;
using Xunit.Abstractions;

using static SourceSharp.Tests.MapTools.Rooms.MultiCellHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// A sky-ceilinged room larger than one cell under the library's sun, with
/// the library's 3D skybox and its overhang (the rooms design, 4.12, D36's
/// parallax and 17.8): the 2 x 2 hall carries a sun layer at its four
/// turns, and linked away from its bakes' cell, at every turn, it agrees
/// with vrad of its link; without the overhang the two agree to the luxel,
/// so what is left is the overhang's shadow edge.
/// </summary>
public sealed class MultiCellSkyTests(MultiCellSkyTests.Fixture fixture, ITestOutputHelper output) : IClassFixture<MultiCellSkyTests.Fixture>
{
    /// <summary>The world x of the overhang's shadow edge across the hall's floor, where the level places it.</summary>
    private const long EdgeX = 640;

    /// <summary>The four quarter turns, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>The hall bakes four turns and a sun layer for each, which the parallax moves.</summary>
    [Fact]
    public void TheHallCarriesASunLayer()
    {
        RoomLighting hall = fixture.Lit.Get("hall").LightingOfCompile!;
        Assert.Equal(4, hall.RotationCount);
        Assert.NotNull(hall.SunLayer);
        Assert.Equal(4, hall.SunLayer!.Turns.Length);
    }

    /// <summary>
    /// The hall alone at (1, 1), away from its bakes' cell, at every turn:
    /// its luxels against vrad of its own linked map, the sun's parallax
    /// moved by the skybox's sun map. Held to the one-cell sky room's p95
    /// (0.03), maximum (0.3) and energy (1%); its p99 to 0.15, not 0.1,
    /// because the overhang's shadow edge crosses the hall's floor, which no
    /// one-cell room's walls let the sky reach: a line of 25 luxels at world
    /// x 640, on the luxel lattice, which vrad lights half and the sun map's
    /// point sample whole, about 0.18 each. Every floor luxel past 0.1 stands
    /// on that line. Measured: p95 0.017 to 0.028, p99 0.078 to 0.134, max
    /// 0.185 to 0.247, energy 1.000 to 1.001, 27 to 38 of 2,916 luxels past
    /// 0.1 (25 or 26 of them the floor's line). Red with the parallax payload
    /// left out: p95 0.77 to 0.82, energy 0.80 to 0.81, 522 to 574 past 0.1.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ALargerSkyRoomAwayFromItsBakesCellLinksToVradOfItsLink(int rotation)
    {
        LevelGrid level = Level(rotation);
        BspData linked = (await RoomLightHarness.LinkAsync(fixture.Lit, level)).Bsp;
        BspData relit = await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(fixture.Unlit, level)).Bsp, SkyboxLitFixture.Options);
        LevelLinkerSkyboxLightingTests.LuxelMetric metric = LevelLinkerSkyboxLightingTests.Luxels(linked, relit);
        List<(long X, long Y, long Z, int Nz)> past = Past(linked, relit, 0.1);
        output.WriteLine($"hall@{rotation} at (1, 1) against vrad of its link: {metric}, {past.Count} past 0.1, {past.Count(p => p.Nz == 1000)} on the floor");
        Assert.True(metric.Count > 0);
        Assert.True(metric.P95 <= 0.03, $"p95 {metric.P95}");
        Assert.True(metric.P99 <= 0.15, $"p99 {metric.P99}");
        Assert.True(metric.Max <= 0.3, $"max {metric.Max}");
        Assert.InRange(metric.Energy, 0.99, 1.01);
        Assert.True(past.Count <= metric.Count * 2 / 100, $"{past.Count} of {metric.Count} past 0.1");
        Assert.All(past.Where(p => p.Nz == 1000), p => Assert.Equal(EdgeX * 100, p.X));
    }

    /// <summary>
    /// The control: the same hall and skybox without the overhang, at the
    /// turns the edge costs most, links to vrad of its link to within 0.02
    /// at every luxel (measured 0.000), so the edge is all the other fact
    /// allows for.
    /// </summary>
    [Theory]
    [InlineData(180)]
    [InlineData(270)]
    public async Task WithoutTheOverhangTheLargerSkyRoomIsVradsToTheLuxel(int rotation)
    {
        LevelGrid level = Level(rotation);
        (RoomLibrary lit, RoomLibrary unlit) = await fixture.OpenAsync();
        BspData linked = (await RoomLightHarness.LinkAsync(lit, level)).Bsp;
        BspData relit = await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(unlit, level)).Bsp, SkyboxLitFixture.Options);
        LevelLinkerSkyboxLightingTests.LuxelMetric metric = LevelLinkerSkyboxLightingTests.Luxels(linked, relit);
        output.WriteLine($"hall@{rotation} without the overhang: {metric}");
        Assert.True(metric.Count > 0);
        Assert.True(metric.Max <= 0.02, $"max {metric.Max}");
    }

    /// <summary>The hall at (1, 1), capped, in a 3 x 3 grid.</summary>
    private static LevelGrid Level(int rotation) => RoomPropHarness.Level("~, +, +", $"~, hall@{rotation}, +", "~, ~, ~");

    /// <summary>The luxels of two maps at the same points that differ past <paramref name="limit"/>: where (in hundredths) and the normal's z (in thousandths).</summary>
    private static List<(long X, long Y, long Z, int Nz)> Past(BspData a, BspData b, double limit)
    {
        var la = LitCompare.Lattice(a);
        var lb = LitCompare.Lattice(b);
        List<(long, long, long, int)> past = [];
        foreach ((var key, (List<ColorRgbExp32> colours, bool thin)) in la)
        {
            if (!lb.TryGetValue(key, out var other) || thin || other.Thin)
            {
                continue;
            }

            if (colours.Min(c => other.Colours.Min(o => LitCompare.Relative(c.ToLinear(), o.ToLinear()))) > limit)
            {
                past.Add((key.Item1, key.Item2, key.Item3, key.Item6));
            }
        }

        return past;
    }

    /// <summary>The hall with a sky ceiling and a lamp, the sun, the skybox with its overhang: compiled lit and unlit once; the same without the overhang on first use.</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private readonly Lazy<Task<(RoomLibrary, RoomLibrary)>> _open = new(async () => (await BuildAsync(true, false), await BuildAsync(false, false)));

        /// <summary>The rooms lit, the skybox's sun map with them.</summary>
        public RoomLibrary Lit { get; private set; } = null!;

        /// <summary>The rooms unlit.</summary>
        public RoomLibrary Unlit { get; private set; } = null!;

        /// <summary>The rooms lit and unlit under a skybox without the overhang.</summary>
        public Task<(RoomLibrary Lit, RoomLibrary Unlit)> OpenAsync() => _open.Value;

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            Lit = await BuildAsync(true, true);
            Unlit = await BuildAsync(false, true);
        }

        /// <inheritdoc/>
        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>
        /// The library: the 2 x 2 hall, its ceiling's underside sky over the
        /// whole footprint, a lamp in its anchor cell; the sun in the gap past
        /// it; the skybox harness's cell, with the sky room fixture's
        /// overhang when asked; compiled as <c>ssmap room</c> compiles it.
        /// </summary>
        private static async Task<RoomLibrary> BuildAsync(bool light, bool overhang)
        {
            VmfDocument library = RoomHarness.LibraryVmf(Hall);
            library.Chunks.Add(RoomLightHarness.Light(810, new Vec3(100, 60, 120)));
            library.Chunks.Add(VmfPlacement.MoveEntity(RoomLightHarness.Sun(), QuarterTurn.Translation(new Vec3(RoomHarness.Cell, 0, 0))));
            RoomLightHarness.SkyCeiling(library, 0, RoomLightHarness.Sky, RoomHarness.Cell, 2 * RoomHarness.Cell);
            RoomSkyboxHarness.AddSkybox(library);
            if (overhang)
            {
                library.GetChunk(MapFileLoader.WorldChunk)!.Children.Add(VmfPlacement.MoveSolid(
                    RoomModel.Slab(RoomHarness.Plain, SkyboxLitFixture.Overhang.Mins, SkyboxLitFixture.Overhang.Maxs, 7102), QuarterTurn.Translation(RoomSkyboxHarness.Corner)));
            }

            RoomLightHarness.WorldAlign(library);
            return await SkyboxLitFixture.CompileAsync(library, light);
        }
    }
}
