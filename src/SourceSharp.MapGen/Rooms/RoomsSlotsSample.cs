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
/// The door slots sample (<c>samples/rooms-slots</c>; the rooms design,
/// 17.8 stage 9): a library whose kit has three door slots per cell edge,
/// and a level that uses them. The kit is the 3 x 3 sample's 256-unit cell
/// with walls 8 deep and doors 64 wide and 240 high, so three fit an edge
/// with a wall depth of wall at each corner (3 x (64 + 2 x 8) = 240 of 256),
/// at 11, 96 and 181 along it, each wide enough for the navigation grid's
/// 16-unit voxels to pass a 32-unit player (a door must be at least the
/// agent and a voxel wide where the grid meets it).
/// </summary>
/// <remarks>
/// <para>
/// The rooms: <c>twin</c>, two doors on its east wall (its south and north
/// slots) and one in the middle of its west wall; <c>corridor</c>, a door at
/// the south slot of its east and west walls, so it enters its neighbours
/// at a corner; <c>corner</c>, a door at the first slot of every wall;
/// <c>three</c>, a door at every slot of its east wall and at the west slot
/// of its north and south walls; and <c>ring</c>, 2 x 2, a door at every
/// slot of every cell edge.
/// </para>
/// <para>
/// The level, north row first: the ring in the north-west with a corner
/// room turned a half beside its east wall; the three-slot room and the
/// two-door room turned a half (its east doors face west, slot for slot
/// reversed) in the middle row, the three-slot room's middle east slot
/// capped; the corridor between two corner rooms along the south slots in
/// the south row. Two rooms join only at the same place on their shared
/// wall; every other slot is capped.
/// </para>
/// </remarks>
public static class RoomsSlotsSample
{
    /// <summary>The sample's folder, repository-relative.</summary>
    public const string Folder = "samples/rooms-slots";

    /// <summary>The level's name.</summary>
    public const string LevelName = "slots";

    /// <summary>The kit's door width: three fit a 256 edge with a wall's depth at each corner.</summary>
    public const float DoorWidth = 64f;

    /// <summary>The kit's wall depth.</summary>
    public const float Wall = 8f;

    /// <summary>The kit's door height: the room's interior, floor to ceiling.</summary>
    public const float DoorHeight = Rooms3x3Kit.CellSize - (2 * Wall);

    /// <summary>The kit's door slots per cell edge.</summary>
    public const int Slots = 3;

    /// <summary>The game name the sample's <c>gameinfo.txt</c> gives.</summary>
    public const string GameInfo =
        "\"GameInfo\"\n{\n\tgame\t\"Rooms door slots sample\"\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n"
        + "\t\t\tgame\t|gameinfo_path|.\n\t\t}\n\t}\n}\n";

    /// <summary>The kit.</summary>
    public static SocketKit Kit => new(DoorWidth, DoorHeight, Wall) { Slots = Slots };

    /// <summary>One room of the sample.</summary>
    /// <param name="Name">Its name.</param>
    /// <param name="CellsX">Cells along its +x.</param>
    /// <param name="CellsY">Cells along its +y.</param>
    /// <param name="Sockets">Its doors: (face, cell offset along it, slot).</param>
    public sealed record SlotRoom(string Name, int CellsX, int CellsY, IReadOnlyList<(RoomFacing Face, int Offset, int Slot)> Sockets)
    {
        /// <summary>Its footprint's extent along +x.</summary>
        public float SpanX => CellsX * Rooms3x3Kit.CellSize;

        /// <summary>Its footprint's extent along +y.</summary>
        public float SpanY => CellsY * Rooms3x3Kit.CellSize;
    }

    /// <summary>The library's rooms, in library order.</summary>
    public static IReadOnlyList<SlotRoom> Rooms { get; } =
    [
        new("twin", 1, 1, [(RoomFacing.PositiveX, 0, 0), (RoomFacing.PositiveX, 0, 2), (RoomFacing.NegativeX, 0, 1)]),
        new("corridor", 1, 1, [(RoomFacing.PositiveX, 0, 0), (RoomFacing.NegativeX, 0, 0)]),
        new("corner", 1, 1, [(RoomFacing.PositiveX, 0, 0), (RoomFacing.NegativeX, 0, 0), (RoomFacing.PositiveY, 0, 0), (RoomFacing.NegativeY, 0, 0)]),
        new("three", 1, 1, [(RoomFacing.PositiveX, 0, 0), (RoomFacing.PositiveX, 0, 1), (RoomFacing.PositiveX, 0, 2), (RoomFacing.PositiveY, 0, 0), (RoomFacing.NegativeY, 0, 0)]),
        new("ring", 2, 2, [.. Enum.GetValues<RoomFacing>().SelectMany(f => Enumerable.Range(0, 2 * Slots).Select(k => (f, k / Slots, k % Slots)))]),
    ];

    /// <summary>The level, north row first (see the class).</summary>
    public static IReadOnlyList<IReadOnlyList<LevelCell?>> LevelRows { get; } =
    [
        [Covered(), Covered(), null],
        [new("ring", 0), Covered(), new("corner", 2)],
        [new("three", 0), new("twin", 2), null],
        [new("corner", 0), new("corridor", 0), new("corner", 0)],
    ];

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
        files[$"levels/{LevelName}.yaml"] = Encoding.UTF8.GetBytes(LevelYaml.Write(level,
        [
            "the rooms door slots sample level",
            "the grid's first line is the north row; each line runs west to east; + is a cell the room to its south-west covers",
        ]));
        return files;
    }

    /// <summary>The library VMF: each room's shell, plugs and lamp in its own cells along +x, its <c>info_room</c> at its low corner.</summary>
    /// <returns>The VMF's text.</returns>
    public static string LibraryVmf()
    {
        VmfMap map = new();
        float x = 0;
        foreach (SlotRoom room in Rooms)
        {
            Point corner = new(x, 0, 0);
            x += room.SpanX + Rooms3x3Kit.LibraryGap;
            map.WorldSolids.AddRange(Shell(room, corner));
            if (room.Name == "corridor")
            {
                // A player start, so a preview's reach has somewhere to walk from.
                VmfEntity start = new() { ClassName = "info_player_start" };
                start.Set("origin", (corner + new Point(128, 43, 9)).ToString());
                start.Set("angles", "0 0 0");
                map.Entities.Add(start);
            }

            VmfEntity light = new() { ClassName = "light" };
            light.Set("origin", (corner + new Point(room.SpanX / 2, room.SpanY / 2, Rooms3x3Kit.CellSize - 48)).ToString());
            light.Set("_light", "255 240 220 200");
            map.Entities.Add(light);

            VmfEntity marker = new() { ClassName = RoomLibraryVmf.RoomEntity };
            marker.Set("origin", corner.ToString());
            marker.Set(RoomLibraryVmf.NameKey, room.Name);
            marker.Set(RoomLibraryVmf.CellSizeKey, Number(Rooms3x3Kit.CellSize));
            marker.Set(RoomLibraryVmf.DoorWidthKey, Number(DoorWidth));
            marker.Set(RoomLibraryVmf.DoorHeightKey, Number(DoorHeight));
            marker.Set(RoomLibraryVmf.WallDepthKey, Number(Wall));
            marker.Set(RoomLibraryVmf.DoorSlotsKey, Slots.ToString(CultureInfo.InvariantCulture));
            if (room.CellsX != 1 || room.CellsY != 1)
            {
                marker.Set(RoomLibraryVmf.RoomFootprintKey, string.Create(CultureInfo.InvariantCulture, $"{room.CellsX} {room.CellsY}"));
            }

            map.Entities.Add(marker);
        }

        return map.Write();
    }

    /// <summary>
    /// A room's shell at a corner: floor and ceiling over the footprint, its
    /// four walls cut by the kit's door at each socket's slot (the strips
    /// between doors full height, the door as tall as the interior), and the
    /// kit's plug in each door.
    /// </summary>
    private static List<VmfSolid> Shell(SlotRoom room, Point at)
    {
        float wall = Wall, top = Rooms3x3Kit.CellSize, cell = Rooms3x3Kit.CellSize;
        List<VmfSolid> solids =
        [
            VmfMap.Box(at, at + new Point(room.SpanX, room.SpanY, wall), Rooms3x3Kit.FloorMaterial),
            VmfMap.Box(at + new Point(0, 0, top - wall), at + new Point(room.SpanX, room.SpanY, top), Rooms3x3Kit.CeilingMaterial),
        ];
        SocketKit kit = Kit;
        foreach (RoomFacing face in (ReadOnlySpan<RoomFacing>)[RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY])
        {
            bool alongY = face is RoomFacing.PositiveX or RoomFacing.NegativeX;
            float length = alongY ? room.SpanY : room.SpanX;
            List<float> doors = [.. room.Sockets.Where(s => s.Face == face).Select(s => (s.Offset * cell) + kit.SlotLow(cell, s.Slot)).Order()];
            float from = 0;
            foreach (float a0 in doors)
            {
                float a1 = a0 + DoorWidth;
                solids.Add(Slab(face, room, at, from, a0, wall, top - wall, Rooms3x3Kit.WallMaterial));
                solids.Add(Slab(face, room, at, a0, a1, wall, wall + DoorHeight, Rooms3x3Kit.PlugMaterial));
                from = a1;
            }

            solids.Add(Slab(face, room, at, from, length, wall, top - wall, Rooms3x3Kit.WallMaterial));
        }

        return [.. solids.Where(s => s is not null)];
    }

    /// <summary>One slab of a wall: <c>a</c> along the face from its low end, <c>b</c> up, the wall's depth inside the footprint; null when empty.</summary>
    private static VmfSolid Slab(RoomFacing face, SlotRoom room, Point at, float a0, float a1, float b0, float b1, string material)
    {
        if (a1 <= a0 || b1 <= b0)
        {
            return null!;
        }

        float w = Wall;
        (Point mins, Point maxs) = face switch
        {
            RoomFacing.PositiveX => (new Point(room.SpanX - w, a0, b0), new Point(room.SpanX, a1, b1)),
            RoomFacing.NegativeX => (new Point(0, a0, b0), new Point(w, a1, b1)),
            RoomFacing.PositiveY => (new Point(a0, room.SpanY - w, b0), new Point(a1, room.SpanY, b1)),
            _ => (new Point(a0, 0, b0), new Point(a1, w, b1)),
        };
        return VmfMap.Box(at + mins, at + maxs, material);
    }

    private static LevelCell Covered() => new(LevelCell.CoveredMark, 0);

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);
}
