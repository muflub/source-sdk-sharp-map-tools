//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

namespace SourceSharp.MapTools.Rooms;

/// <summary>
/// A shaped room's <c>SHAP</c> pack section: what the room container does
/// not say about a room that is not a cube of its cell (the rooms design,
/// 17.6 and 17.11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a section, and why the room container stays as it is.</b> The
/// container (<see cref="RoomObjectStore"/>, <c>SSROOM01</c>) records a
/// room's cell size, kit and sockets, and every cube room's container is
/// what it always was; so a room's height travels beside it, in a section
/// only a shaped room has. A library of cube rooms packs to the bytes it
/// did, and the room cache keeps every cube room's entries (its rows hold a
/// room's finished sections, this one among them for a shaped room).
/// </para>
/// <para>
/// <b>The bytes</b> have the link sections' framing
/// (<see cref="RoomLinkSections"/>: a codec byte, always none, the payload's
/// length as a big-endian <c>int64</c>, then an <c>int32</c> revision,
/// <see cref="Revision"/>). The payload, in big-endian <c>int32</c>s and a
/// little-endian float, as the other room sections write them: the rotation
/// count, always 1 (a room's shape is the same at every turn: the height
/// does not turn, and a one-cell footprint neither); then per rotation its
/// payload's byte length and the payload: the height (a float, a whole
/// number of units), the footprint's width and depth in cells (1 and 1),
/// the socket count and each socket's cell offset in the footprint, in
/// socket order (0 and 0).
/// </para>
/// <para>
/// <b>A room larger than one cell</b> (17.8) writes <see cref="LargeRevision"/>,
/// the same framing and rotation count, with a payload of: the height; the
/// footprint's width and depth in cells; the socket count and, per socket in
/// socket order, the room-local cell it stands in (x and y) and its
/// position along that cell edge (always 0, the edge's centre: a field kept
/// so that doors at several positions along an edge need no new layout);
/// the count of covered cells and each one's subtree root
/// (<see cref="RoomCellTree.Roots"/>, x fastest); the count of block nodes
/// the link leaves out and each one's index, ascending. Node indices do not
/// change with a turn, so one copy serves all four.
/// </para>
/// <para>
/// <b>The pack version.</b> A pack holding any shaped room is written at
/// version 5 (<see cref="RoomPack.ShapedVersion"/>), one holding a room
/// larger than one cell at version 6 (<see cref="RoomPack.Version"/>), one of
/// cube rooms at version 4 (<see cref="RoomPack.CubeVersion"/>). An older build would skip an
/// unknown tag and link a tall room as a cube, its top tree's bounds one
/// cell tall, so this is the kind of change a version is kept for: an older
/// build refuses the version and never reads the section.
/// </para>
/// <para>
/// <b>Refused</b>, naming the room and the section: a revision this build
/// does not read (read around, the room would be a cube), a rotation count
/// other than 1, a footprint other than one cell (a larger room is PR 20's),
/// a socket count other than the room's, an offset outside the footprint, a
/// height that breaks <see cref="RoomDefinition.HeightProblem"/> for the
/// room's kit, a height equal to the cell size (a cube carries no section),
/// and a section in a pack of a version that cannot hold one.
/// </para>
/// </remarks>
internal static class RoomShape
{
    /// <summary>The tag of a shaped room's section.</summary>
    public const string SectionTag = "SHAP";

    /// <summary>The revision of a one-cell room's section, which this build writes and reads.</summary>
    public const int Revision = RoomLinkSections.Revision;

    /// <summary>The revision of a larger room's section (17.8): the footprint, the sockets' cells and the cell tree.</summary>
    public const int LargeRevision = 2;

    /// <summary>The section for a room, or null for a cube room, which has none.</summary>
    /// <param name="definition">The room.</param>
    /// <param name="cells">A larger room's cell tree (<see cref="RoomCellTree"/>); null for a room of one cell.</param>
    /// <returns>The section, or null.</returns>
    /// <exception cref="ArgumentException">A larger room has no cell tree, or one of another footprint.</exception>
    public static RoomPackSectionData? ToSection(RoomDefinition definition, RoomCellTree? cells = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsShaped)
        {
            return null;
        }

        if (definition.IsLarge)
        {
            return LargeSection(definition, cells ?? throw new ArgumentException($"room {definition.Name} covers several cells but has no cell tree.", nameof(cells)));
        }

        RoomLinkSections.Writer turn = new();
        turn.Structs<float>([definition.Height], counted: false);
        turn.Int(1);
        turn.Int(1);
        turn.Int(definition.Sockets.Count);
        for (int s = 0; s < definition.Sockets.Count; s++)
        {
            turn.Int(0);
            turn.Int(0);
        }

        byte[] payload = turn.ToArray();
        RoomLinkSections.Writer w = new();
        w.Int(Revision);
        w.Int(1);
        w.Int(payload.Length);
        w.Raw(payload);
        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None));
    }

    /// <summary>Revision 2: a larger room's footprint, sockets and cell tree.</summary>
    private static RoomPackSectionData LargeSection(RoomDefinition definition, RoomCellTree cells)
    {
        if (cells.CellsX != definition.CellsX || cells.CellsY != definition.CellsY)
        {
            throw new ArgumentException($"room {definition.Name}: its cell tree is of another footprint.", nameof(cells));
        }

        RoomLinkSections.Writer turn = new();
        turn.Structs<float>([definition.Height], counted: false);
        turn.Int(definition.CellsX);
        turn.Int(definition.CellsY);
        turn.Int(definition.Sockets.Count);
        foreach (RoomSocket socket in definition.Sockets)
        {
            (int x, int y) = definition.CellOf(socket);
            turn.Int(x);
            turn.Int(y);
            turn.Int(0);
        }

        turn.Ints([.. cells.Roots]);
        turn.Ints([.. cells.BlockNodes]);
        byte[] payload = turn.ToArray();
        RoomLinkSections.Writer w = new();
        w.Int(LargeRevision);
        w.Int(1);
        w.Int(payload.Length);
        w.Raw(payload);
        return new RoomPackSectionData(SectionTag, RoomLinkSections.Encode(w.ToArray(), RoomLinkCodec.None));
    }

    /// <summary>Whether a <c>SHAP</c> section describes a room larger than one cell (its revision), which only a version 6 pack holds.</summary>
    /// <param name="section">The section.</param>
    /// <returns>True for a larger room's section.</returns>
    public static bool IsLarge(RoomPackSectionData section) =>
        section.Tag == SectionTag && IsLarge(new ArraySegment<byte>(section.Bytes.ToArray()));

    /// <summary>Whether a <c>SHAP</c> section's bytes are a larger room's (revision <see cref="LargeRevision"/>).</summary>
    /// <param name="bytes">The section's bytes, as stored.</param>
    /// <returns>True for a larger room's section.</returns>
    public static bool IsLarge(ArraySegment<byte> bytes)
    {
        _ = RoomLinkSections.OpenAnyRevision(bytes, "?", SectionTag, out int revision);
        return revision == LargeRevision;
    }

    /// <summary>What a room's section records, read from its bytes; null when the room has none.</summary>
    /// <param name="section">The section's bytes, or null.</param>
    /// <param name="room">The room's name, for messages.</param>
    /// <returns>The shape, or null.</returns>
    /// <exception cref="LinkException">
    /// The section is cut short, of a revision this build does not read, or
    /// out of shape (a rotation count other than 1, a footprint other than
    /// one cell, an offset outside it, bytes after its end); each message
    /// names the room and the section.
    /// </exception>
    /// <remarks>
    /// Read before the room's container, so the container's check of its
    /// compile (<see cref="RoomObjectStore"/>) holds the leaves to the room's
    /// own box; what the section says about the room's kit and sockets is
    /// held to the container by <see cref="Apply"/>.
    /// </remarks>
    public static RoomShapeData? Read(ArraySegment<byte>? section, string room)
    {
        if (section is null)
        {
            return null;
        }

        RoomLinkSections.Reader r = RoomLinkSections.OpenAnyRevision(section, room, SectionTag, out int revision)!;
        if (revision == LargeRevision)
        {
            return ReadLarge(r);
        }

        if (revision != Revision)
        {
            throw new LinkException(
                $"room pack entry \"{room}\": its \"{SectionTag}\" section is of a revision this build does not read;"
                + " its shape would be lost, so recompile the library with ssmap room.");
        }

        int rotations = r.Int();
        if (rotations != 1)
        {
            throw r.Mismatch($"{rotations} rotations; a room's shape is stored once");
        }

        int length = r.Count("shape bytes");
        int start = r.Position;
        float height = r.Structs<float>("height", 1, counted: false)[0];
        int width = r.Int();
        int depth = r.Int();
        if (width != 1 || depth != 1)
        {
            throw r.Mismatch(string.Create(CultureInfo.InvariantCulture,
                $"a footprint of {width} x {depth} cells; this build links rooms of one cell"));
        }

        int sockets = r.Int();
        if (sockets < 0 || sockets > 4)
        {
            throw r.Mismatch($"{sockets} sockets; a room of one cell has 0 to 4");
        }

        for (int s = 0; s < sockets; s++)
        {
            int dx = r.Int();
            int dy = r.Int();
            if (dx != 0 || dy != 0)
            {
                throw r.Mismatch(string.Create(CultureInfo.InvariantCulture,
                    $"socket {s} at cell ({dx}, {dy}) of a one-cell footprint"));
            }
        }

        if (r.Position - start != length)
        {
            throw r.Mismatch($"a shape of {r.Position - start} bytes where it records {length}");
        }

        r.End();
        return new RoomShapeData(height, sockets);
    }

    /// <summary>Revision 2's payload (<see cref="LargeSection"/>).</summary>
    private static RoomShapeData ReadLarge(RoomLinkSections.Reader r)
    {
        int rotations = r.Int();
        if (rotations != 1)
        {
            throw r.Mismatch($"{rotations} rotations; a room's shape is stored once");
        }

        int length = r.Count("shape bytes");
        int start = r.Position;
        float height = r.Structs<float>("height", 1, counted: false)[0];
        int width = r.Int();
        int depth = r.Int();
        if (width < 1 || depth < 1 || width > RoomDefinition.MaxFootprint || depth > RoomDefinition.MaxFootprint || (width == 1 && depth == 1))
        {
            throw r.Mismatch(string.Create(CultureInfo.InvariantCulture,
                $"a footprint of {width} x {depth} cells; a larger room covers 1 to {RoomDefinition.MaxFootprint} cells each way, and more than one"));
        }

        int sockets = r.Count("sockets");
        (int X, int Y)[] cells = new (int, int)[sockets];
        for (int s = 0; s < sockets; s++)
        {
            int x = r.Int();
            int y = r.Int();
            int slot = r.Int();
            if (x < 0 || y < 0 || x >= width || y >= depth)
            {
                throw r.Mismatch(string.Create(CultureInfo.InvariantCulture,
                    $"socket {s} at cell ({x}, {y}) outside its {width} x {depth} footprint"));
            }

            if (slot != 0)
            {
                throw r.Mismatch(string.Create(CultureInfo.InvariantCulture,
                    $"socket {s} at door position {slot} along its cell edge; this build places a door at the edge's centre only"));
            }

            cells[s] = (x, y);
        }

        int[] roots = ReadInts(r, "cell roots");
        if (roots.Length != width * depth)
        {
            throw r.Mismatch(string.Create(CultureInfo.InvariantCulture, $"{roots.Length} cell roots for {width * depth} cells"));
        }

        int[] blocks = ReadInts(r, "block nodes");
        for (int i = 0; i < blocks.Length; i++)
        {
            if (blocks[i] < 0 || (i > 0 && blocks[i] <= blocks[i - 1]))
            {
                throw r.Mismatch("block nodes out of order");
            }
        }

        if (r.Position - start != length)
        {
            throw r.Mismatch($"a shape of {r.Position - start} bytes where it records {length}");
        }

        r.End();
        return new RoomShapeData(height, sockets)
        {
            CellsX = width, CellsY = depth, SocketCells = cells, Cells = new RoomCellTree(width, depth, roots, blocks),
        };

        static int[] ReadInts(RoomLinkSections.Reader r, string what)
        {
            int count = r.Count(what);
            int[] values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = r.Int();
            }

            return values;
        }
    }

    /// <summary>
    /// The room's definition with the height its section records, or the
    /// definition as it is when the room has no section.
    /// </summary>
    /// <param name="shape">The room's shape (<see cref="Read"/>), or null.</param>
    /// <param name="definition">The room's definition from its container.</param>
    /// <returns>The definition, shaped when the section says so.</returns>
    /// <exception cref="LinkException">
    /// The section disagrees with the container: another socket count, the
    /// cell size as a height, or a height the room's kit refuses. The message
    /// names the section, and the pack reader puts the room in front.
    /// </exception>
    public static RoomDefinition Apply(RoomShapeData? shape, RoomDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (shape is not { } data)
        {
            return definition;
        }

        if (data.Sockets != definition.Sockets.Count)
        {
            throw Mismatch($"{data.Sockets} sockets; the room has {definition.Sockets.Count}");
        }

        if (data.Height == definition.CellSize && data.Cells is null)
        {
            throw Mismatch("the cell size as its height; a cube room has no shape section");
        }

        if (data.Height != definition.CellSize && RoomDefinition.HeightProblem(data.Height, definition.Kit) is { } problem)
        {
            throw Mismatch($"room_height {problem.TrimEnd('.')}");
        }

        if (data.Cells is null)
        {
            return definition with { Height = data.Height };
        }

        // A larger room: each socket's offset is the cell it stands in along
        // its face, and that cell must be on the face's side of the footprint.
        RoomSocket[] sockets = new RoomSocket[definition.Sockets.Count];
        for (int s = 0; s < sockets.Length; s++)
        {
            RoomSocket socket = definition.Sockets[s];
            (int x, int y) = data.SocketCells![s];
            (bool onFace, int offset) = socket.Facing switch
            {
                RoomFacing.PositiveX => (x == data.CellsX - 1, y),
                RoomFacing.NegativeX => (x == 0, y),
                RoomFacing.PositiveY => (y == data.CellsY - 1, x),
                _ => (y == 0, x),
            };
            if (!onFace)
            {
                throw Mismatch(string.Create(CultureInfo.InvariantCulture, $"socket \"{socket.Name}\" at cell ({x}, {y}), which is not on its {RoomLibraryVmf.WallName(socket.Facing)} side"));
            }

            sockets[s] = socket with { Offset = offset };
        }

        RoomDefinition shaped = definition with { Height = data.Height, CellsX = data.CellsX, CellsY = data.CellsY, Sockets = sockets };
        try
        {
            shaped.Validate();
        }
        catch (ArgumentException exception)
        {
            throw Mismatch($"a footprint its sockets do not fit: {exception.Message}");
        }

        return shaped;

        static LinkException Mismatch(string what) => new($"its \"{SectionTag}\" section holds {what}.");
    }
}

/// <summary>What a room's <c>SHAP</c> section records (<see cref="RoomShape"/>).</summary>
/// <param name="Height">The room's height.</param>
/// <param name="Sockets">How many sockets it lists.</param>
internal readonly record struct RoomShapeData(float Height, int Sockets)
{
    /// <summary>The footprint's cells along +x: 1 for a room of one cell.</summary>
    public int CellsX { get; init; } = 1;

    /// <summary>The footprint's cells along +y.</summary>
    public int CellsY { get; init; } = 1;

    /// <summary>A larger room's sockets' room-local cells, in socket order; null for a room of one cell.</summary>
    public IReadOnlyList<(int X, int Y)>? SocketCells { get; init; }

    /// <summary>A larger room's cell tree as the pack recorded it; null for a room of one cell.</summary>
    public RoomCellTree? Cells { get; init; }
}
