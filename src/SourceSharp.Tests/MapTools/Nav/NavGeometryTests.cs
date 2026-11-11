//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
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
/// What the navigation reads from a compiled room: the world's brushes are
/// solid, a door's are its obstacle, a trigger's are nothing, a prop is its
/// model's hull, and ladders come from entities too.
/// </summary>
public sealed class NavGeometryTests
{
    private static VmfChunk Entity(string className, int id, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", className);
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    /// <summary>
    /// A room with a door, a trigger, two props (one whose model the content
    /// has, one it lacks) and both ladder entities, compiled; its geometry
    /// splits them as the navigation needs.
    /// </summary>
    [Fact]
    public async Task ACompiledRoomsGeometrySplitsTheWorldFromItsEntities()
    {
        RoomDefinition definition = RoomHarness.WalkableRoom("props", RoomFacing.PositiveX);
        VmfDocument vmf = RoomHarness.BuildRoomModel(definition);
        VmfChunk door = Entity("func_door", 800001, ("targetname", "cxry_gate"));
        door.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(96, 64, 16), new Vec3(112, 192, 176), 800002));
        VmfChunk trigger = Entity("trigger_multiple", 800003);
        trigger.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(160, 64, 16), new Vec3(192, 96, 64), 800004));
        vmf.Chunks.Add(door);
        vmf.Chunks.Add(trigger);
        vmf.Chunks.Add(Entity("prop_physics", 800005, ("model", "models/crate.mdl"), ("origin", "64 64 16"), ("angles", "0 90 0")));
        vmf.Chunks.Add(Entity("prop_physics", 800006, ("model", "models/missing.mdl"), ("origin", "64 192 16")));
        vmf.Chunks.Add(Entity("info_ladder", 800007, ("mins.x", "200"), ("mins.y", "100"), ("mins.z", "16"), ("maxs.x", "216"), ("maxs.y", "140"), ("maxs.z", "200")));
        vmf.Chunks.Add(Entity("func_useableladder", 800008, ("point0", "40 200 20"), ("point1", "40 200 180")));
        RoomObject room = await NavRoomsFixture.CompileAsync(vmf, definition);

        NavGeometry geometry = NavGeometry.FromBsp(room.Bsp, m => m == "models/crate.mdl" ? (new Vec3(-8, -4, 0), new Vec3(8, 4, 20)) : null);

        // The door: its brushes are its obstacle, never world.
        NavObstacleSource gate = geometry.Obstacles.Single(o => o.ClassName == "func_door");
        Assert.Equal(("cxry_gate", Nav3dObstacleKind.Door), (gate.TargetName, gate.Kind));
        Assert.Equal(new Box(new Vec3(96, 64, 16), new Vec3(112, 192, 176)), gate.Bounds);
        Assert.DoesNotContain(geometry.Brushes, b => b.MinX == 96 && b.MaxX == 112 && b.MinY == 64);
        Assert.DoesNotContain(geometry.Brushes, b => b.MinX == 160 && b.MaxX == 192);
        Assert.True(gate.HammerId > 0);

        // The prop turned 90°: its 16 × 8 hull is 8 × 16 at its origin.
        NavObstacleSource crate = geometry.Obstacles.Single(o => o.ClassName == "prop_physics");
        Assert.Equal((Nav3dObstacleKind.Physics, new Box(new Vec3(60, 56, 16), new Vec3(68, 72, 36))), (crate.Kind, crate.Bounds));
        Assert.Contains(geometry.Warnings, w => w.Contains("models/missing.mdl", StringComparison.Ordinal));

        // The ladders: info_ladder's box, and the useable ladder's climb widened by a player's half-width.
        Assert.Contains(new Box(new Vec3(200, 100, 16), new Vec3(216, 140, 200)), geometry.Ladders);
        Assert.Contains(new Box(new Vec3(24, 184, 20), new Vec3(56, 216, 180)), geometry.Ladders);

        // Without a hull source, props are left out, each with a warning naming its model.
        NavGeometry plain = NavGeometry.FromBsp(room.Bsp);
        Assert.Single(plain.Obstacles);
        Assert.Contains(plain.Warnings, w => w.Contains("models/crate.mdl", StringComparison.Ordinal));

        // The world's brushes: the room's own that its tree holds, less the door's and the trigger's;
        // the doorway's sill and lintel, inside the floor's and the ceiling's brushes, are in no leaf.
        Assert.Equal(NavBrush.FromBsp(room.Bsp).Count - 4, geometry.Brushes.Count);
    }

    /// <summary>
    /// A <c>func_viscluster</c> and a <c>func_occluder</c> leave their brushes in the lump (the
    /// compile writes every map brush, as stock's EmitBrushes does) but in no model's tree, and
    /// nothing collides with them: they are not the world's. Taken for it, ctf_2fort's viscluster
    /// boxes and occluder planes walled its doorways and the sides of its bridge.
    /// </summary>
    [Fact]
    public async Task AVisclustersAndAnOccludersBrushesAreNotTheWorlds()
    {
        RoomDefinition definition = RoomHarness.WalkableRoom("vis", RoomFacing.PositiveX);
        VmfDocument vmf = RoomHarness.BuildRoomModel(definition);
        VmfChunk cluster = Entity("func_viscluster", 800021);
        cluster.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(96, 64, 16), new Vec3(112, 192, 176), 800022));
        VmfChunk occluder = Entity("func_occluder", 800023, ("StartActive", "1"));
        occluder.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(160, 64, 16), new Vec3(176, 192, 176), 800024));
        vmf.Chunks.Add(cluster);
        vmf.Chunks.Add(occluder);
        RoomObject room = await NavRoomsFixture.CompileAsync(vmf, definition);

        NavGeometry geometry = NavGeometry.FromBsp(room.Bsp);

        // The stimulus: both slabs are in the compiled lump.
        List<NavBrush> lump = NavBrush.FromBsp(room.Bsp);
        Assert.Contains(lump, b => b.MinX == 96 && b.MaxX == 112 && b.MinY == 64);
        Assert.Contains(lump, b => b.MinX == 160 && b.MaxX == 176 && b.MinY == 64);
        Assert.DoesNotContain(geometry.Brushes, b => b.MinX == 96 && b.MaxX == 112 && b.MinY == 64);
        Assert.DoesNotContain(geometry.Brushes, b => b.MinX == 160 && b.MaxX == 176 && b.MinY == 64);
        // The control: the world's own brushes stay, the floor and the plug in the room's doorway among them.
        Assert.Contains(geometry.Brushes, b => (b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ) == (0, 0, 0, 256, 256, 16));
        Assert.Contains(geometry.Brushes, b => (b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ) == (240, 80, 16, 256, 176, 240));
    }

    /// <summary>
    /// A brush entity with an <c>origin</c> (a rotating door's origin brush
    /// gives it one) is compiled with its brushes relative to that origin,
    /// as the game places its model there: its obstacle is where the door
    /// stands in the map, not around the map's origin. One without an origin
    /// is where its brushes are.
    /// </summary>
    [Fact]
    public async Task ABrushEntityWithAnOriginIsItsObstacleWhereItStands()
    {
        RoomDefinition definition = RoomHarness.WalkableRoom("doors", RoomFacing.PositiveX);
        VmfDocument vmf = RoomHarness.BuildRoomModel(definition);
        VmfChunk turning = Entity("func_door_rotating", 800011, ("targetname", "turning"), ("origin", "104 128 96"));
        turning.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(96, 64, 16), new Vec3(112, 192, 176), 800012));
        VmfChunk sliding = Entity("func_door", 800013, ("targetname", "sliding"));
        sliding.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(160, 64, 16), new Vec3(176, 192, 176), 800014));
        vmf.Chunks.Add(turning);
        vmf.Chunks.Add(sliding);
        RoomObject room = await NavRoomsFixture.CompileAsync(vmf, definition);

        NavGeometry geometry = NavGeometry.FromBsp(room.Bsp);

        Assert.Contains(EntityLump.Parse(room.Bsp[BspLump.Entities]), e => e.ClassName == "func_door_rotating" && e.Get("origin") == "104 128 96");
        Assert.Equal(new Box(new Vec3(96, 64, 16), new Vec3(112, 192, 176)), geometry.Obstacles.Single(o => o.TargetName == "turning").Bounds);
        Assert.Equal(new Box(new Vec3(160, 64, 16), new Vec3(176, 192, 176)), geometry.Obstacles.Single(o => o.TargetName == "sliding").Bounds);
    }

    /// <summary>
    /// A map whose leaf lump is version 0 (each leaf 56 bytes, its ambient
    /// cube inline, as stock BSP version 19 maps store them) splits its
    /// brushes between the world and its entities exactly as the same map
    /// with version 1 leaves does. Read as version 1, the door's brushes
    /// were no longer found and the world lost brushes.
    /// </summary>
    [Fact]
    public async Task AVersionZeroLeafLumpSplitsTheBrushesAsVersionOneDoes()
    {
        RoomDefinition definition = RoomHarness.WalkableRoom("leafs", RoomFacing.PositiveX);
        VmfDocument vmf = RoomHarness.BuildRoomModel(definition);
        VmfChunk door = Entity("func_door", 800001, ("targetname", "gate"));
        door.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(96, 64, 16), new Vec3(112, 192, 176), 800002));
        vmf.Chunks.Add(door);
        RoomObject room = await NavRoomsFixture.CompileAsync(vmf, definition);
        NavGeometry current = NavGeometry.FromBsp(room.Bsp);
        Assert.Equal(1, room.Bsp[BspLump.Leafs].Version);

        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(room.Bsp[BspLump.Leafs]);
        DLeafVersion0[] old = new DLeafVersion0[leafs.Length];
        for (int i = 0; i < leafs.Length; i++)
        {
            DLeaf l = leafs[i];
            old[i] = new DLeafVersion0
            {
                Contents = l.Contents,
                Cluster = l.Cluster,
                AreaFlags = l.AreaFlags,
                Mins = l.Mins,
                Maxs = l.Maxs,
                FirstLeafFace = l.FirstLeafFace,
                NumLeafFaces = l.NumLeafFaces,
                FirstLeafBrush = l.FirstLeafBrush,
                NumLeafBrushes = l.NumLeafBrushes,
                LeafWaterDataId = l.LeafWaterDataId,
            };
        }

        BspLumpData lump = BspStructView.ToLump<DLeafVersion0>(old, 0);
        room.Bsp.SetLump(BspLump.Leafs, lump.Data, 0);
        NavGeometry fromOld = NavGeometry.FromBsp(room.Bsp);

        Assert.Equal(
            current.Brushes.Select(b => (b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ, b.Contents)),
            fromOld.Brushes.Select(b => (b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ, b.Contents)));
        Assert.Equal(new Box(new Vec3(96, 64, 16), new Vec3(112, 192, 176)), Assert.Single(fromOld.Obstacles).Bounds);
    }

    /// <summary>
    /// A displacement is no brush in the compiled map (its brush is
    /// discarded), so it is read from the displacement lumps: one solid per
    /// triangle, its top the surface, extruded straight down the given depth,
    /// none of them an overhang; a triangle tagged removed adds none.
    /// </summary>
    [Fact]
    public async Task ADisplacementIsATriangleShellOfSolidsWithFlatBottoms()
    {
        RoomDefinition definition = RoomHarness.WalkableRoom("terrain", RoomFacing.PositiveX);
        VmfDocument vmf = RoomHarness.BuildRoomModel(definition);
        Box patch = new(new Vec3(64, 64, 16), new Vec3(128, 176, 24));
        vmf.GetChunk(MapFileLoader.WorldChunk)!.Children.Add(
            RoomDisplacementHarness.Patch(RoomDisplacementHarness.PatchBrush, patch, power: 2, offsets: false, heights: (_, _) => 8));
        RoomObject room = await NavRoomsFixture.CompileAsync(vmf, definition);
        Assert.DoesNotContain(NavGeometry.FromBsp(room.Bsp).Brushes, b => b.MinX == 64 && b.MaxX == 128 && b.MinY == 64 && b.MaxY == 176);

        IReadOnlyList<NavBrush> shell = NavGeometry.Displacements(room.Bsp, 16);

        // Power 2: 4 x 4 quads, two triangles each; the surface at 24 + 8.
        Assert.Equal(32, shell.Count);
        Assert.All(shell, b =>
        {
            Assert.False(b.IsOverhang);
            Assert.Equal(32, b.MaxZ, 3);
            Assert.Equal(16, b.MinZ, 3);
            Assert.InRange(b.MinX, 64 - 0.01, 128);
            Assert.InRange(b.MaxY, 64, 176 + 0.01);
            Assert.True((b.Contents & 1) != 0);
        });
        Assert.Equal(64 * 112, shell.Sum(b => (b.MaxX - b.MinX) * (b.MaxY - b.MinY)) / 2, 1);

        DispInfo info = BspStructView.As<DispInfo>(room.Bsp[BspLump.DispInfo])[0];
        byte[] tags = room.Bsp[BspLump.DispTris].Data.ToArray();
        tags[info.DispTriStart * 2] |= (byte)DispTriTags.Remove;
        room.Bsp.SetLump(BspLump.DispTris, tags, room.Bsp[BspLump.DispTris].Version);
        Assert.Equal(31, NavGeometry.Displacements(room.Bsp, 16).Count);
    }

    /// <summary>
    /// Three displacements' solids made on four threads are the solids made
    /// on one, in displacement order; and a displacement that cannot be read
    /// throws as itself, as it did one at a time.
    /// </summary>
    [Fact]
    public async Task DisplacementsMadeOnManyThreadsAreTheSameInOrder()
    {
        RoomDefinition definition = RoomHarness.WalkableRoom("terrain", RoomFacing.PositiveX);
        VmfDocument vmf = RoomHarness.BuildRoomModel(definition);
        foreach ((float y, int height) in new[] { (32f, 8), (96f, 4), (160f, 12) })
        {
            vmf.GetChunk(MapFileLoader.WorldChunk)!.Children.Add(RoomDisplacementHarness.Patch(
                RoomDisplacementHarness.PatchBrush, new Box(new Vec3(64, y, 16), new Vec3(128, y + 48, 24)), power: 2, offsets: false,
                heights: (_, _) => height));
        }

        RoomObject room = await NavRoomsFixture.CompileAsync(vmf, definition);

        IReadOnlyList<NavBrush> one = NavGeometry.Displacements(room.Bsp, 16);
        IReadOnlyList<NavBrush> four = NavGeometry.Displacements(room.Bsp, 16, 4);

        Assert.Equal(96, one.Count);
        Assert.Equal(one.Select(b => b.PlaneFloats()), four.Select(b => b.PlaneFloats()));
        Assert.Equal(3, one.Chunk(32).Select(c => c.Max(b => b.MaxZ)).Distinct().Count());

        // The second displacement's base face names a plane past the lump.
        DispInfo info = BspStructView.As<DispInfo>(room.Bsp[BspLump.DispInfo])[1];
        byte[] faces = room.Bsp[BspLump.Faces].Data.ToArray();
        Span<DFace> faceSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, DFace>(faces.AsSpan());
        faceSpan[info.MapFace].PlaneNum = ushort.MaxValue;
        room.Bsp.SetLump(BspLump.Faces, faces, room.Bsp[BspLump.Faces].Version);
        Assert.Throws<IndexOutOfRangeException>(() => NavGeometry.Displacements(room.Bsp, 16, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavGeometry.Displacements(room.Bsp, 16, 0));
    }

    /// <summary>
    /// A triangle facing up is extruded straight down (flat bottom, vertical
    /// sides); a wall triangle along its normal, backwards; and the side the
    /// base face faces is the front whichever way the triangle is wound.
    /// </summary>
    [Fact]
    public void APrismIsExtrudedAwayFromItsFront()
    {
        Vec3 up = new(0, 0, 1);
        NavBrush floor = NavGeometry.Prism(new Vec3(0, 0, 10), new Vec3(32, 0, 18), new Vec3(0, 32, 10), up, 16, 1)!;
        Assert.Equal((0d, 0d, -6d, 32d, 32d, 18d), (floor.MinX, floor.MinY, floor.MinZ, floor.MaxX, floor.MaxY, Math.Round(floor.MaxZ, 3)));
        Assert.False(floor.IsOverhang);
        Assert.False(floor.IsAxial);
        Assert.Equal(5, floor.PlaneCount);

        NavBrush wound = NavGeometry.Prism(new Vec3(0, 0, 10), new Vec3(0, 32, 10), new Vec3(32, 0, 18), up, 16, 1)!;
        Assert.Equal((floor.MinZ, Math.Round(floor.MaxZ, 3)), (wound.MinZ, Math.Round(wound.MaxZ, 3)));

        NavBrush wall = NavGeometry.Prism(new Vec3(0, 0, 0), new Vec3(0, 32, 0), new Vec3(0, 0, 32), new Vec3(1, 0, 0), 16, 1)!;
        Assert.Equal((-16d, 0d, 0d, 0d, 32d, 32d), (Math.Round(wall.MinX, 3), wall.MinY, wall.MinZ, Math.Round(wall.MaxX, 3), wall.MaxY, wall.MaxZ));

        NavBrush ceiling = NavGeometry.Prism(new Vec3(0, 0, 64), new Vec3(32, 0, 64), new Vec3(0, 32, 64), new Vec3(0, 0, -1), 16, 1)!;
        Assert.Equal((64d, 80d), (Math.Round(ceiling.MinZ, 3), Math.Round(ceiling.MaxZ, 3)));

        Assert.Null(NavGeometry.Prism(new Vec3(0, 0, 0), new Vec3(1, 1, 1), new Vec3(2, 2, 2), up, 16, 1));
    }

    /// <summary>
    /// A static prop whose model has collision pieces is one box per piece,
    /// turned by its angles, so the gap between two posts stays open; a
    /// model without pieces falls back to its hull.
    /// </summary>
    [Fact]
    public void AStaticPropWithCollisionPiecesIsABoxPerPiece()
    {
        StaticPropLump lump = new();
        lump.ModelNames.AddRange(["models/gate.mdl", "models/crate.mdl"]);
        lump.Props.Add(new StaticProp { PropType = 0, Origin = new Vec3(100, 200, 16), Angles = new Vec3(0, 90, 0), Solid = 6 });
        lump.Props.Add(new StaticProp { PropType = 1, Origin = new Vec3(0, 0, 16), Solid = 6 });
        BspData bsp = new();
        bsp.GameLumps.Add(lump.Write());
        NavPiece[] posts = [NavPiece.Box(new Vec3(-24, -4, 0), new Vec3(-16, 4, 64)), NavPiece.Box(new Vec3(16, -4, 0), new Vec3(24, 4, 64))];

        (IReadOnlyList<NavBrush> boxes, IReadOnlyList<string> warnings) = NavGeometry.StaticProps(
            bsp, _ => (new Vec3(-24, -4, 0), new Vec3(24, 4, 64)), m => m == "models/gate.mdl" ? posts : null);

        Assert.Empty(warnings);
        Assert.Equal(
            [(96d, 176d, 16d, 104d, 184d, 80d), (96d, 216d, 16d, 104d, 224d, 80d), (-24d, -4d, 16d, 24d, 4d, 80d)],
            boxes.Select(b => (b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ)));
    }

    /// <summary>
    /// A static prop's piece is solid up to its own top, not its box's: a
    /// wedge rising from 0 at x -40 to 48 at x 40 holds the point under its
    /// slope and not the point over its low end that its box would, and has
    /// no downward sloped face (so the file lists no brush for it). Turned
    /// a quarter turn about z, it rises along y instead. A piece that is a
    /// box keeps the axial box brush.
    /// </summary>
    [Fact]
    public void AStaticPropPieceIsSolidUpToItsOwnTop()
    {
        StaticPropLump lump = new();
        lump.ModelNames.AddRange(["models/wedge.mdl", "models/crate.mdl"]);
        lump.Props.Add(new StaticProp { PropType = 0, Origin = new Vec3(0, 0, 16), Solid = 6 });
        lump.Props.Add(new StaticProp { PropType = 0, Origin = new Vec3(500, 0, 16), Angles = new Vec3(0, 90, 0), Solid = 6 });
        lump.Props.Add(new StaticProp { PropType = 1, Origin = new Vec3(0, 300, 16), Angles = new Vec3(0, 180, 0), Solid = 6 });
        BspData bsp = new();
        bsp.GameLumps.Add(lump.Write());

        (IReadOnlyList<NavBrush> solids, _) = NavGeometry.StaticProps(bsp, null, m => m == "models/wedge.mdl" ? [Wedge] : [NavPiece.Box(new Vec3(-8, -4, 0), new Vec3(8, 4, 20))]);

        Assert.Equal(3, solids.Count);
        NavBrush wedge = solids[0];
        Assert.False(wedge.IsOverhang);
        Assert.True(wedge.Overlaps(new NavBox(-1, -1, 16, 1, 1, 39)));
        Assert.False(wedge.Overlaps(new NavBox(-1, -1, 41, 1, 1, 60)));
        Assert.False(wedge.Overlaps(new NavBox(-39, -1, 30, -37, 1, 60)));
        Assert.True(wedge.Overlaps(new NavBox(37, -1, 30, 39, 1, 60)));

        NavBrush turned = solids[1];
        Assert.False(turned.Overlaps(new NavBox(499, -39, 30, 501, -37, 60)));
        Assert.True(turned.Overlaps(new NavBox(499, 37, 30, 501, 39, 60)));

        Assert.True(solids[2].IsAxial);
        Assert.Equal((-8d, 296d, 16d, 8d, 304d, 36d), (solids[2].MinX, solids[2].MinY, solids[2].MinZ, solids[2].MaxX, solids[2].MaxY, solids[2].MaxZ));
    }

    /// <summary>A wedge 80 long, 64 wide, rising from 0 at x -40 to 48 at x 40.</summary>
    internal static NavPiece Wedge => new(
        [new(-40, -32, 0), new(40, -32, 0), new(-40, 32, 0), new(40, 32, 0), new(40, -32, 48), new(40, 32, 48)],
        [0, 1, 3, 0, 3, 2, 0, 4, 1, 2, 3, 5, 1, 4, 5, 1, 5, 3, 0, 2, 5, 0, 5, 4]);

    /// <summary>
    /// Static props are world solids, each its model's hull turned by its
    /// angles; a prop that is not solid adds nothing, and a model the content
    /// lacks is one warning counting its props.
    /// </summary>
    [Fact]
    public void StaticPropsAreTheirHullsTurnedAndAMissingModelIsOneWarning()
    {
        StaticPropLump lump = new();
        lump.ModelNames.AddRange(["models/crate.mdl", "models/missing.mdl", "models/ghost.mdl"]);
        lump.Props.Add(new StaticProp { PropType = 0, Origin = new Vec3(64, 64, 16), Angles = new Vec3(0, 90, 0), Solid = 6 });
        lump.Props.Add(new StaticProp { PropType = 1, Origin = new Vec3(0, 0, 0), Solid = 6 });
        lump.Props.Add(new StaticProp { PropType = 1, Origin = new Vec3(8, 0, 0), Solid = 2 });
        lump.Props.Add(new StaticProp { PropType = 2, Origin = new Vec3(0, 0, 0), Solid = 0 });
        BspData bsp = new();
        bsp.GameLumps.Add(lump.Write());

        (IReadOnlyList<NavBrush> boxes, IReadOnlyList<string> warnings) = NavGeometry.StaticProps(
            bsp, m => m == "models/crate.mdl" || m == "models/ghost.mdl" ? (new Vec3(-8, -4, 0), new Vec3(8, 4, 20)) : null);

        NavBrush crate = Assert.Single(boxes);
        Assert.Equal((60d, 56d, 16d, 68d, 72d, 36d), (crate.MinX, crate.MinY, crate.MinZ, crate.MaxX, crate.MaxY, crate.MaxZ));
        Assert.True(crate.IsAxial);
        Assert.Equal(NavGeometry.StaticPropContents, crate.Contents);
        Assert.Equal(
            ["static prop model \"models/missing.mdl\" has no hull the content gives, so its 2 solid props are left out of the navigation's solids."],
            warnings);
        Assert.Equal(["models/crate.mdl", "models/missing.mdl"], NavModelBounds.StaticPropModels(bsp));
        Assert.Equal(([], []), (NavGeometry.StaticProps(new BspData(), null).Brushes.ToArray(), NavModelBounds.StaticPropModels(new BspData()).ToArray()));
    }

    [Theory]
    [InlineData("func_door", Nav3dObstacleKind.Door)]
    [InlineData("FUNC_DOOR_ROTATING", Nav3dObstacleKind.Door)]
    [InlineData("prop_door_rotating", Nav3dObstacleKind.Door)]
    [InlineData("func_movelinear", Nav3dObstacleKind.Mover)]
    [InlineData("func_tracktrain", Nav3dObstacleKind.Mover)]
    [InlineData("func_brush", Nav3dObstacleKind.Toggle)]
    [InlineData("func_wall_toggle", Nav3dObstacleKind.Toggle)]
    [InlineData("func_breakable", Nav3dObstacleKind.Breakable)]
    [InlineData("func_breakable_surf", Nav3dObstacleKind.Breakable)]
    [InlineData("func_physbox", Nav3dObstacleKind.Physics)]
    [InlineData("prop_physics_multiplayer", Nav3dObstacleKind.Physics)]
    [InlineData("prop_dynamic_override", Nav3dObstacleKind.Prop)]
    public void DynamicClassesAreKnownByKind(string className, Nav3dObstacleKind kind) => Assert.Equal(kind, NavGeometry.KindOf(className));

    [Theory]
    [InlineData("func_detail")]
    [InlineData("trigger_multiple")]
    [InlineData("prop_static")]
    [InlineData("info_player_start")]
    public void OtherClassesAreNotObstacles(string className) => Assert.Null(NavGeometry.KindOf(className));

    [Fact]
    public void LadderEntitiesReadEitherSpellingAndAMalformedOneIsNone()
    {
        BspEntity vectors = new();
        vectors.Pairs.Add(new BspKeyValue("mins", "10 20 30"));
        vectors.Pairs.Add(new BspKeyValue("maxs", "0 40 50"));
        Assert.Equal(new Box(new Vec3(0, 20, 30), new Vec3(10, 40, 50)), NavGeometry.LadderBox(vectors));

        BspEntity half = new();
        half.Pairs.Add(new BspKeyValue("mins.x", "1"));
        Assert.Null(NavGeometry.LadderBox(half));
        Assert.Null(NavGeometry.UseableLadder(half));
    }

    [Theory]
    [InlineData("1 -2.5 3e1", true, 1f, -2.5f, 30f)]
    [InlineData("  4   5 6 ", true, 4f, 5f, 6f)]
    [InlineData("[1 2 3]", false, 0f, 0f, 0f)]
    [InlineData("1 2", false, 0f, 0f, 0f)]
    [InlineData("1 2 3 4", false, 0f, 0f, 0f)]
    [InlineData("1 x 3", false, 0f, 0f, 0f)]
    [InlineData(null, false, 0f, 0f, 0f)]
    public void AnEntityKeysVectorIsExactlyThreeNumbers(string? text, bool parses, float x, float y, float z)
    {
        Assert.Equal(parses, NavGeometry.TryVector(text, out Vec3 v));
        Assert.Equal(new Vec3(x, y, z), v);
    }

    [Fact]
    public void APropsBoxTurnsExactlyAtQuarterTurnsAndBoundsItsCornersOtherwise()
    {
        (Vec3, Vec3) hull = (new Vec3(-10, -2, 0), new Vec3(10, 2, 30));
        BspEntity prop = new();
        prop.Pairs.Add(new BspKeyValue("origin", "100 100 0"));
        prop.Pairs.Add(new BspKeyValue("angles", "0 180 0"));
        Assert.Equal(new Box(new Vec3(90, 98, 0), new Vec3(110, 102, 30)), NavGeometry.PropBox(prop, hull));
        prop.Pairs[1] = new BspKeyValue("angles", "0 45 0");
        Box turned = NavGeometry.PropBox(prop, hull)!.Value;
        Assert.Equal(12 / MathF.Sqrt(2), turned.Maxs.X - 100, 3);
        prop.Pairs[1] = new BspKeyValue("angles", "90 0 0");
        Assert.Equal(new Box(new Vec3(100, 98, -10), new Vec3(130, 102, 10)), NavGeometry.PropBox(prop, hull));
        BspEntity nowhere = new();
        Assert.Null(NavGeometry.PropBox(nowhere, hull));
    }

    [Fact]
    public void GeometryTakesMoreBrushesAndNamesItsOverhangsInOrder()
    {
        float r = MathF.Sqrt(0.5f);
        NavBrush overhang = NavBrush.FromPlanes([
            (new Vec3(1, 0, 0), 20f), (new Vec3(-1, 0, 0), 0f), (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f),
            (new Vec3(0, 0, 1), 30f), (new Vec3(r, 0, -r), -10f * r)], 1)!;
        NavBrush water = NavBrush.FromPlanes([
            (new Vec3(1, 0, 0), 20f), (new Vec3(-1, 0, 0), 0f), (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f),
            (new Vec3(0, 0, 1), 30f), (new Vec3(r, 0, -r), -10f * r)], 0x20)!;
        NavGeometry geometry = new NavGeometry { Brushes = [water, overhang] }.With([NavBrush.Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1), 1)]);
        Assert.Equal(3, geometry.Brushes.Count);
        Assert.Equal([overhang], NavClearanceBuilder.OverhangBrushes(geometry));
    }
}
