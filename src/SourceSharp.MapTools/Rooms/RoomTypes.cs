//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// Where a room's geometry lives inside its grid cell.
/// </summary>
public enum RoomFacing : byte
{
    /// <summary>The room's own +x face, the cell face at x = cell size.</summary>
    PositiveX = 0,

    /// <summary>The room's own -x face, the cell face at x = 0.</summary>
    NegativeX = 1,

    /// <summary>The room's own +y face, the cell face at y = cell size.</summary>
    PositiveY = 2,

    /// <summary>The room's own -y face, the cell face at y = 0.</summary>
    NegativeY = 3,
}

/// <summary>
/// Where a kit's door stands up its wall: the <c>info_room</c> key
/// <c>door_sill</c> (<see cref="RoomLibraryVmf.DoorSillKey"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two rules.</b> The first kits centred the door up the cell face,
/// which put the sill on the floor only for a door exactly the room's
/// interior height (<c>cell − 2 × wall_depth</c>, <see cref="PlayerHull"/>):
/// a lower door would hang in the wall. A kit that wants a door lower than
/// that, or rooms lower than their cell with doors that still reach the
/// floor, stands its door on the floor instead: the sill is the wall depth
/// (the floor slab's top) whatever the door's height.
/// </para>
/// <para>
/// <b>Why centred stays the default.</b> For the one door height the centred
/// rule accepts, the two rules agree only to within
/// <see cref="PlayerHull.SillTolerance"/>, not to the bit: switching every
/// kit to the floor rule could move a plug box by its last bit and change
/// every room's compile. So a kit is centred unless it says otherwise, and a
/// library that says nothing packs and links to the bytes it always did.
/// </para>
/// </remarks>
public enum DoorSill : byte
{
    /// <summary>The door is centred up its cell face; it stands on the floor only when it is the room's interior height. The default.</summary>
    Centred = 0,

    /// <summary>The door stands on the floor: its sill is the wall depth, whatever its height.</summary>
    Floor = 1,
}

/// <summary>
/// The one door shape a room library ships: the socket kit.
/// </summary>
/// <param name="Width">The opening's width along the face it sits on.</param>
/// <param name="Height">The opening's height.</param>
/// <param name="Depth">How deep the socket's hardware reaches into the room.</param>
/// <remarks>
/// <para>
/// Rooms join only at standard door sockets, so the
/// opening in a room's shell is not free-form — it is this rectangle, at the
/// centre of one of the cell's four vertical faces (or of one cell edge of a
/// larger room's perimeter), standing as <see cref="Sill"/> says. The linter
/// refuses a room whose opening is any other size or anywhere else, and the
/// linker matches sockets by geometry: a socket on one face of the shared
/// wall meeting the same socket on the other side is a join, and the two
/// sockets' plug brushes touch inside the wall, which is what makes the
/// sealed pair stay sealed before the door is placed.
/// </para>
/// <para>
/// <see cref="Depth"/> reaches inward from the cell face on both sides of the
/// shared wall, so the two plugs meet at the wall's mid-plane when the rooms
/// are whole-cell placements.
/// </para>
/// </remarks>
public readonly record struct SocketKit(float Width, float Height, float Depth)
{
    /// <summary>The RPG's standard socket: a 96×96 opening, 8 units deep.</summary>
    public static SocketKit Standard { get; } = new(96, 96, 8);

    /// <summary>
    /// Where the door stands up its wall (<see cref="DoorSill"/>): centred,
    /// the default, or on the floor. Part of the kit, so it is compared
    /// wherever kits are (rooms of a library, libraries of a level, a layout
    /// against its library), and two rooms only join when their doors stand
    /// alike.
    /// </summary>
    /// <remarks>
    /// An init property with the centred default, rather than a constructor
    /// argument, so every kit built before the rule existed, and every kit
    /// that does not ask, is the kit it always was: equal, printed and
    /// recorded the same.
    /// </remarks>
    public DoorSill Sill { get; init; }

    /// <summary>The most door slots a cell edge may have.</summary>
    public const int MaxSlots = 5;

    /// <summary>
    /// How many places along each cell edge a door may stand (the library's
    /// <c>door_slots</c>): 1, the default, the door centred on the edge as it
    /// always was; or 3 or 5, the edge divided evenly with a door centred in
    /// each part (<see cref="SlotLow"/>). Odd, so the middle slot is the
    /// one-slot door and the slots mirror onto each other about it: turned
    /// or mirrored, a slot lands on a slot. Part of the kit, so it is
    /// compared wherever kits are, and two rooms only join at the same
    /// slot.
    /// </summary>
    /// <remarks>
    /// Stored as the count less one, so a kit that does not ask (or asks for
    /// 1) is equal, printed and recorded as the kit it always was.
    /// </remarks>
    public int Slots
    {
        get => _extraSlots + 1;
        init => _extraSlots = value - 1;
    }

    private readonly int _extraSlots;

    /// <summary>
    /// Where slot <paramref name="slot"/>'s door starts along a cell edge, in
    /// units from the edge's own start (the room's south on an east or west
    /// face, its west on a north or south face).
    /// </summary>
    /// <param name="cellSize">The cell the doors are cut in.</param>
    /// <param name="slot">The slot, 0 to <see cref="Slots"/> less one.</param>
    /// <returns>The door's low edge along the cell edge.</returns>
    /// <remarks>
    /// <para>
    /// The middle slot's is the one-slot door's, the unit rectangle's start
    /// times the cell, the very product every plug box has always been built
    /// from. Each slot before it is the middle of its part of the evenly
    /// divided edge, less half the door, rounded to a whole unit; each slot
    /// after it is the mirror of the slot as far before the middle,
    /// <c>cell - low - width</c>. So slot <c>k</c> and slot
    /// <c>Slots - 1 - k</c> are each other's reflection exactly, which is
    /// what a turn of a half or a mirror does to an edge, and two rooms
    /// facing across an edge meet at the same place whichever way each is
    /// turned.
    /// </para>
    /// <para>
    /// Whole units, because a third or a fifth of a cell is not one: the
    /// rounding moves a door less than a unit from the exact division, and
    /// keeps every plug's faces on whole coordinates, as a hand-built room's
    /// are.
    /// </para>
    /// </remarks>
    public float SlotLow(float cellSize, int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Slots);
        int middle = (Slots - 1) / 2;
        if (slot == middle)
        {
            (float u0, _, _, _) = OpeningUnit(cellSize);
            return u0 * cellSize;
        }

        if (slot > middle)
        {
            return cellSize - SlotLow(cellSize, Slots - 1 - slot) - Width;
        }

        double part = (double)cellSize / Slots;
        return (float)Math.Round(((slot + 0.5) * part) - (Width / 2.0), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Why the kit's slots do not fit its cell edge, or null: the first door
    /// leaves at least the wall depth of wall to the corner.
    /// </summary>
    /// <param name="cellSize">The cell the doors are cut in.</param>
    /// <returns>The reason with its numbers, or null for a kit of one slot or slots that fit.</returns>
    /// <remarks>
    /// One test holds every gap. The first door's corner gap is a part's
    /// half less half a door, <c>c/2N - w/2</c>; the wall between two doors
    /// is a part less a door, <c>c/N - w</c>, twice it. Rounding each door
    /// to a whole unit moves a gap by less than one, so a corner of at least
    /// the wall depth leaves at least twice the depth less one between doors
    /// (the wall depth or more for any depth of a unit or more): plugs never
    /// overlap or meet, and every corner keeps a wall. The edge a kit of
    /// <c>N</c> slots needs is <c>N × (w + 2 × depth)</c>.
    /// </remarks>
    public string? SlotProblem(float cellSize)
    {
        if (Slots == 1)
        {
            return null;
        }

        float corner = SlotLow(cellSize, 0);
        return corner >= Depth
            ? null
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"door_slots {Slots} puts the first door {corner:0.###} units from the corner of a {cellSize:0.###}-unit cell edge, less than the wall depth {Depth:0.###};")
            + string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $" {Slots} doors {Width:0.###} wide need an edge of at least {Slots} x ({Width:0.###} + 2 x {Depth:0.###}) = {Slots * (Width + (2 * Depth)):0.###}, or fewer slots.");
    }

    /// <summary>The opening's rectangle on the unit cell <c>[0,1]²</c> of a face.</summary>
    /// <remarks>
    /// Across the face the opening is always centred, <c>0.5 ∓ w/2c</c>. Up
    /// it, a centred kit's is <c>0.5 ∓ h/2c</c>; a floor kit's runs from the
    /// wall depth to the wall depth plus the door, divided by the cell. The
    /// heights in units are <see cref="OpeningZ"/>, which every plug box is
    /// built from.
    /// </remarks>
    public (float U0, float V0, float U1, float V1) OpeningUnit(float cellSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cellSize, 0f);
        float halfU = Width / (2 * cellSize);
        if (Sill == DoorSill.Floor)
        {
            return (0.5f - halfU, Depth / cellSize, 0.5f + halfU, (Depth + Height) / cellSize);
        }

        float halfV = Height / (2 * cellSize);
        return (0.5f - halfU, 0.5f - halfV, 0.5f + halfU, 0.5f + halfV);
    }

    /// <summary>The opening's sill and top, in units above the floor's underside (z = 0).</summary>
    /// <param name="cellSize">The cell the kit's doors are cut in.</param>
    /// <returns>The sill and the top of the opening.</returns>
    /// <remarks>
    /// A centred kit's is the unit rectangle's times the cell, the very
    /// product every plug box has always been built from, so its bits do not
    /// move; a floor kit's is the wall depth and the wall depth plus the
    /// door, exact, with no division in between.
    /// </remarks>
    public (float Z0, float Z1) OpeningZ(float cellSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cellSize, 0f);
        if (Sill == DoorSill.Floor)
        {
            return (Depth, Depth + Height);
        }

        (_, float v0, _, float v1) = OpeningUnit(cellSize);
        return (v0 * cellSize, v1 * cellSize);
    }

    /// <summary>Validates the kit as a socket: positive, finite, and it fits in a face.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not a positive finite number, or the sill is not a rule.</exception>
    /// <exception cref="ArgumentException">The opening does not fit inside one face.</exception>
    /// <remarks>
    /// Every comparison against NaN is false, so a plain "not positive" check
    /// lets a NaN through; the finite test is what refuses it.
    /// </remarks>
    public void Validate()
    {
        RoomNumbers.RequirePositiveFinite(Width, nameof(Width));
        RoomNumbers.RequirePositiveFinite(Height, nameof(Height));
        RoomNumbers.RequirePositiveFinite(Depth, nameof(Depth));
        if (Width > 2_000_000f || Height > 2_000_000f)
        {
            throw new ArgumentException("The socket kit is larger than any cell can hold.", nameof(Width));
        }

        if ((byte)Sill > (byte)DoorSill.Floor)
        {
            throw new ArgumentOutOfRangeException(nameof(Sill), Sill, "A door is centred or stands on the floor.");
        }

        if (Slots < 1 || Slots > MaxSlots || Slots % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Slots), Slots, "A cell edge has 1, 3 or 5 door slots.");
        }
    }

    /// <summary>
    /// The kit as messages print it: <c>Width = 96, Height = 224, Depth = 16</c>,
    /// with <c>, Sill = Floor</c> only for a floor kit, so a centred kit
    /// prints exactly as it did before kits had a sill.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Width = ");
        builder.Append(Width);
        builder.Append(", Height = ");
        builder.Append(Height);
        builder.Append(", Depth = ");
        builder.Append(Depth);
        if (Sill != DoorSill.Centred)
        {
            builder.Append(", Sill = ");
            builder.Append(Sill.ToString());
        }

        if (Slots != 1)
        {
            builder.Append(", Slots = ");
            builder.Append(Slots);
        }

        return true;
    }
}

/// <summary>
/// One opening in a room's shell, on one cell edge of its perimeter.
/// </summary>
/// <param name="Facing">Which side of the room the opening is in, room-local.</param>
/// <param name="Name">The socket's name, unique in the room.</param>
/// <remarks>
/// <para>
/// In a room library a socket is found from its door plug
/// (<see cref="RoomLibraryVmf"/>): a world brush filling the kit's plug box
/// at one cell edge of the room's perimeter makes that edge a socket. That
/// the plug really is the kit's trigger-surfaced hardware, and that the room
/// holds no other, is the linter's job, see <see cref="RoomLinter"/>.
/// </para>
/// <para>
/// <b>A socket's place is (face, offset).</b> A room of one cell has one
/// cell edge per face, so its sockets are told apart by their face alone
/// and every offset is 0. A room larger than one cell (the rooms design,
/// 17.8) has one cell edge per cell along each face, and
/// <see cref="Offset"/> counts them from the room's own south (on its east
/// and west faces) or west (on its north and south faces). Everything per
/// socket (the plug box, the joints, the door graph, navigation's door
/// points, the level map's doors) is keyed by the socket, not by its face,
/// and its place comes from one function (<see cref="RoomLinter.SealBox"/>),
/// so a further position along an edge would be one more field here.
/// </para>
/// </remarks>
public readonly record struct RoomSocket(RoomFacing Facing, string Name)
{
    /// <summary>
    /// The cell edge of its face the socket is on: 0 for a room of one cell,
    /// and 0 to the face's length in cells less one for a larger room,
    /// counted from the room's own south (east and west faces) or west
    /// (north and south faces).
    /// </summary>
    public int Offset { get; init; }

    /// <summary>
    /// The door slot along its cell edge (<see cref="SocketKit.Slots"/>): 0
    /// for a kit of one slot, and 0 to the kit's slots less one otherwise,
    /// counted the way <see cref="Offset"/> is, from the room's own south
    /// (east and west faces) or west (north and south faces). A socket's
    /// place is (face, offset, slot).
    /// </summary>
    public int Slot { get; init; }

    /// <summary>Validates the socket.</summary>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The facing is not one of four, or the offset is negative.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("A socket needs a name.", nameof(Name));
        }

        if ((uint)Facing > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(Facing), Facing, "A room has four cell faces.");
        }

        if (Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Offset), Offset, "A socket's cell offset counts from 0.");
        }

        if (Slot < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Slot), Slot, "A socket's door slot counts from 0.");
        }
    }

    /// <summary>
    /// The socket as messages print it: its facing and name, with its offset
    /// only when it is not 0, so a one-cell room's socket prints as it did
    /// before sockets had offsets.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Facing = ");
        builder.Append(Facing.ToString());
        builder.Append(", Name = ");
        builder.Append(Name);
        if (Offset != 0)
        {
            builder.Append(", Offset = ");
            builder.Append(Offset);
        }

        if (Slot != 0)
        {
            builder.Append(", Slot = ");
            builder.Append(Slot);
        }

        return true;
    }
}

/// <summary>
/// One room of a library: a sealed box built to occupy one grid cell, or a
/// rectangle of them (<see cref="CellsX"/> by <see cref="CellsY"/>).
/// </summary>
/// <param name="Name">The room's name, unique in its library file.</param>
/// <param name="CellSize">The edge the box is built for; a whole-cell placement.</param>
/// <param name="Kit">The door kit every opening is cut from.</param>
/// <param name="Sockets">The openings in the shell.</param>
/// <remarks>
/// The rules a room must satisfy for the linker's shortcuts to be sound — its
/// brushes lie inside its own cells, nothing crosses a cell face except the
/// fixed kit, the shell is sealed except at the registered openings, the
/// interior is convex-enough to be reached from every corner without leaving
/// the cell — are enforced by <see cref="RoomLinter"/>, not assumed: every
/// shortcut the linker takes is unsound without them.
/// </remarks>
public sealed record RoomDefinition(string Name, float CellSize, SocketKit Kit, IReadOnlyList<RoomSocket> Sockets)
{
    /// <summary>
    /// The most units a room may be tall: the engine's coordinates stop at
    /// ±16,384 (<see cref="GeometryEpsilons.MaxCoordInteger"/>), and every
    /// floor is at z = 0.
    /// </summary>
    public const float MaxHeight = GeometryEpsilons.MaxCoordInteger;

    /// <summary>
    /// The room's own height, in units: its <c>info_room</c>'s
    /// <c>room_height</c> key (the rooms design, 17.6), the cell size when it
    /// has none. The room's box is <c>[0, c] × [0, c] × [0, Height]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the box changes, never the door.</b> The door box is the one a
    /// cube room of the library has: <see cref="RoomLinter.SealBox"/> keeps
    /// computing its z range from <see cref="SocketKit.OpeningUnit"/> and the
    /// <see cref="CellSize"/>, never from this height. So a tall room's plugs
    /// are bit for bit a cube room's, joints between rooms of different
    /// heights match exactly, and the kit's sill rule
    /// (<see cref="PlayerHull.DoorProblem"/>) keeps every door on the floor.
    /// Recomputing the sill as the wall depth would be cleaner, but could move
    /// a plug by its last bit where the sill tolerance let a library through,
    /// and so change every room's compile.
    /// </para>
    /// <para>
    /// <b>Why an init property with the cube's default</b> rather than a
    /// constructor argument: every room built before heights, and every one
    /// that does not ask, is a cube, and must stay equal to the definition it
    /// always was (the pack, the room cache and the link compare definitions).
    /// A room whose height is the cell size is a cube, not a shaped room
    /// (<see cref="IsShaped"/>), whatever spelled it.
    /// </para>
    /// </remarks>
    public float Height { get; init; } = CellSize;

    /// <summary>
    /// The most cells a room may cover along either of its axes: a guard, as
    /// a level's own grid has one (<see cref="LevelYaml.MaxCells"/>), so a
    /// mistyped footprint is refused rather than compiled.
    /// </summary>
    public const int MaxFootprint = 16;

    /// <summary>
    /// How many cells the room covers along its own +x: its <c>info_room</c>'s
    /// <c>room_footprint</c> (the rooms design, 17.8), 1 by default.
    /// </summary>
    /// <remarks>
    /// An init property with the one-cell default, like <see cref="Height"/>,
    /// so every room that does not ask is the definition it always was.
    /// </remarks>
    public int CellsX { get; init; } = 1;

    /// <summary>How many cells the room covers along its own +y, 1 by default (<see cref="CellsX"/>).</summary>
    public int CellsY { get; init; } = 1;

    /// <summary>
    /// Whether the room covers more than one cell (17.8): its tree is
    /// compiled to split at every cell face, and the link routes each cell
    /// it covers to that cell's subtree.
    /// </summary>
    public bool IsLarge => CellsX != 1 || CellsY != 1;

    /// <summary>Whether the room's height is its own: taller or lower than its cell.</summary>
    public bool HasOwnHeight => Height != CellSize;

    /// <summary>
    /// Whether the room is shaped: taller or lower than its cell, or larger
    /// than one cell. A shaped room carries the pack's <c>SHAP</c> section
    /// (<see cref="RoomShape"/>), and a pack holding one is written at
    /// version 5, or 6 for a larger room (<see cref="RoomPack"/>); a cube
    /// room is what it always was, to the byte.
    /// </summary>
    public bool IsShaped => HasOwnHeight || IsLarge;

    /// <summary>
    /// The room's box, room-local: <c>[0, W·c] × [0, D·c] × [0, h]</c>. What
    /// the split, the lint, the prop and furniture cell rules and navigation
    /// hold the room to; the cube <c>[0, c]³</c> for a room that is not
    /// shaped.
    /// </summary>
    public Box Bounds => new(Vec3.Zero, new Vec3(SpanX, SpanY, Height));

    /// <summary>The footprint's extent along the room's own +x, in units: the cell size for a room of one cell.</summary>
    public float SpanX => CellsX == 1 ? CellSize : CellsX * CellSize;

    /// <summary>The footprint's extent along the room's own +y, in units.</summary>
    public float SpanY => CellsY == 1 ? CellSize : CellsY * CellSize;

    /// <summary>How many cell edges a face has: the footprint's length along it.</summary>
    /// <param name="facing">The face.</param>
    /// <returns><see cref="CellsY"/> on the east and west faces, <see cref="CellsX"/> on the north and south.</returns>
    public int EdgesOn(RoomFacing facing) => facing is RoomFacing.PositiveX or RoomFacing.NegativeX ? CellsY : CellsX;

    /// <summary>The room-local cell a socket stands in: the cell inside the footprint whose edge it is on.</summary>
    /// <param name="socket">The socket.</param>
    /// <returns>The cell, 0-based from the room's south-west.</returns>
    public (int X, int Y) CellOf(RoomSocket socket) => socket.Facing switch
    {
        RoomFacing.PositiveX => (CellsX - 1, socket.Offset),
        RoomFacing.NegativeX => (0, socket.Offset),
        RoomFacing.PositiveY => (socket.Offset, CellsY - 1),
        _ => (socket.Offset, 0),
    };

    /// <summary>Validates the definition.</summary>
    /// <exception cref="ArgumentException">The name is blank or two sockets share a face.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The cell size is not a positive finite number, or the height breaks
    /// a rule of <see cref="HeightProblem"/>.
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("A room needs a name.", nameof(Name));
        }

        RoomNumbers.RequirePositiveFinite(CellSize, nameof(CellSize));
        Kit.Validate();
        if (HasOwnHeight && HeightProblem(Height, Kit) is { } problem)
        {
            throw new ArgumentOutOfRangeException(nameof(Height), Height, $"room {Name}: room_height {problem}");
        }

        if (CellsX < 1 || CellsX > MaxFootprint || CellsY < 1 || CellsY > MaxFootprint)
        {
            throw new ArgumentOutOfRangeException(nameof(CellsX), (CellsX, CellsY),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"room {Name}: a footprint of {CellsX} x {CellsY} cells; a room covers 1 to {MaxFootprint} cells each way."));
        }

        HashSet<(RoomFacing, int, int)> seen = [];
        foreach (RoomSocket socket in Sockets)
        {
            socket.Validate();
            if (socket.Offset >= EdgesOn(socket.Facing))
            {
                throw new ArgumentOutOfRangeException(nameof(Sockets), socket.Offset,
                    string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"socket \"{socket.Name}\" is at cell {socket.Offset} of a face {EdgesOn(socket.Facing)} cell(s) long."));
            }

            if (socket.Slot >= Kit.Slots)
            {
                throw new ArgumentOutOfRangeException(nameof(Sockets), socket.Slot,
                    string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"socket \"{socket.Name}\" is at door slot {socket.Slot} of a kit with {Kit.Slots}."));
            }

            if (!seen.Add((socket.Facing, socket.Offset, socket.Slot)))
            {
                throw new ArgumentException(
                    Kit.Slots > 1
                        ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                            $"Two sockets at one door slot: {socket.Facing} at cell {socket.Offset}, slot {socket.Slot}.")
                        : IsLarge
                        ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                            $"Two sockets at one cell edge: {socket.Facing} at cell {socket.Offset}.")
                        : string.Concat("Two sockets on the same face: ", socket.Facing.ToString(), "."),
                    nameof(Sockets));
            }
        }
    }

    /// <summary>
    /// What is wrong with a room height for a kit, as the tail of the rooms
    /// design's 17.3 texts (after <c>room_height {h} </c>), or null when it
    /// is a height: a whole number of units, at least the door plus the floor
    /// and ceiling slabs (<c>door_height + 2 × wall_depth</c>), and at most
    /// <see cref="MaxHeight"/>.
    /// </summary>
    /// <param name="height">The height.</param>
    /// <param name="kit">The library's door kit.</param>
    /// <returns>The problem, or null.</returns>
    /// <remarks>
    /// <para>
    /// A room lower than its cell is allowed (a low hallway), as long as the
    /// door fits. Whole units because nothing finer is authored, and so that
    /// every bound the link writes from it (node and leaf bounds are shorts)
    /// is the height itself, not a rounding of it.
    /// </para>
    /// <para>
    /// Navigation's rules (a whole number of voxels, at most 255 of them) are
    /// the library's, not the room's: they depend on its settings, so the
    /// split checks them (<see cref="RoomLibraryVmf"/>).
    /// </para>
    /// </remarks>
    public static string? HeightProblem(float height, SocketKit kit)
    {
        if (!float.IsFinite(height) || height != MathF.Floor(height))
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{height:0.###} is not a whole number of units.");
        }

        float least = kit.Height + (2 * kit.Depth);
        if (height < least)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{height:0.###} leaves no room for the door; a room is at least {least:0.###} tall (door_height + 2 x wall_depth).");
        }

        return height > MaxHeight
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{height:0.###} is taller than {MaxHeight:0.###}, the most the engine's coordinates allow.")
            : null;
    }
}

/// <summary>
/// Where one room of the library stands in the level.
/// </summary>
/// <param name="Room">The library name of the room to place.</param>
/// <param name="CellX">The cell column; the placement's x translation is <c>CellX × cell size</c>.</param>
/// <param name="CellY">The cell row.</param>
/// <param name="Rotation">
/// Quarter turns about +z, counter-clockwise seen from above: 0, 1, 2 or 3.
/// A count, not degrees; anything outside 0..3 is refused rather than
/// reduced, because a layout that says 90 or 4 almost certainly meant
/// something other than what the reduction would give it.
/// </param>
/// <remarks>
/// Translations are whole cells and rotations are quarter turns, which is what
/// makes the transforms exact — <see cref="RoomTransform"/> permutes and negates
/// components, so no float drifts and the assembly's bytes cannot depend on
/// thread count (invariant I4).
/// </remarks>
public readonly record struct RoomPlacement(string Room, int CellX, int CellY, int Rotation)
{
    /// <summary>The room's name as the layout spells it.</summary>
    internal string RawRoom => Room;

    /// <summary>
    /// Whole cells up (positive) or down (negative) from the grid: 0 for
    /// every room a level places, -1 for the library's skybox room, which the
    /// link places below the grid (the rooms design, 4.12 and open point O11).
    /// </summary>
    /// <remarks>
    /// Only <see cref="RoomTransform"/> reads it, as a translation along z.
    /// The grid logic (joints, neighbours, the top tree's cells) keys on
    /// <see cref="CellX"/> and <see cref="CellY"/> alone, which is why the
    /// skybox never joins that logic: it is placed beside the layout, not in
    /// it.
    /// </remarks>
    public int Level { get; init; }

    /// <summary>
    /// The placed room's footprint along its own +x, in cells
    /// (<see cref="RoomDefinition.CellsX"/>): 1 for a room of one cell. The
    /// placement's cell is then its <b>anchor</b>, the south-west cell of the
    /// footprint as placed, and the room covers <see cref="SpanCellsX"/> by
    /// <see cref="SpanCellsY"/> cells from it (the rooms design, 17.8).
    /// </summary>
    /// <remarks>
    /// Set from the room's definition by whatever builds the layout from a
    /// level (<see cref="LevelGrid.ToLayout"/>), and by the link for a layout
    /// built in code (<see cref="LevelLinker.WithFootprints"/>), so every
    /// transform of the placement (<see cref="RoomTransform"/>) knows how far
    /// a turn moves the room.
    /// </remarks>
    public int CellsX { get; init; } = 1;

    /// <summary>The placed room's footprint along its own +y, in cells (<see cref="CellsX"/>).</summary>
    public int CellsY { get; init; } = 1;

    /// <summary>The cells the placed room covers along world x: its footprint turned.</summary>
    public int SpanCellsX => (NormalizedRotation & 1) == 0 ? CellsX : CellsY;

    /// <summary>The cells the placed room covers along world y.</summary>
    public int SpanCellsY => (NormalizedRotation & 1) == 0 ? CellsY : CellsX;

    /// <summary>Whether the placed room covers more than one cell.</summary>
    public bool IsLarge => CellsX != 1 || CellsY != 1;

    /// <summary>The world cell a room-local cell of the footprint is placed in.</summary>
    /// <param name="x">The room-local cell along the room's +x.</param>
    /// <param name="y">The room-local cell along its +y.</param>
    /// <returns>The world cell.</returns>
    /// <remarks>
    /// The turn of <see cref="RoomTransform.Apply"/> on cells: a quarter turn
    /// takes the room's +x to world +y, so its cell <c>(x, y)</c> lands
    /// <c>D − 1 − y</c> cells east of the anchor and <c>x</c> north.
    /// </remarks>
    public (int X, int Y) WorldCell(int x, int y) => NormalizedRotation switch
    {
        0 => (CellX + x, CellY + y),
        1 => (CellX + (CellsY - 1 - y), CellY + x),
        2 => (CellX + (CellsX - 1 - x), CellY + (CellsY - 1 - y)),
        _ => (CellX + y, CellY + (CellsX - 1 - x)),
    };

    /// <summary>The room-local cell of the footprint a world cell holds: <see cref="WorldCell"/> undone.</summary>
    /// <param name="x">The world cell's column.</param>
    /// <param name="y">The world cell's row.</param>
    /// <returns>The room-local cell (outside the footprint for a cell the room does not cover).</returns>
    public (int X, int Y) LocalCell(int x, int y)
    {
        int dx = x - CellX, dy = y - CellY;
        return NormalizedRotation switch
        {
            0 => (dx, dy),
            1 => (dy, CellsY - 1 - dx),
            2 => (CellsX - 1 - dx, CellsY - 1 - dy),
            _ => (CellsX - 1 - dy, dx),
        };
    }

    /// <summary>Every world cell the placed room covers, each with its room-local cell, room-local order (x fastest).</summary>
    /// <returns>The covered cells; the anchor alone for a room of one cell.</returns>
    public IEnumerable<(int X, int Y, int LocalX, int LocalY)> Covered()
    {
        for (int y = 0; y < CellsY; y++)
        {
            for (int x = 0; x < CellsX; x++)
            {
                (int wx, int wy) = WorldCell(x, y);
                yield return (wx, wy, x, y);
            }
        }
    }

    /// <summary>
    /// The world cell a socket of the placed room stands in, and the cell
    /// across its cell edge: where the room joining it must stand.
    /// </summary>
    /// <param name="definition">The placed room.</param>
    /// <param name="socket">The socket.</param>
    /// <param name="cellSize">The grid's cell size.</param>
    /// <returns>The two cells.</returns>
    public ((int X, int Y) Cell, (int X, int Y) Across) SocketCells(RoomDefinition definition, RoomSocket socket, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(definition);
        (int lx, int ly) = definition.CellOf(socket);
        (int x, int y) = WorldCell(lx, ly);
        (int axis, int sign) = new RoomTransform(this, cellSize).WorldNormal(socket.Facing);
        return ((x, y), axis == 0 ? (x + sign, y) : (x, y + sign));
    }

    /// <summary>
    /// Where along its world cell edge a socket of the placed room stands:
    /// the world coordinate, across the edge's normal, of its plug's middle.
    /// Two sockets facing each other across an edge meet when these are
    /// equal, which under a kit of several door slots is the same slot seen
    /// from both sides (slot <c>k</c> of one room meets slot <c>k</c> or
    /// <c>slots - 1 - k</c> of the other, as their turns have it).
    /// </summary>
    /// <param name="definition">The placed room.</param>
    /// <param name="socket">The socket.</param>
    /// <param name="cellSize">The grid's cell size.</param>
    /// <returns>The world coordinate along the edge.</returns>
    public float SocketAlong(RoomDefinition definition, RoomSocket socket, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RoomTransform transform = new(this, cellSize);
        Box plug = RoomLinter.SealBox(definition, socket, cellSize);
        Vec3 middle = transform.Apply((plug.Mins + plug.Maxs) * 0.5f);
        return transform.WorldNormal(socket.Facing).Axis == 0 ? middle.Y : middle.X;
    }

    /// <summary>Whether two sockets' plugs stand at the same place along their shared edge (<see cref="SocketAlong"/>), to within half a unit.</summary>
    /// <param name="ours">This placement's along-edge coordinate.</param>
    /// <param name="theirs">The other's.</param>
    /// <returns>True when they meet.</returns>
    public static bool SameSlot(float ours, float theirs) => Math.Abs(ours - theirs) < 0.5f;

    /// <summary>
    /// The placement as messages print it: room, cell and rotation, with its
    /// level and footprint only when they are not a grid room's of one cell.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Room = ");
        builder.Append(Room);
        builder.Append(", CellX = ");
        builder.Append(CellX);
        builder.Append(", CellY = ");
        builder.Append(CellY);
        builder.Append(", Rotation = ");
        builder.Append(Rotation);
        if (Level != 0)
        {
            builder.Append(", Level = ");
            builder.Append(Level);
        }

        if (IsLarge)
        {
            builder.Append(", CellsX = ");
            builder.Append(CellsX);
            builder.Append(", CellsY = ");
            builder.Append(CellsY);
        }

        return true;
    }

    /// <summary>Validates the placement.</summary>
    /// <exception cref="ArgumentException">The room name is blank.</exception>
    /// <exception cref="LinkException">The rotation is not a quarter-turn count 0..3.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Room))
        {
            throw new ArgumentException("A placement names a room.", nameof(Room));
        }

        if ((uint)Rotation > 3)
        {
            throw new LinkException(
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"placement rotation {Rotation} is not a quarter-turn count (0, 1, 2 or 3)"));
        }
    }

    /// <summary>The rotation reduced to 0..3.</summary>
    /// <remarks>
    /// Equal to <see cref="Rotation"/> for every placement that passed
    /// <see cref="Validate"/>; the reduction only keeps the transforms total
    /// for a caller that builds a <see cref="RoomTransform"/> directly.
    /// </remarks>
    public int NormalizedRotation => ((Rotation % 4) + 4) % 4;
}

/// <summary>
/// One room's entry in a level layout: where it stands and who it joins.
/// </summary>
/// <param name="Placement">The whole-cell placement.</param>
/// <param name="Joints">The socket pairs, this room's socket to the neighbour's.</param>
/// <param name="Capped">Sockets of this room that face the void and are capped shut.</param>
public sealed record RoomInstance(RoomPlacement Placement, IReadOnlyList<(string Socket, string NeighborSocket)> Joints, IReadOnlyList<string> Capped)
{
    /// <summary>Validates the instance.</summary>
    public void Validate()
    {
        Placement.Validate();
        foreach ((string socket, string neighbor) in Joints)
        {
            if (string.IsNullOrWhiteSpace(socket) || string.IsNullOrWhiteSpace(neighbor))
            {
                throw new ArgumentException("A joint names both of its sockets.", nameof(Joints));
            }
        }
    }
}

/// <summary>
/// A level: rooms of one library on the shared grid, joined at their sockets.
/// </summary>
/// <param name="Name">The level's name; becomes the map's base name.</param>
/// <param name="CellSize">The grid's edge — the library is built for one size.</param>
/// <param name="Kit">The library's door kit.</param>
/// <param name="Rooms">The placements, in link order.</param>
/// <remarks>
/// The linker's view of a level, with every joint and cap spelled out. A
/// level file (<see cref="LevelYaml"/>) never spells them: its
/// <see cref="LevelGrid.ToLayout"/> derives them from the geometry. Two
/// instances may not share a cell, and every socket of every instance must
/// be jointed to a neighbour's socket or explicitly capped — the linter's
/// "matched or capped" rule (§10b first table), enforced at link time
/// because only the layout knows its neighbours.
/// </remarks>
public sealed record LevelLayout(string Name, float CellSize, SocketKit Kit, IReadOnlyList<RoomInstance> Rooms)
{
    /// <summary>
    /// The level grid's columns, or null when the layout was built without a
    /// grid: what tells a reference to an empty cell from one off the grid
    /// (the missing-neighbour warning says which). Without it only a
    /// negative column is off the grid.
    /// </summary>
    public int? Columns { get; init; }

    /// <summary>The level grid's rows, or null: as <see cref="Columns"/>.</summary>
    public int? Rows { get; init; }

    /// <summary>
    /// What the level file says about its transitions, or null when it says
    /// nothing (<see cref="LevelGrid.Transitions"/>): the link and the flatten
    /// apply the level rule of the rooms design (11.1) when this is set or a
    /// placed room has a transition role, and link as before otherwise.
    /// </summary>
    public LevelTransitions? Transitions { get; init; }

    /// <summary>Validates the layout's own shape.</summary>
    /// <exception cref="ArgumentException">The name is blank, the level places no room, or two rooms share a cell.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The cell size is not a positive finite number.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("A level needs a name.", nameof(Name));
        }

        RoomNumbers.RequirePositiveFinite(CellSize, nameof(CellSize));
        Kit.Validate();

        // A level of no rooms has no world model to hang anything on: every
        // later step (the grid extent, the first room's lump versions) would
        // index an empty list.
        if (Rooms.Count == 0)
        {
            throw new ArgumentException("A level places at least one room.", nameof(Rooms));
        }

        // Every cell a room covers (its anchor alone for a room of one cell):
        // two footprints may not overlap (17.8).
        HashSet<(int, int)> cells = [];
        foreach (RoomInstance room in Rooms)
        {
            room.Validate();
            foreach ((int x, int y, _, _) in room.Placement.Covered())
            {
                if (!cells.Add((x, y)))
                {
                    throw new ArgumentException(
                        string.Create(System.Globalization.CultureInfo.InvariantCulture,
                            $"two rooms are placed in cell ({x}, {y})."),
                        nameof(Rooms));
                }
            }
        }
    }
}

/// <summary>
/// Everything about the level the linker knows before it copies a byte: the
/// resolved placements and the joint graph the visibility rows are built from.
/// </summary>
/// <param name="Layout">The validated layout.</param>
/// <param name="Rooms">The library entries, index-aligned with <see cref="LevelLayout.Rooms"/>.</param>
/// <param name="Planes">The grid's cell-face planes, index-aligned with the faces the door graph names.</param>
public sealed record LevelPlan(
    LevelLayout Layout,
    IReadOnlyList<ResolvedPlacement> Rooms,
    IReadOnlyList<Plane> Planes)
{
}

/// <summary>
/// One placement resolved against its library room: the room object, the exact
/// transform, and the joint graph edges.
/// </summary>
/// <param name="Instance">The layout entry.</param>
/// <param name="Room">The room object it names.</param>
/// <param name="Index">The placement's link order.</param>
/// <param name="Translation">The world translation, whole cells.</param>
/// <param name="Rotation">Quarter turns, normalised.</param>
/// <param name="SocketClusters">The socket seal clusters, room-local indices, in socket order.</param>
public sealed record ResolvedPlacement(
    RoomInstance Instance,
    RoomObject Room,
    int Index,
    Vec3 Translation,
    int Rotation,
    IReadOnlyList<int> SocketClusters)
{
    /// <summary>The room's sockets, room order.</summary>
    public IReadOnlyList<RoomSocket> Sockets => Room.Definition.Sockets;

    /// <summary>
    /// The placement's own lighting payload when the skybox parallax moved
    /// its sun (<see cref="LevelLinker.PlanSkyboxParallax"/>), else null: the
    /// stored turn, as every placement at its bakes' cell takes it.
    /// </summary>
    internal RoomLightingPayload? Parallax { get; init; }
}

/// <summary>The numeric checks the room records share.</summary>
internal static class RoomNumbers
{
    /// <summary>Refuses NaN, the infinities, zero and negatives.</summary>
    /// <param name="value">The number.</param>
    /// <param name="name">The parameter it came from.</param>
    /// <exception cref="ArgumentOutOfRangeException">The number is not positive and finite.</exception>
    public static void RequirePositiveFinite(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be a positive finite number.");
        }
    }
}

/// <summary>
/// An exception from the room linter: one broken guarantee, named.
/// </summary>
public sealed class RoomLintException : Exception
{
    /// <summary>Names the broken guarantee and the geometry that broke it.</summary>
    /// <param name="message">Which guarantee, and where.</param>
    public RoomLintException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// An exception from the linker: a level whose rooms cannot be linked.
/// </summary>
/// <remarks>
/// The linker never silently links a broken level — an unjointed, uncapped
/// socket, a room the library does not have, a placement that shares a cell,
/// a transform that is not whole-cell quarter-turn, a lump kind it cannot
/// relocate. Silence would deliver a level whose PVS is not a superset of
/// anything (§10b's premise).
/// </remarks>
public sealed class LinkException : Exception
{
    /// <summary>Names the refused condition.</summary>
    /// <param name="message">What cannot be linked, and why.</param>
    public LinkException(string message)
        : base(message)
    {
    }
}
