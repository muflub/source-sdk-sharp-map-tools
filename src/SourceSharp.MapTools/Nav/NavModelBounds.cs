//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Buffers.Binary;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// The movement hulls of the models a room's props use, read from the
/// game's content before the room's navigation is built, so the build itself
/// stays synchronous and reads nothing.
/// </summary>
/// <remarks>
/// A prop blocks agents with its model's movement hull, the box the engine
/// sweeps it as; a model's header states it (<c>hull_min</c>,
/// <c>hull_max</c>) at a fixed place for every studio version, so only the
/// header's first bytes are needed and a model of any version is read. The
/// dictionary belongs to one room's build and is dropped with it.
/// </remarks>
public static class NavModelBounds
{
    /// <summary>Where a studio header keeps its movement hull's low corner.</summary>
    private const int HullOffset = 104;

    /// <summary>The studio header's ident: <c>IDST</c>.</summary>
    private static ReadOnlySpan<byte> Ident => "IDST"u8;

    /// <summary>The model paths a BSP's dynamic-obstacle props name, distinct, in entity order.</summary>
    /// <param name="bsp">The BSP.</param>
    /// <returns>The paths, as the entities spell them.</returns>
    public static IReadOnlyList<string> PropModels(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        List<string> models = [];
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            if (NavGeometry.KindOf(entity.ClassName) is not null && entity.Get("model") is { Length: > 0 } model && model[0] != '*'
                && !models.Contains(model, StringComparer.Ordinal))
            {
                models.Add(model);
            }
        }

        return models;
    }

    /// <summary>
    /// The model paths a BSP's solid static props name, distinct, in the
    /// prop dictionary's order: the hulls <see cref="NavGeometry.StaticProps"/>
    /// asks for.
    /// </summary>
    /// <param name="bsp">The BSP.</param>
    /// <returns>The paths, as the dictionary spells them; none for a map without a static prop lump.</returns>
    public static IReadOnlyList<string> StaticPropModels(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        if (NavGeometry.StaticPropLumpOf(bsp) is not { } lump)
        {
            return [];
        }

        HashSet<int> used = [.. lump.Props.Where(p => p.Solid != 0).Select(p => (int)p.PropType)];
        return [.. lump.ModelNames.Where((_, i) => used.Contains(i)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reads the collision pieces of models' <c>.phy</c> files from the
    /// content (<see cref="Pieces"/>), for the static props a compiled map's
    /// navigation makes solid.
    /// </summary>
    /// <param name="content">The game's content.</param>
    /// <param name="models">The model paths (<c>.mdl</c>); each one's <c>.phy</c> beside it is read.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Each path's pieces, or null when the content has no <c>.phy</c> for it or it is not one this reads.</returns>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<(Vec3 Mins, Vec3 Maxs)>?>> LoadPiecesAsync(
        IContentFileSystem content, IReadOnlyList<string> models, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(models);
        Dictionary<string, IReadOnlyList<(Vec3, Vec3)>?> pieces = new(StringComparer.Ordinal);
        foreach (string model in models)
        {
            IReadOnlyList<(Vec3, Vec3)>? found = null;
            if (VPath.TryCreate(Path.ChangeExtension(model, ".phy"), out VPath path))
            {
                using IMemoryOwner<byte>? owner = await content.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                found = owner is null ? null : Pieces(owner.Memory.Span);
            }

            pieces[model] = found;
        }

        return pieces;
    }

    /// <summary>
    /// A model's collision as the game builds a static prop's from its
    /// <c>.phy</c>: the first solid's convex pieces, each as its bounding box
    /// in the model's frame (Source units, z up).
    /// </summary>
    /// <param name="phy">The <c>.phy</c> file's bytes.</param>
    /// <returns>The boxes, one per convex piece, in the file's order; null when the bytes are not a <c>.phy</c> this reads.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why pieces.</b> A static prop's movement hull is one box around
    /// the whole model, which closes every opening a prop has: a gate frame,
    /// an arch, a fence with a gap, a rail over a stair's side. The game
    /// collides with the <c>.phy</c>'s convex pieces, so the box around each
    /// piece keeps the openings between pieces; it still over-estimates a
    /// piece that is not a box, and can only block more, never less.
    /// </para>
    /// <para>
    /// <b>The format.</b> The file is a header and its solids, each a size
    /// and the solid's bytes (<see cref="PhyFile"/>); a solid is a
    /// <c>VPHY</c> header (28 bytes after the size word that frames it) and an IVP compact surface, or, in a
    /// file from before that header, the compact surface alone. The
    /// surface's ledge tree leads to its convex pieces (ledges); a ledge's
    /// triangles name its corners by index into a point array the ledge
    /// locates relative to itself, each point four floats in IVP's metres
    /// with y down, turned here into Source units.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(Vec3 Mins, Vec3 Maxs)>? Pieces(ReadOnlySpan<byte> phy)
    {
        PhyFile file;
        try
        {
            file = PhyFile.Parse(phy.ToArray());
        }
        catch (InvalidStudioException)
        {
            return null;
        }

        if (file.Solids.Count == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> solid = file.SolidData(0).Span;
        // The solid's size word is the file's framing (PhyFile reads it), so
        // a VPHY header starts at the solid's first byte, 28 bytes long.
        ReadOnlySpan<byte> surface = solid.Length >= 4 && solid[..4].SequenceEqual("VPHY"u8)
            ? solid[Phys.Managed.VphyWriter.HeaderSize..]
            : solid;
        if (surface.Length < 0x30 || !surface.Slice(0x2c, 4).SequenceEqual("IVPS"u8))
        {
            return null;
        }

        try
        {
            List<(Vec3, Vec3)> boxes = [];
            foreach (int ledge in Phys.Managed.IvpCollideQueries.LeafOffsets(surface))
            {
                int pointOffset = BinaryPrimitives.ReadInt32LittleEndian(surface[ledge..]);
                int triangles = BinaryPrimitives.ReadInt16LittleEndian(surface[(ledge + 12)..]);
                double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue];
                double[] hi = [double.MinValue, double.MinValue, double.MinValue];
                for (int t = 0; t < triangles; t++)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        int index = (int)(BinaryPrimitives.ReadUInt32LittleEndian(surface[(ledge + 16 + (16 * t) + 4 + (4 * e))..]) & 0xffff);
                        int at = ledge + pointOffset + (16 * index);
                        float x = BinaryPrimitives.ReadSingleLittleEndian(surface[at..]);
                        float y = BinaryPrimitives.ReadSingleLittleEndian(surface[(at + 4)..]);
                        float z = BinaryPrimitives.ReadSingleLittleEndian(surface[(at + 8)..]);
                        double[] hl = [x * (double)Phys.Managed.IvpCollideQueries.IvpToHl, z * (double)Phys.Managed.IvpCollideQueries.IvpToHl, -y * (double)Phys.Managed.IvpCollideQueries.IvpToHl];
                        for (int k = 0; k < 3; k++)
                        {
                            lo[k] = Math.Min(lo[k], hl[k]);
                            hi[k] = Math.Max(hi[k], hl[k]);
                        }
                    }
                }

                if (triangles > 0 && lo[0] < hi[0] && lo[1] < hi[1] && lo[2] < hi[2])
                {
                    boxes.Add((new Vec3((float)lo[0], (float)lo[1], (float)lo[2]), new Vec3((float)hi[0], (float)hi[1], (float)hi[2])));
                }
            }

            return boxes.Count > 0 ? boxes : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Reads models' hulls from the content.</summary>
    /// <param name="content">The game's content.</param>
    /// <param name="models">The model paths.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Each path's hull, or null when the content lacks the model or it is not a studio model.</returns>
    public static async Task<IReadOnlyDictionary<string, (Vec3 Mins, Vec3 Maxs)?>> LoadAsync(
        IContentFileSystem content, IReadOnlyList<string> models, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(models);
        Dictionary<string, (Vec3, Vec3)?> hulls = new(StringComparer.Ordinal);
        foreach (string model in models)
        {
            (Vec3, Vec3)? hull = null;
            if (VPath.TryCreate(model, out VPath path))
            {
                using IMemoryOwner<byte>? owner = await content.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                hull = owner is null ? null : Hull(owner.Memory.Span);
            }

            hulls[model] = hull;
        }

        return hulls;
    }

    /// <summary>A studio model's movement hull, from its header.</summary>
    /// <param name="mdl">The model file's bytes (its header suffices).</param>
    /// <returns>The hull, or null when the bytes are not a studio header or the hull is empty.</returns>
    public static (Vec3 Mins, Vec3 Maxs)? Hull(ReadOnlySpan<byte> mdl)
    {
        if (mdl.Length < HullOffset + 24 || !mdl[..4].SequenceEqual(Ident))
        {
            return null;
        }

        Vec3 mins = new(
            BinaryPrimitives.ReadSingleLittleEndian(mdl[HullOffset..]),
            BinaryPrimitives.ReadSingleLittleEndian(mdl[(HullOffset + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(mdl[(HullOffset + 8)..]));
        Vec3 maxs = new(
            BinaryPrimitives.ReadSingleLittleEndian(mdl[(HullOffset + 12)..]),
            BinaryPrimitives.ReadSingleLittleEndian(mdl[(HullOffset + 16)..]),
            BinaryPrimitives.ReadSingleLittleEndian(mdl[(HullOffset + 20)..]));
        return mins.X < maxs.X && mins.Y < maxs.Y && mins.Z < maxs.Z ? (mins, maxs) : null;
    }
}
