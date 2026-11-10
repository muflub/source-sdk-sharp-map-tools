//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The floor check: every place a player stands in a built navigation has
/// ground under it in the map's own collision, so a navigation whose solid
/// was larger than the map's is caught where it puts a floor over nothing.
/// </summary>
public sealed class NavFloorCheckTests(BspNavMap map) : IClassFixture<BspNavMap>
{
    /// <summary>The static prop's model in <see cref="BspNavMap"/>: the 16 x 16 x 32 box at (64, 176, 16).</summary>
    private const string Box = RoomPropHarness.BoxModel;

    private Nav3dReader Open() => Nav3dReader.Open(Nav3dWriter.Write(map.Build().Level));

    /// <summary>
    /// The hall's navigation, built from the map's own solids, has ground
    /// under every place a player stands: on the floor, the step, the ledge,
    /// the displacement and the prop's top. The answer is the same at any
    /// thread count.
    /// </summary>
    [Fact]
    public void EveryPlaceToStandInTheHallHasGroundUnderIt()
    {
        Nav3dReader nav = Open();
        NavFloorReport report = NavFloorCheck.Check(nav, map.Bsp, BspNavMap.Hull, null);

        Assert.Empty(report.Missing);
        Assert.Empty(report.Warnings);
        Assert.Equal(18, report.Depth);
        Assert.True(report.Standable > 300, $"{report.Standable} places to stand");
        Assert.Equal(report.Standable, Enumerable.Range(0, nav.LeafCount).Count(l => Nav3dWalk.IsStand(nav, Nav3dWalker.Player, l)));
        NavFloorReport four = NavFloorCheck.Check(nav, map.Bsp, BspNavMap.Hull, null, new NavFloorCheckOptions { MaxDegree = 4 });
        Assert.Equal((report.Standable, 0), (four.Standable, four.Missing.Count));
    }

    /// <summary>
    /// A navigation built with the prop as its 32-high hull, checked against
    /// a map whose prop is only a 4-high slab: the four places on the hull's
    /// top (z 48) have nothing within a step under the player's box, and are
    /// reported; looking 40 units down, the slab and the floor are found.
    /// </summary>
    [Fact]
    public void AFloorOnASolidTheMapDoesNotHaveIsReported()
    {
        Nav3dReader nav = Open();
        NavPiece[] slab = [NavPiece.Box(new Vec3(-8, -8, 0), new Vec3(8, 8, 4))];
        Func<string, IReadOnlyList<NavPiece>?> pieces = m => m == Box ? slab : null;

        NavFloorReport report = NavFloorCheck.Check(nav, map.Bsp, BspNavMap.Hull, pieces);

        Assert.Equal(4, report.Missing.Count);
        Assert.All(report.Missing, f =>
        {
            Assert.Equal(48f, f.Position.Z);
            Assert.InRange(f.Position.X, 48, 80);
            Assert.InRange(f.Position.Y, 160, 192);
            Assert.True(Nav3dWalk.IsStand(nav, Nav3dWalker.Player, f.Leaf));
        });
        Assert.Equal(report.Missing.Select(f => f.Leaf).Order(), report.Missing.Select(f => f.Leaf));
        Assert.Equal(report.Missing, NavFloorCheck.Check(nav, map.Bsp, BspNavMap.Hull, pieces, new NavFloorCheckOptions { MaxDegree = 4 }).Missing);
        Assert.Empty(NavFloorCheck.Check(nav, map.Bsp, BspNavMap.Hull, pieces, new NavFloorCheckOptions { Depth = 40 }).Missing);
    }

    /// <summary>
    /// A heap: a static prop whose collision is a 32 x 32 plateau 48 high on
    /// a 160 x 160 base, in a hall of its own. Built from the box around the
    /// heap, the navigation put floors at 48 over the heap's foot, where a
    /// player's box would drop 30 to the slope; built from the heap's own
    /// top, every place to stand has ground under it, the plateau stays a
    /// place to stand at 48, and a floor on the slope is the slope's height.
    /// </summary>
    [Fact]
    public async Task APropsHeapIsAFloorAtItsOwnHeight()
    {
        VmfDocument vmf = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        vmf.Chunks.Add(world);
        int id = 100;
        void Solid(Vec3 mins, Vec3 maxs) => world.Children.Add(RoomModel.Slab(RoomHarness.Plain, mins, maxs, id++));
        Solid(new Vec3(0, 0, 0), new Vec3(512, 512, 16));
        Solid(new Vec3(0, 0, 240), new Vec3(512, 512, 256));
        Solid(new Vec3(0, 0, 16), new Vec3(16, 512, 240));
        Solid(new Vec3(496, 0, 16), new Vec3(512, 512, 240));
        Solid(new Vec3(16, 0, 16), new Vec3(496, 16, 240));
        Solid(new Vec3(16, 496, 16), new Vec3(496, 512, 240));
        vmf.Chunks.Add(RoomPropHarness.Prop(900, Box, new Vec3(256, 256, 16), "0 0 0", ("solid", "6")));
        byte[] bytes = await BspNavMap.CompileAsync(vmf);
        BspData bsp = await BspFile.LoadAsync(new MemoryStream(bytes, writable: false));

        NavPiece heap = new(
            [new(-80, -80, 0), new(80, -80, 0), new(-80, 80, 0), new(80, 80, 0), new(-16, -16, 48), new(16, -16, 48), new(-16, 16, 48), new(16, 16, 48)],
            [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3]);
        Func<string, IReadOnlyList<NavPiece>?> pieces = m => m == Box ? [heap] : null;
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(
            LevelNavFromBsp.Build(bsp, Nav3dBspBinding.Of(bytes), BspNavMap.Hull, new BspNavOptions { ModelPieces = pieces }).Level));

        NavFloorReport report = NavFloorCheck.Check(nav, bsp, BspNavMap.Hull, pieces);
        Assert.Empty(report.Missing);
        Assert.True(report.Standable > 300, $"{report.Standable} places to stand");

        int plateau = nav.FindLeaf(new Vec3(248, 248, 72));
        Assert.Equal(64f, nav.Leaf(plateau).FloorZ(Nav3dClipClass.Player));
        Assert.True(Nav3dWalk.IsStand(nav, Nav3dWalker.Player, plateau));

        // The slope falls 48 over 64 from the plateau's edge at x 272: at the
        // column x 288..304 its highest point is 16 + 48 - 12 = 52.
        int slope = nav.FindLeaf(new Vec3(296, 256, 70));
        Assert.Equal(52f, nav.Leaf(slope).FloorZ(Nav3dClipClass.Player), 0.01f);
    }

    /// <summary>
    /// The collision is the world's brushes of the agent's class (player clip
    /// for the player, not for an NPC), the displacement's surface, and each
    /// solid static prop's pieces placed, or its hull; a prop whose model the
    /// content lacks is left out with one warning.
    /// </summary>
    [Fact]
    public void TheCollisionIsTheClassesBrushesTheDisplacementAndTheProps()
    {
        NavPiece wedge = new(
            [new(-8, -8, 0), new(8, -8, 0), new(-8, 8, 0), new(8, 8, 0), new(8, -8, 32), new(8, 8, 32)],
            [0, 1, 3, 0, 3, 2, 0, 4, 1, 2, 3, 5, 1, 4, 5, 1, 5, 3, 0, 2, 5, 0, 5, 4]);
        (IReadOnlyList<NavBrush> player, IReadOnlyList<string> none) = NavFloorCheck.Collision(map.Bsp, BspNavMap.Hull, m => m == Box ? [wedge] : null, Nav3dClipClass.Player);
        (IReadOnlyList<NavBrush> npc, _) = NavFloorCheck.Collision(map.Bsp, BspNavMap.Hull, null, Nav3dClipClass.Npc);
        (IReadOnlyList<NavBrush> bare, IReadOnlyList<string> warnings) = NavFloorCheck.Collision(map.Bsp, null, null, Nav3dClipClass.Player);

        Assert.Empty(none);
        static bool Clip(NavBrush b) => (b.Contents & 0x10000) != 0 && b.MinX == 300 && b.MaxX == 364;
        Assert.Contains(player, Clip);
        Assert.DoesNotContain(npc, Clip);
        Assert.Contains(player, b => b.MinY >= 400 && b.MaxY <= 496 && Math.Abs(b.MaxZ - BspNavMap.PatchTop) < 0.01);

        // The wedge, placed at (64, 176, 16): its slope runs from 16 at x 56
        // to 48 at x 72, so the point a quarter along is under it and the
        // one above the low end's middle is not.
        static bool AtTheProp(NavBrush b) => b.MinX >= 55 && b.MaxX <= 73 && b.MinY >= 167 && b.MaxY <= 185 && b.MinZ >= 15;
        NavBrush placed = Assert.Single(player, AtTheProp);
        Assert.Equal((56d, 168d, 16d, 72d, 184d, 48d), (Math.Round(placed.MinX, 3), Math.Round(placed.MinY, 3), Math.Round(placed.MinZ, 3), Math.Round(placed.MaxX, 3), Math.Round(placed.MaxY, 3), Math.Round(placed.MaxZ, 3)));
        Assert.True(placed.Overlaps(new NavBox(59, 175, 16, 61, 177, 22)));
        Assert.False(placed.Overlaps(new NavBox(59, 175, 30, 61, 177, 40)));
        Assert.True(Assert.Single(npc, AtTheProp).IsAxial);

        Assert.DoesNotContain(bare, AtTheProp);
        Assert.Equal(["static prop model \"models/props_test/box.mdl\" has no collision the content gives, so its 1 solid prop is left out of the check."], warnings);
    }

    private static bool AtTheProp(NavBrush b) => b.MinX >= 55 && b.MaxX <= 73 && b.MinY >= 167 && b.MaxY <= 185 && b.MinZ >= 15;

    private static readonly NavPiece[] Wedge =
    [
        new([new(-8, -8, 0), new(8, -8, 0), new(-8, 8, 0), new(8, 8, 0), new(8, -8, 32), new(8, 8, 32)],
            [0, 1, 3, 0, 3, 2, 0, 4, 1, 2, 3, 5, 1, 4, 5, 1, 5, 3, 0, 2, 5, 0, 5, 4]),
    ];

    /// <summary>
    /// A prop placed vphysics-solid whose model the content has but with no
    /// <c>.phy</c> is non-solid, as the engine makes it ("bogus solid type"),
    /// and the warning says so; it does not stand in as its hull box. TF2's
    /// mining cart is such a model, and its box once made a spawn look stuck
    /// inside a cart players walk through.
    /// </summary>
    [Fact]
    public void AVphysicsPropWithNoPhyIsNotSolid()
    {
        (IReadOnlyList<NavBrush> solids, IReadOnlyList<string> warnings) = NavFloorCheck.Collision(map.Bsp, BspNavMap.Hull, _ => [], Nav3dClipClass.Player);

        Assert.DoesNotContain(solids, AtTheProp);
        string warning = Assert.Single(warnings);
        Assert.Contains("\"models/props_test/box.mdl\"", warning, StringComparison.Ordinal);
        Assert.Contains("non-solid", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no <c>.phy</c> and no model either, nothing is known of the
    /// prop: it is left out as one the content lacks, not called non-solid.
    /// </summary>
    [Fact]
    public void AVphysicsPropWhoseModelIsMissingIsLacking()
    {
        (IReadOnlyList<NavBrush> solids, IReadOnlyList<string> warnings) = NavFloorCheck.Collision(map.Bsp, null, _ => [], Nav3dClipClass.Player);

        Assert.DoesNotContain(solids, AtTheProp);
        Assert.Contains("has no collision the content gives", Assert.Single(warnings), StringComparison.Ordinal);
    }

    /// <summary>
    /// A prop placed bounding-box solid collides as its hull box even when
    /// its model has a <c>.phy</c>: the engine reads the <c>.phy</c> only for
    /// vphysics. Any other solid type the engine makes non-solid.
    /// </summary>
    [Theory]
    [InlineData("2", true)]
    [InlineData("1", false)]
    public async Task ABoundingBoxPropIsItsHullAndOtherSolidTypesAreNot(string solid, bool boxed)
    {
        BspData bsp = await BspFile.LoadAsync(new MemoryStream(await BspNavMap.CompileAsync(BspNavMap.Vmf(solid)), writable: false));

        (IReadOnlyList<NavBrush> solids, IReadOnlyList<string> warnings) = NavFloorCheck.Collision(bsp, BspNavMap.Hull, _ => Wedge, Nav3dClipClass.Player);

        if (boxed)
        {
            Assert.True(Assert.Single(solids, AtTheProp).IsAxial);
            Assert.Empty(warnings);
        }
        else
        {
            Assert.DoesNotContain(solids, AtTheProp);
            Assert.Contains("non-solid", Assert.Single(warnings), StringComparison.Ordinal);
        }
    }
}
