//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>What capping a socket does to one voxel of its room: the voxel's key with the door shut.</summary>
/// <param name="Voxel">The voxel's index in the cell, x fastest.</param>
/// <param name="Key">Its key with the socket capped; its records index the room's table.</param>
public readonly record struct NavCapChange(int Voxel, NavVoxelKey Key);

/// <summary>One socket of a room: what capping it changes, and where its door point stands.</summary>
/// <param name="Capped">The voxels whose key changes when the socket is capped, in voxel order.</param>
/// <param name="DoorPoint">The door's point, room-local: on the cell face, at the opening's middle, on the doorway's floor.</param>
public sealed record RoomNavSocket(IReadOnlyList<NavCapChange> Capped, Vec3 DoorPoint);

/// <summary>A dynamic obstacle of a room, as the compile found it, in the room's coordinates at its turn.</summary>
/// <param name="ClassName">Its classname.</param>
/// <param name="TargetName">Its <c>targetname</c> as written (room-local names unresolved), or null.</param>
/// <param name="HammerId">Its <c>hammerid</c>, or -1.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Bounds">Its bounds.</param>
public sealed record RoomNavObstacle(string ClassName, string? TargetName, int HammerId, Nav3dObstacleKind Kind, Box Bounds);

/// <summary>A point of interest an author placed in a room, in the room's own coordinates at one turn.</summary>
/// <param name="Position">Its position, room-local (the room is <c>[0, cell]² × [0, height]</c>).</param>
/// <param name="Yaw">Its facing in degrees, 0 to 360, counter-clockwise from +x.</param>
/// <param name="HasFacing">Whether the author gave it a facing.</param>
/// <param name="Radius">Its radius, 0 for none.</param>
/// <param name="Type">Its type.</param>
/// <param name="Tags">Its tags as written.</param>
/// <param name="Name">Its name, possibly with a room-local prefix (<see cref="RoomLocalNames"/>), or null.</param>
/// <param name="AgentMask">The presets it applies to, bit per preset.</param>
/// <param name="EntityId">The <c>info_poi</c>'s id in the library, for messages.</param>
public sealed record RoomNavPoi(
    Vec3 Position, float Yaw, bool HasFacing, float Radius, string Type, string Tags, string? Name, uint AgentMask, string EntityId)
{
    /// <summary>Whether the point is an arrival (<see cref="RoomPois.ArrivalType"/>).</summary>
    public bool IsArrival => Type == RoomPois.ArrivalType;
}

/// <summary>
/// A room's navigation at one turn, as <c>ssmap room</c> precomputes it and
/// the link stitches it: the settings it was built with, the room's role,
/// sockets and points of interest, and its clearance grid as voxel columns,
/// with what capping each socket changes.
/// </summary>
/// <remarks>
/// <para>
/// Everything is in the cell's own voxels and coordinates at
/// <see cref="Turn"/> quarter turns, so the link places it by adding the
/// cell's offset, never by resampling. Every door is open in the grid; a
/// capped door is its socket's <see cref="RoomNavSocket.Capped"/> keys
/// applied. Opening is the one state a room cannot know alone (it needs the
/// neighbour's door), so it is the state stored, and closing is the
/// precomputed difference.
/// </para>
/// <para>
/// <b>Turning is exact.</b> A clearance record describes a voxel by
/// horizontal Chebyshev gaps and heights, and a quarter turn maps a square
/// footprint and its four gaps onto themselves, so the records do not
/// change: turning moves columns (<c>(x, y) → (n − 1 − y, x)</c>), capped
/// voxels, door points, points of interest and obstacles' bounds, and turns
/// the overhanging brushes' planes.
/// </para>
/// </remarks>
public sealed record RoomNav
{
    /// <summary>The cell's edge.</summary>
    public required float CellSize { get; init; }

    /// <summary>The voxel's edge.</summary>
    public required float VoxelSize { get; init; }

    /// <summary>Voxels along the cell's edge.</summary>
    public required int CellVoxels { get; init; }

    /// <summary>
    /// Voxels up the room's columns: its height in voxels (the rooms design,
    /// 17.6), <see cref="CellVoxels"/> for a room that is a cube, which is
    /// what an unset value reads as.
    /// </summary>
    /// <remarks>
    /// A room taller than its cell has taller columns and one lower than it
    /// shorter ones; the footprint is the cell's either way, so turning and
    /// stitching are unchanged. A room whose columns differ from its edge is
    /// stored at <see cref="RoomNavSection.ShapedRevision"/>, and a level
    /// placing one is a version 3 <c>.nav3d</c>.
    /// </remarks>
    public int ColumnVoxels
    {
        get => _columnVoxels ?? CellVoxels;
        init => _columnVoxels = value;
    }

    /// <summary>
    /// The cells the grid covers along x at <see cref="Turn"/>: 1 for a room
    /// of one cell; for a room larger than one cell (the rooms design, 17.8)
    /// its footprint as turned, so a quarter turn swaps it with
    /// <see cref="CellsY"/>. The grid is one over the footprint,
    /// <see cref="CellVoxels"/> to a cell.
    /// </summary>
    public int CellsX { get; init; } = 1;

    /// <summary>The cells the grid covers along y at <see cref="Turn"/> (<see cref="CellsX"/>).</summary>
    public int CellsY { get; init; } = 1;

    /// <summary>Whether the grid covers more than one cell: a larger room's navigation.</summary>
    public bool IsLarge => CellsX != 1 || CellsY != 1;

    /// <summary>Voxels along x of the whole grid.</summary>
    public int SizeX => CellsX * CellVoxels;

    /// <summary>Voxels along y of the whole grid.</summary>
    public int SizeY => CellsY * CellVoxels;

    /// <summary>Whether the room's columns are not its cell's edge: a shaped room's navigation.</summary>
    public bool IsShaped => ColumnVoxels != CellVoxels;

    private readonly int? _columnVoxels;

    /// <summary>The floor threshold it was built with.</summary>
    public required float FloorNormalZ { get; init; }

    /// <summary>The step height of its library.</summary>
    public float StepHeight { get; init; } = NavSettings.DefaultStepHeight;

    /// <summary>The jump height of its library.</summary>
    public float JumpHeight { get; init; } = NavSettings.DefaultJumpHeight;

    /// <summary>The jump distance of its library.</summary>
    public float JumpDistance { get; init; } = NavSettings.DefaultJumpDistance;

    /// <summary>The water cost multiplier it was built with.</summary>
    public float WaterCost { get; init; } = NavSettings.DefaultWaterCost;

    /// <summary>The ladder cost multiplier it was built with.</summary>
    public float LadderCost { get; init; } = NavSettings.DefaultLadderCost;

    /// <summary>The quarter turns this copy is at, 0 to 3.</summary>
    public required int Turn { get; init; }

    /// <summary>The room's transition role.</summary>
    public required RoomRole Role { get; init; }

    /// <summary>The library's agent presets.</summary>
    public required IReadOnlyList<NavAgentSpec> Agents { get; init; }

    /// <summary>The room's sockets, in its socket order, with their facing at turn 0.</summary>
    public required IReadOnlyList<RoomSocket> Sockets { get; init; }

    /// <summary>The authored points of interest at this turn.</summary>
    public required IReadOnlyList<RoomNavPoi> Pois { get; init; }

    /// <summary>The clearance records the grid's keys index; record 0 blocks everything.</summary>
    public required IReadOnlyList<byte[]> Records { get; init; }

    /// <summary>The cell's voxel columns with every door open.</summary>
    public required NavColumns Columns { get; init; }

    /// <summary>Per socket, in socket order: what capping it changes and its door point.</summary>
    public required IReadOnlyList<RoomNavSocket> SocketData { get; init; }

    /// <summary>The room's dynamic obstacles, which records name by position.</summary>
    public IReadOnlyList<RoomNavObstacle> Obstacles { get; init; } = [];

    /// <summary>The room's overhanging brushes' planes (four floats each), which records name by position.</summary>
    public IReadOnlyList<float[]> Brushes { get; init; } = [];

    /// <summary>What the build could not read (a prop whose model the content lacks); not stored in the pack.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// This navigation turned by more quarter turns: columns, capped voxels,
    /// door points, points, obstacles and brushes turned; the records as they
    /// are. Exact: a permutation of voxels and a quarter turn of coordinates.
    /// </summary>
    /// <param name="quarterTurns">Further counter-clockwise quarter turns.</param>
    /// <returns>The navigation at <c>(Turn + quarterTurns) mod 4</c>.</returns>
    public RoomNav Turned(int quarterTurns)
    {
        int r = ((quarterTurns % 4) + 4) % 4;
        if (r == 0)
        {
            return this;
        }

        // A larger room's grid is a rectangle (17.8): a quarter turn swaps
        // its sides, and the room moves back by its footprint, as its
        // placement's transform does. A grid of one cell is the square it was.
        int sx = SizeX, sy = SizeY;
        (int tsx, int tsy) = (r & 1) == 0 ? (sx, sy) : (sy, sx);
        List<NavRun>[] columns = new List<NavRun>[sx * sy];
        for (int y = 0; y < sy; y++)
        {
            for (int x = 0; x < sx; x++)
            {
                (int tx, int ty) = TurnColumn(x, y, sx, sy, r);
                columns[(ty * tsx) + tx] = [.. Columns.Column(x, y).ToArray()];
            }
        }

        int[] starts = new int[(sx * sy) + 1];
        List<NavRun> runs = [];
        for (int c = 0; c < sx * sy; c++)
        {
            starts[c] = runs.Count;
            runs.AddRange(columns[c]);
        }

        starts[^1] = runs.Count;
        RoomTransform turn = new(new RoomPlacement("turn", 0, 0, r) { CellsX = CellsX, CellsY = CellsY }, CellSize);
        float spanX = CellsX == 1 ? CellSize : CellsX * CellSize, spanY = CellsY == 1 ? CellSize : CellsY * CellSize;
        return this with
        {
            Turn = (Turn + r) % 4,
            CellsX = (r & 1) == 0 ? CellsX : CellsY,
            CellsY = (r & 1) == 0 ? CellsY : CellsX,
            Columns = new NavColumns(tsx, tsy, starts, [.. runs]),
            SocketData = [.. SocketData.Select(s => new RoomNavSocket(
                [.. s.Capped.Select(c => c with { Voxel = TurnVoxel(c.Voxel, sx, sy, r) }).OrderBy(c => c.Voxel)],
                turn.Apply(s.DoorPoint)))],
            Pois = [.. Pois.Select(p => p with { Position = turn.Apply(p.Position), Yaw = TurnYaw(p.Yaw, r) })],
            Obstacles = [.. Obstacles.Select(o => o with { Bounds = TurnBox(o.Bounds, turn) })],
            Brushes = [.. Brushes.Select(b => (IsLarge
                ? NavBrush.FromPlaneFloats(b, 1)!.Turned(r, spanX, spanY)
                : NavBrush.FromPlaneFloats(b, 1)!.Turned(r, CellSize)).PlaneFloats())],
        };
    }

    /// <summary>A yaw turned by quarter turns, kept in <c>[0, 360)</c>.</summary>
    /// <param name="yaw">Degrees.</param>
    /// <param name="quarterTurns">Counter-clockwise quarter turns.</param>
    /// <returns>The turned yaw.</returns>
    public static float TurnYaw(float yaw, int quarterTurns)
    {
        float turned = yaw + (90f * (((quarterTurns % 4) + 4) % 4));
        while (turned >= 360f)
        {
            turned -= 360f;
        }

        return turned;
    }

    /// <summary>Where a voxel column goes under quarter turns: <c>(x, y) → (n − 1 − y, x)</c> each turn.</summary>
    /// <param name="x">The column along x.</param>
    /// <param name="y">Along y.</param>
    /// <param name="n">Voxels along the cell's edge.</param>
    /// <param name="quarterTurns">Counter-clockwise quarter turns.</param>
    /// <returns>The turned column.</returns>
    public static (int X, int Y) TurnColumn(int x, int y, int n, int quarterTurns) => TurnColumn(x, y, n, n, quarterTurns);

    /// <summary>
    /// Where a column of a grid of <paramref name="sizeX"/> by
    /// <paramref name="sizeY"/> goes under quarter turns: <c>(x, y) → (sizeY − 1 − y, x)</c>
    /// each turn, the sides swapping as the grid turns.
    /// </summary>
    /// <param name="x">The column along x.</param>
    /// <param name="y">Along y.</param>
    /// <param name="sizeX">The grid's voxels along x before the turns.</param>
    /// <param name="sizeY">Along y.</param>
    /// <param name="quarterTurns">Counter-clockwise quarter turns.</param>
    /// <returns>The turned column.</returns>
    public static (int X, int Y) TurnColumn(int x, int y, int sizeX, int sizeY, int quarterTurns)
    {
        int r = ((quarterTurns % 4) + 4) % 4;
        for (int t = 0; t < r; t++)
        {
            (x, y) = (sizeY - 1 - y, x);
            (sizeX, sizeY) = (sizeY, sizeX);
        }

        return (x, y);
    }

    /// <summary>A voxel index turned by quarter turns.</summary>
    /// <param name="voxel">The index, x fastest.</param>
    /// <param name="n">Voxels along the cell's edge.</param>
    /// <param name="quarterTurns">Counter-clockwise quarter turns.</param>
    /// <returns>The turned index.</returns>
    public static int TurnVoxel(int voxel, int n, int quarterTurns) => TurnVoxel(voxel, n, n, quarterTurns);

    /// <summary>A voxel index of a grid of <paramref name="sizeX"/> by <paramref name="sizeY"/> turned by quarter turns.</summary>
    /// <param name="voxel">The index, x fastest.</param>
    /// <param name="sizeX">The grid's voxels along x before the turns.</param>
    /// <param name="sizeY">Along y.</param>
    /// <param name="quarterTurns">Counter-clockwise quarter turns.</param>
    /// <returns>The turned index, in the turned grid.</returns>
    public static int TurnVoxel(int voxel, int sizeX, int sizeY, int quarterTurns)
    {
        int x = voxel % sizeX;
        int y = voxel / sizeX % sizeY;
        int z = voxel / (sizeX * sizeY);
        (int tx, int ty) = TurnColumn(x, y, sizeX, sizeY, quarterTurns);
        int turnedX = (quarterTurns & 1) == 0 ? sizeX : sizeY, turnedY = (quarterTurns & 1) == 0 ? sizeY : sizeX;
        return (((z * turnedY) + ty) * turnedX) + tx;
    }

    private static Box TurnBox(Box box, RoomTransform turn)
    {
        Vec3 a = turn.Apply(box.Mins);
        Vec3 b = turn.Apply(box.Maxs);
        return new Box(
            new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
            new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
    }
}
