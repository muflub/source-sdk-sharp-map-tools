//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapGen.Catalog;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapGen.Rooms;

/// <summary>
/// The multi-cell sample: a room library on the 3x3 kit whose rooms cover
/// more than one cell (the rooms design, 17.8), and a level placing them
/// among rooms of one cell, written at its four whole turns, as files under
/// <c>samples/rooms-multicell/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rooms.</b> Every room is a shell of the kit's slabs: a floor and a
/// ceiling over its whole footprint, walls around its perimeter cut by the
/// kit's door at each socket's cell edge, and the kit's trigger plug in
/// each door. Inside, each has a light and what makes its footprint matter:
/// </para>
/// <list type="bullet">
/// <item><c>hub</c>: one cell, four doors, the rooms of one cell the level joins the others with.</item>
/// <item><c>hall</c>: 2 x 2, doors at <c>east0</c>, <c>west1</c>, <c>north0</c> and <c>south1</c>, a pillar standing across both internal cell faces.</item>
/// <item><c>court</c>: 3 x 3, a door at the middle edge of each side, a low wall across an internal cell face, and the level's player start.</item>
/// <item><c>gallery</c>: 1 x 2, doors at each end and at the east side's north cell.</item>
/// <item><c>atrium</c>: 2 x 2 and two cells tall (<c>room_height 512</c>), a
/// displacement crossing the internal cell face x = 256 and a pool across
/// the internal face y = 256, neither touching a door.</item>
/// </list>
/// <para>
/// <b>Navigation</b> is built with the library's defaults: one grid over
/// each larger room's footprint, written into the level's file cell by cell.
/// </para>
/// <para>
/// <b>The level</b> is written by hand: the court in the south-west, the
/// hall east of it through a hub, the gallery north of the hall and the
/// atrium beside it, every room reached through a door. The turned files
/// are the level turned whole (<see cref="Turned"/>), each larger room
/// written again in its new south-west cell with <c>+</c> in the others, so
/// every room is linked at every quarter turn. Deterministic: the same
/// bytes on every run and host.
/// </para>
/// </remarks>
public static class RoomsMultiCellSample
{
    /// <summary>The sample's folder, repository-relative.</summary>
    public const string Folder = "samples/rooms-multicell";

    /// <summary>The level's base name.</summary>
    public const string LevelName = "multicell";

    /// <summary>The atrium's height: two cells.</summary>
    public const float AtriumHeight = 512f;

    /// <summary>The atrium's pool surface, room-local z: 48 above the floor's top.</summary>
    public const float PoolLevel = 64f;

    /// <summary>The game name the sample's <c>gameinfo.txt</c> gives.</summary>
    public const string GameInfo =
        "\"GameInfo\"\n{\n\tgame\t\"Rooms multi-cell sample\"\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n"
        + "\t\t\tgame\t|gameinfo_path|.\n\t\t}\n\t}\n}\n";

    /// <summary>One room of the sample.</summary>
    /// <param name="Name">Its name.</param>
    /// <param name="CellsX">Cells along its +x.</param>
    /// <param name="CellsY">Cells along its +y.</param>
    /// <param name="Height">Its height.</param>
    /// <param name="Sockets">Its doors: (face, cell offset along it).</param>
    /// <param name="Extra">Its brushes beyond the shell, built at the corner given.</param>
    public sealed record MultiRoom(
        string Name, int CellsX, int CellsY, float Height, IReadOnlyList<(RoomFacing Face, int Offset)> Sockets, Func<Point, IReadOnlyList<VmfSolid>> Extra)
    {
        /// <summary>Its footprint's extent along +x.</summary>
        public float SpanX => CellsX * Rooms3x3Kit.CellSize;

        /// <summary>Its footprint's extent along +y.</summary>
        public float SpanY => CellsY * Rooms3x3Kit.CellSize;
    }

    /// <summary>The library's rooms, in library order.</summary>
    public static IReadOnlyList<MultiRoom> Rooms { get; } =
    [
        new("hub", 1, 1, Rooms3x3Kit.CellSize,
            [(RoomFacing.PositiveX, 0), (RoomFacing.NegativeX, 0), (RoomFacing.PositiveY, 0), (RoomFacing.NegativeY, 0)], _ => []),
        new("hall", 2, 2, Rooms3x3Kit.CellSize,
            [(RoomFacing.PositiveX, 0), (RoomFacing.NegativeX, 1), (RoomFacing.PositiveY, 0), (RoomFacing.NegativeY, 1)],
            at => [VmfMap.Box(at + new Point(224, 224, 16), at + new Point(288, 288, 240), Rooms3x3Kit.BlockMaterial)]),
        new("court", 3, 3, Rooms3x3Kit.CellSize,
            [(RoomFacing.PositiveX, 1), (RoomFacing.NegativeX, 1), (RoomFacing.PositiveY, 1), (RoomFacing.NegativeY, 1)],
            at => [VmfMap.Box(at + new Point(224, 448, 16), at + new Point(320, 464, 80), Rooms3x3Kit.BlockMaterial)]),
        new("gallery", 1, 2, Rooms3x3Kit.CellSize,
            [(RoomFacing.PositiveX, 1), (RoomFacing.PositiveY, 0), (RoomFacing.NegativeY, 0)], _ => []),
        new("atrium", 2, 2, AtriumHeight,
            [(RoomFacing.PositiveX, 0), (RoomFacing.NegativeX, 0), (RoomFacing.PositiveY, 1), (RoomFacing.NegativeY, 1)],
            at =>
            [
                RoomsFeaturesSample.Patch(new Bounds(at + new Point(192, 48, 16), at + new Point(320, 176, 24)), 3, RoomsFeaturesSample.RidgeHeight),
                VmfMap.Box(at + new Point(280, 200, 16), at + new Point(460, 320, PoolLevel), RoomsFeaturesSample.WaterMaterial),
            ]),
    ];

    /// <summary>
    /// The level, north row first: the court (3 x 3) in the south-west, a hub
    /// east of it into the hall (2 x 2), a hub north of the court, the
    /// gallery (1 x 2) north of the hall, the atrium (2 x 2) east of the
    /// gallery reached from the hubs on the east side.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<LevelCell?>> LevelRows { get; } =
    [
        [null, null, null, null, Cell("hub"), null, Cell("hub")],
        [null, Cell("hub"), null, null, Covered(), Covered(), Covered()],
        [Covered(), Covered(), Covered(), null, Cell("gallery"), Cell("atrium"), Covered()],
        [Covered(), Covered(), Covered(), Cell("hub"), Covered(), Covered(), Cell("hub")],
        [Cell("court"), Covered(), Covered(), null, Cell("hall"), Covered(), Cell("hub")],
    ];

    /// <summary>A level file's name for the level turned <paramref name="turns"/> quarter turns.</summary>
    /// <param name="turns">0 to 3.</param>
    /// <returns>The name.</returns>
    public static string TurnName(int turns) =>
        turns == 0 ? LevelName : string.Create(CultureInfo.InvariantCulture, $"{LevelName}_turn{turns}");

    /// <summary>Every file of the sample, by path under the sample folder.</summary>
    /// <returns>The files.</returns>
    public static IReadOnlyDictionary<string, byte[]> Build()
    {
        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["gameinfo.txt"] = Encoding.UTF8.GetBytes(GameInfo),
            [Rooms3x3Kit.LibraryFile] = Encoding.UTF8.GetBytes(LibraryVmf()),
        };

        foreach ((string path, string vmt) in RoomsFeaturesSample.Materials())
        {
            files[path] = Encoding.UTF8.GetBytes(vmt);
        }

        LevelGrid level = RoomsSampleLevels.FromRows(LevelName, Rooms3x3Sample.LibraryFromLevels, LevelRows);
        for (int turns = 0; turns < 4; turns++)
        {
            string name = TurnName(turns);
            files[$"levels/{name}.yaml"] = Encoding.UTF8.GetBytes(LevelYaml.Write(RoomsSampleLevels.Renamed(level, name),
            [
                turns == 0
                    ? "the rooms multi-cell sample level"
                    : string.Create(CultureInfo.InvariantCulture, $"the rooms multi-cell sample level turned {turns * 90} degrees"),
                "the grid's first line is the north row; each line runs west to east; + is a cell the room to its south-west covers",
            ]));
            level = Turned(level);
        }

        return files;
    }

    /// <summary>
    /// A level turned a quarter turn counter-clockwise, whole: every room in
    /// its turned cells a quarter further on, a larger room written again in
    /// the south-west cell of its turned footprint with <c>+</c> in the rest.
    /// </summary>
    /// <param name="level">The level, its larger rooms the sample's.</param>
    /// <returns>The turned level.</returns>
    public static LevelGrid Turned(LevelGrid level)
    {
        ArgumentNullException.ThrowIfNull(level);
        int rows = level.Rows, columns = level.Columns;
        LevelCell?[] cells = new LevelCell?[rows * columns];
        foreach ((int x, int y, LevelCell cell) in level.Placed)
        {
            MultiRoom room = Rooms.Single(r => r.Name == cell.Room);
            RoomPlacement placement = new(cell.Room, x, y, cell.Rotation) { CellsX = room.CellsX, CellsY = room.CellsY };
            List<(int X, int Y)> turned = [.. placement.Covered().Select(c => (rows - 1 - c.Y, c.X))];
            (int ax, int ay) = (turned.Min(c => c.X), turned.Min(c => c.Y));
            foreach ((int tx, int ty) in turned)
            {
                cells[(ty * rows) + tx] = (tx, ty) == (ax, ay)
                    ? cell with { Rotation = (cell.Rotation + 1) % 4 }
                    : new LevelCell(LevelCell.CoveredMark, 0);
            }
        }

        return new LevelGrid(level.Name, level.Library, columns, rows, cells) { Libraries = level.Libraries, Aliases = level.Aliases };
    }

    /// <summary>The library VMF: each room's shell, plugs and brushes in its own cells along +x, its <c>info_room</c> at its low corner.</summary>
    /// <returns>The VMF's text.</returns>
    public static string LibraryVmf()
    {
        VmfMap map = new();
        float x = 0;
        foreach (MultiRoom room in Rooms)
        {
            Point corner = new(x, 0, 0);
            x += room.SpanX + Rooms3x3Kit.LibraryGap;
            map.WorldSolids.AddRange(Shell(room, corner));
            map.WorldSolids.AddRange(room.Extra(corner));

            if (room.Name == "court")
            {
                // A player start, so a preview's reach (ssmap preview
                // -movement) has somewhere to walk from.
                VmfEntity start = new() { ClassName = "info_player_start" };
                start.Set("origin", (corner + new Point(128, 128, 17)).ToString());
                start.Set("angles", "0 45 0");
                map.Entities.Add(start);
            }

            VmfEntity light = new() { ClassName = "light" };
            light.Set("origin", (corner + new Point(room.SpanX / 2, room.SpanY / 2 - 64, room.Height - 48)).ToString());
            light.Set("_light", "255 240 220 200");
            map.Entities.Add(light);

            VmfEntity marker = new() { ClassName = RoomLibraryVmf.RoomEntity };
            marker.Set("origin", corner.ToString());
            marker.Set(RoomLibraryVmf.NameKey, room.Name);
            marker.Set(RoomLibraryVmf.CellSizeKey, Number(Rooms3x3Kit.CellSize));
            marker.Set(RoomLibraryVmf.DoorWidthKey, Number(Rooms3x3Kit.DoorWidth));
            marker.Set(RoomLibraryVmf.DoorHeightKey, Number(Rooms3x3Kit.DoorHeight));
            marker.Set(RoomLibraryVmf.WallDepthKey, Number(Rooms3x3Kit.Wall));
            if (room.CellsX != 1 || room.CellsY != 1)
            {
                marker.Set(RoomLibraryVmf.RoomFootprintKey, string.Create(CultureInfo.InvariantCulture, $"{room.CellsX} {room.CellsY}"));
            }

            if (room.Height != Rooms3x3Kit.CellSize)
            {
                marker.Set(RoomLibraryVmf.RoomHeightKey, Number(room.Height));
            }

            map.Entities.Add(marker);
        }

        return map.Write();
    }

    /// <summary>
    /// A room's shell at a corner: the floor and the ceiling over its whole
    /// footprint, its four walls cut by the kit's door at each socket's cell
    /// edge (the strips beside the doors full height, a lintel over each),
    /// and the kit's plug in each door.
    /// </summary>
    private static List<VmfSolid> Shell(MultiRoom room, Point at)
    {
        float wall = Rooms3x3Kit.Wall, top = room.Height;
        List<VmfSolid> solids =
        [
            VmfMap.Box(at, at + new Point(room.SpanX, room.SpanY, wall), Rooms3x3Kit.FloorMaterial),
            VmfMap.Box(at + new Point(0, 0, top - wall), at + new Point(room.SpanX, room.SpanY, top), Rooms3x3Kit.CeilingMaterial),
        ];
        foreach (RoomFacing face in (ReadOnlySpan<RoomFacing>)[RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY])
        {
            bool alongY = face is RoomFacing.PositiveX or RoomFacing.NegativeX;
            float length = alongY ? room.SpanY : room.SpanX;
            List<int> doors = [.. room.Sockets.Where(s => s.Face == face).Select(s => s.Offset).Order()];
            float from = 0;
            foreach (int door in doors)
            {
                float a0 = (door * Rooms3x3Kit.CellSize) + Rooms3x3Kit.DoorLow, a1 = (door * Rooms3x3Kit.CellSize) + Rooms3x3Kit.DoorHigh;
                solids.Add(Slab(face, room, at, from, a0, wall, top - wall, Rooms3x3Kit.WallMaterial));
                solids.Add(Slab(face, room, at, a0, a1, wall + Rooms3x3Kit.DoorHeight, top - wall, Rooms3x3Kit.WallMaterial));
                solids.Add(Slab(face, room, at, a0, a1, wall, wall + Rooms3x3Kit.DoorHeight, Rooms3x3Kit.PlugMaterial));
                from = a1;
            }

            solids.Add(Slab(face, room, at, from, length, wall, top - wall, Rooms3x3Kit.WallMaterial));
        }

        // A lintel of no height (a door as tall as the room's interior) is no brush.
        return [.. solids.Where(s => s is not null)];
    }

    /// <summary>One slab of a wall: <c>a</c> along the face from its low end, <c>b</c> up, the wall's depth inside the footprint; null when empty.</summary>
    private static VmfSolid Slab(RoomFacing face, MultiRoom room, Point at, float a0, float a1, float b0, float b1, string material)
    {
        if (a1 <= a0 || b1 <= b0)
        {
            return null!;
        }

        float w = Rooms3x3Kit.Wall;
        (Point mins, Point maxs) = face switch
        {
            RoomFacing.PositiveX => (new Point(room.SpanX - w, a0, b0), new Point(room.SpanX, a1, b1)),
            RoomFacing.NegativeX => (new Point(0, a0, b0), new Point(w, a1, b1)),
            RoomFacing.PositiveY => (new Point(a0, room.SpanY - w, b0), new Point(a1, room.SpanY, b1)),
            _ => (new Point(a0, 0, b0), new Point(a1, w, b1)),
        };
        return VmfMap.Box(at + mins, at + maxs, material);
    }

    private static LevelCell Cell(string room, int rotation = 0) => new(room, rotation);

    private static LevelCell Covered() => new(LevelCell.CoveredMark, 0);

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);
}
