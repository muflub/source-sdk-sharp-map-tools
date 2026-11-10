//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Rooms;

/// <summary>One placed room of a level grid: which room, and how it is turned.</summary>
/// <param name="Room">The room's library name.</param>
/// <param name="Rotation">Quarter turns counter-clockwise seen from above, 0 to 3.</param>
/// <param name="Line">Where the level file names it, for messages; 0 when it was not read from a file.</param>
/// <param name="Column">The column on that line.</param>
public sealed record LevelCell(string Room, int Rotation, int Line = 0, int Column = 0)
{
    /// <summary>Where the cell was written, as a message prefix (<c>line 3, column 7: </c>), or empty.</summary>
    public string Where => Line > 0
        ? string.Create(CultureInfo.InvariantCulture, $"line {Line}, column {Column}: ")
        : string.Empty;

    /// <summary>
    /// The mark of a cell a larger room covers besides its anchor (the rooms
    /// design, 17.8 and O29): <c>+</c>. Not a room name (names start with a
    /// letter, a digit or <c>_</c>), so a build that knows no larger rooms
    /// refuses a level holding one with its room-name message.
    /// </summary>
    public const string CoveredMark = "+";

    /// <summary>Whether the cell is a <see cref="CoveredMark"/>: part of a larger room placed elsewhere, not a placement.</summary>
    public bool IsCovered => Room == CoveredMark;
}

/// <summary>One library of a level that names several (<c>libraries:</c>): its key and its VMF.</summary>
/// <param name="Key">
/// The key: what a cell writes before a dot to name one of its rooms
/// (<c>base.corner</c>), and the front of every room's name in the linked
/// level. It starts with a letter and holds only letters, digits, <c>_</c>
/// and <c>-</c> (<see cref="LevelLibraries.KeyProblem"/>).
/// </param>
/// <param name="Path">The library VMF, as the level file wrote it (relative to the level file's folder).</param>
/// <param name="Line">Where the level file names it, for messages; 0 when it was not read from a file.</param>
/// <param name="Column">The column on that line.</param>
public sealed record LevelLibrary(string Key, string Path, int Line = 0, int Column = 0)
{
    /// <summary>Where the library was written, as a message prefix, or empty.</summary>
    public string Where => Line > 0
        ? string.Create(CultureInfo.InvariantCulture, $"line {Line}, column {Column}: ")
        : string.Empty;
}

/// <summary>A short name for a room (<c>aliases:</c>): what a cell may write in its place.</summary>
/// <param name="Name">The alias, a name by the room-name rule without a dot.</param>
/// <param name="Value">What it stands for, as written: <c>lib.room</c> or a bare room name, without a turn.</param>
/// <param name="Line">Where the level file gives it, for messages; 0 when it was not read from a file.</param>
/// <param name="Column">The column on that line.</param>
public sealed record LevelAlias(string Name, string Value, int Line = 0, int Column = 0);

/// <summary>
/// A level as a grid: rows and columns of cells, each holding one room of
/// the library, turned, or nothing.
/// </summary>
/// <remarks>
/// <para>
/// This is what a level file says (<see cref="LevelYaml"/>) and what the
/// seeded generator makes (<see cref="LevelGenerator"/>). Cell <c>(x, y)</c>
/// is column <c>x</c> from the west and row <c>y</c> from the south; a room
/// there stands in the world box <c>[x·cell, (x+1)·cell] × [y·cell,
/// (y+1)·cell]</c>, turned about its cell's centre.
/// </para>
/// <para>
/// <b>Joints are implicit.</b> <see cref="ToLayout"/> joins two sockets
/// exactly when they face each other across a shared wall: the room on one
/// side has a socket on that wall, turned, and so does the room on the
/// other. Every other socket — facing an empty cell, the grid's edge, or a
/// neighbour's plain wall — is capped, its plug kept as a wall. There is no
/// way to name a joint or a cap by hand, so a level cannot disagree with its
/// own geometry.
/// </para>
/// </remarks>
public sealed class LevelGrid
{
    private readonly LevelCell?[] _cells;

    /// <summary>A grid from its cells.</summary>
    /// <param name="name">The level's name; the linked map's base name.</param>
    /// <param name="library">The room library VMF the level's rooms come from, as the level file wrote it.</param>
    /// <param name="rows">How many rows, south to north.</param>
    /// <param name="columns">How many columns, west to east.</param>
    /// <param name="cells">The cells, row by row from the south-west, x fastest; null where there is no room.</param>
    /// <exception cref="ArgumentException">The cell count is not rows × columns, or a rotation is not 0 to 3.</exception>
    public LevelGrid(string name, string library, int rows, int columns, IReadOnlyList<LevelCell?> cells)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        if (cells.Count != (long)rows * columns)
        {
            throw new ArgumentException($"a {rows}x{columns} grid has {rows * columns} cells, not {cells.Count}", nameof(cells));
        }

        foreach (LevelCell? cell in cells)
        {
            if (cell is not null && (uint)cell.Rotation > 3)
            {
                throw new ArgumentException($"rotation {cell.Rotation} is not a quarter-turn count (0 to 3)", nameof(cells));
            }
        }

        Name = name;
        Library = library;
        Rows = rows;
        Columns = columns;
        _cells = [.. cells];
    }

    /// <summary>The level's name.</summary>
    public string Name { get; }

    /// <summary>
    /// The room library the level's rooms come from, as written; for a level
    /// that names several (<see cref="Libraries"/>), the first one's path,
    /// the library whose singletons the level takes.
    /// </summary>
    public string Library { get; }

    /// <summary>
    /// The libraries a level file names with <c>libraries:</c>, in the order
    /// written (the first supplies the singletons, the rooms design's 17.4),
    /// or null for a level that names one with <c>library:</c>, as every
    /// level file written before several libraries did.
    /// </summary>
    /// <remarks>
    /// Null and a list of one are different levels on purpose: a
    /// <c>library:</c> level's cells are bare room names and it links and
    /// writes exactly as it always did, while a <c>libraries:</c> level's
    /// cells resolve to qualified names (<see cref="LevelLibraries.Resolve"/>)
    /// even when it lists one library.
    /// </remarks>
    public IReadOnlyList<LevelLibrary>? Libraries { get; init; }

    /// <summary>The level file's <c>aliases:</c>, in the order written; empty when it has none.</summary>
    public IReadOnlyList<LevelAlias> Aliases { get; init; } = [];

    /// <summary>The row count.</summary>
    public int Rows { get; }

    /// <summary>The column count.</summary>
    public int Columns { get; }

    /// <summary>The cells, row by row from the south-west, x fastest.</summary>
    public IReadOnlyList<LevelCell?> Cells => _cells;

    /// <summary>
    /// What the level file says about its transitions (<c>up</c>,
    /// <c>down</c>, <c>up_map</c>, <c>down_map</c>, <c>spawn</c>,
    /// <c>spawn_count</c>), or null when it says nothing of them, as every
    /// level file written before transitions.
    /// </summary>
    public LevelTransitions? Transitions { get; init; }

    /// <summary>The same level with other transition settings (the level file's <c>up</c>, <c>down</c>, map and spawn keys).</summary>
    /// <param name="transitions">The settings, or null for none.</param>
    /// <returns>A new grid with the same name, library and cells.</returns>
    public LevelGrid WithTransitions(LevelTransitions? transitions) =>
        new(Name, Library, Rows, Columns, _cells) { Transitions = transitions, Libraries = Libraries, Aliases = Aliases };

    /// <summary>The same level with other cells: what resolving its cells' names gives (<see cref="LevelLibraries.Resolve"/>).</summary>
    /// <param name="cells">The cells, row by row from the south-west, x fastest.</param>
    /// <returns>A new grid with the same name, libraries, aliases and transitions.</returns>
    public LevelGrid WithCells(IReadOnlyList<LevelCell?> cells) =>
        new(Name, Library, Rows, Columns, cells) { Transitions = Transitions, Libraries = Libraries, Aliases = Aliases };

    /// <summary>The cell at a column and row, or null for no room.</summary>
    /// <param name="x">The column, from the west.</param>
    /// <param name="y">The row, from the south.</param>
    public LevelCell? this[int x, int y] => _cells[(y * Columns) + x];

    /// <summary>Every placed room with its cell, row by row from the south-west: the link order.</summary>
    public IEnumerable<(int X, int Y, LevelCell Cell)> Placed
    {
        get
        {
            for (int y = 0; y < Rows; y++)
            {
                for (int x = 0; x < Columns; x++)
                {
                    if (this[x, y] is { IsCovered: false } cell)
                    {
                        yield return (x, y, cell);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The level as the linker takes it: every placed room with its joints
    /// derived from the geometry and every other socket capped.
    /// </summary>
    /// <param name="rooms">The library's rooms by name; null for a name it lacks.</param>
    /// <param name="cellSize">The library's grid.</param>
    /// <param name="kit">The library's door kit.</param>
    /// <returns>The layout, in link order.</returns>
    /// <exception cref="LinkException">The level places a room the library does not have.</exception>
    public LevelLayout ToLayout(Func<string, RoomDefinition?> rooms, float cellSize, SocketKit kit)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        // The placements first, each with its room's footprint, and every
        // cell each covers (its anchor alone for a room of one cell); then
        // each socket is jointed to the socket that stands across its cell
        // edge facing back, or capped (17.8: joints are per cell edge).
        List<(RoomPlacement Placement, RoomDefinition Definition)> placed = [];
        Dictionary<(int X, int Y), int> covered = [];
        foreach ((int x, int y, LevelCell cell) in Placed)
        {
            RoomDefinition definition = Definition(rooms, cell);
            RoomPlacement placement = new(cell.Room, x, y, cell.Rotation) { CellsX = definition.CellsX, CellsY = definition.CellsY };
            CheckFootprint(cell, placement);
            foreach ((int cx, int cy, _, _) in placement.Covered())
            {
                covered[(cx, cy)] = placed.Count;
            }

            placed.Add((placement, definition));
        }

        // Every + is some larger room's (CheckFootprint held each room's
        // other cells to + already).
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                if (this[x, y] is { IsCovered: true } mark && !covered.ContainsKey((x, y)))
                {
                    throw new LinkException(
                        $"{mark.Where}{LevelCell.CoveredMark} is in no room's footprint; {LevelCell.CoveredMark} marks the cells a larger room covers besides its south-west one.");
                }
            }
        }

        List<RoomInstance> instances = [];
        foreach ((RoomPlacement placement, RoomDefinition definition) in placed)
        {
            RoomTransform transform = new(placement, cellSize);
            List<(string, string)> joints = [];
            List<string> capped = [];
            foreach (RoomSocket socket in definition.Sockets)
            {
                (int axis, int sign) = transform.WorldNormal(socket.Facing);
                (_, (int X, int Y) across) = placement.SocketCells(definition, socket, cellSize);
                string? theirs = null;
                if (covered.TryGetValue(across, out int other))
                {
                    (RoomPlacement otherPlacement, RoomDefinition neighbour) = placed[other];
                    RoomTransform otherTransform = new(otherPlacement, cellSize);
                    foreach (RoomSocket candidate in neighbour.Sockets)
                    {
                        if (otherTransform.WorldNormal(candidate.Facing) == (axis, -sign)
                            && otherPlacement.SocketCells(neighbour, candidate, cellSize).Cell == across)
                        {
                            theirs = candidate.Name;
                        }
                    }
                }

                if (theirs is null)
                {
                    capped.Add(socket.Name);
                }
                else
                {
                    joints.Add((socket.Name, theirs));
                }
            }

            instances.Add(new RoomInstance(placement, joints, capped));
        }

        return new LevelLayout(Name, cellSize, kit, instances) { Columns = Columns, Rows = Rows, Transitions = Transitions };
    }

    /// <summary>
    /// Holds a placed room's footprint to the grid (17.8): every cell it
    /// covers is on the grid, and each one but its anchor (the south-west
    /// cell, where the room is written) holds <see cref="LevelCell.CoveredMark"/>.
    /// A room of one cell covers its anchor alone and passes.
    /// </summary>
    private void CheckFootprint(LevelCell cell, RoomPlacement placement)
    {
        if (!placement.IsLarge)
        {
            return;
        }

        if (placement.CellX + placement.SpanCellsX > Columns || placement.CellY + placement.SpanCellsY > Rows)
        {
            throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                $"{cell.Where}room {placement.Room} at cell ({placement.CellX}, {placement.CellY}) turned {placement.NormalizedRotation * 90}"
                + $" covers {placement.SpanCellsX} x {placement.SpanCellsY} cells and runs off the {Rows}x{Columns} grid."));
        }

        foreach ((int x, int y, _, _) in placement.Covered())
        {
            if ((x, y) == (placement.CellX, placement.CellY))
            {
                continue;
            }

            if (this[x, y] is not { IsCovered: true } other)
            {
                string token = this[x, y] is { } holds ? holds.Room : LevelYaml.Empty;
                throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                    $"{(this[x, y] ?? cell).Where}room {placement.Room} at cell ({placement.CellX}, {placement.CellY}) covers cell ({x}, {y}), which holds {token}; write {LevelCell.CoveredMark} there."));
            }

            _ = other;
        }
    }

    private static RoomDefinition Definition(Func<string, RoomDefinition?> rooms, LevelCell cell) =>
        rooms(cell.Room)
        ?? throw new LinkException($"{cell.Where}the level places room \"{cell.Room}\", which is not in the room library.");
}
