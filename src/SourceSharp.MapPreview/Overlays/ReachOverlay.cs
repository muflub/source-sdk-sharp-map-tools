//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapPreview.Render;
using SourceSharp.MapTools.Movement;

namespace SourceSharp.MapPreview.Overlays;

/// <summary>
/// Paints the movement analysis onto a view: a dot on each floor position,
/// coloured by who can reach it, where the view can see it.
/// </summary>
/// <remarks>
/// Green is floor every team's spawns reach; yellow is floor only some teams
/// reach (a spawn room, or one side of a one-way drop); grey is floor no
/// spawn reaches; red is a trap. A dot is drawn only where its floor is the
/// nearest surface in the view, so a roof hides the floor under it as the
/// cutaway overview shows it.
/// </remarks>
public static class ReachOverlay
{
    /// <summary>The colour of floor every team reaches.</summary>
    public static Rgba AllTeams => new(40, 220, 60);

    /// <summary>The colour of floor only some teams reach.</summary>
    public static Rgba SomeTeams => new(250, 220, 40);

    /// <summary>The colour of floor no spawn reaches.</summary>
    public static Rgba Unreached => new(150, 150, 150);

    /// <summary>The colour of a trap.</summary>
    public static Rgba Trap => new(240, 40, 40);

    /// <summary>The colour of floor only explosive jumps reach.</summary>
    public static Rgba ExplosiveOnly => new(60, 140, 255);

    /// <summary>The legend drawn under the view's label.</summary>
    public const string Legend = "GREEN ALL TEAMS  YELLOW SOME  GREY NONE  BLUE BLAST JUMP ONLY  RED TRAP";

    /// <summary>Paints the dots.</summary>
    /// <param name="image">The image.</param>
    /// <param name="view">The camera view.</param>
    /// <param name="buffer">The opaque pass, for the visibility test.</param>
    /// <param name="result">The analysis.</param>
    /// <param name="scale">Dot size multiplier.</param>
    /// <param name="cutAboveZ">The view's cutaway height: floor above it is not drawn, as the geometry above it is not.</param>
    /// <param name="explosiveOnly">Floor nodes only explosive jumps reach, drawn blue, or null.</param>
    /// <returns>How many dots were drawn.</returns>
    public static int Draw(RgbaImage image, CameraView view, GBuffer buffer, ReachabilityResult result, int scale, float? cutAboveZ = null, IReadOnlyCollection<int>? explosiveOnly = null)
    {
        HashSet<int> blast = explosiveOnly is null ? [] : [.. explosiveOnly];
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(result);
        HashSet<int> trapped = [.. result.Traps.SelectMany(t => t.Nodes)];
        int teams = result.ReachedBy.Count;
        int drawn = 0;
        for (int n = 0; n < result.Nodes.Count; n++)
        {
            Vec3 feet = result.Nodes[n];
            if (cutAboveZ is { } cut && feet.Z > cut)
            {
                continue;
            }

            if (view.Project(feet + new Vec3(0, 0, 1)) is not { } p)
            {
                continue;
            }

            int x = (int)p.X, y = (int)p.Y;
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            {
                continue;
            }

            float depth = buffer.Depth[(y * image.Width) + x];
            if (p.Depth > depth + 8f)
            {
                continue;
            }

            int reachedBy = result.ReachedBy.Values.Count(r => r[n]);
            Rgba colour = trapped.Contains(n) ? Trap
                : reachedBy == 0 && blast.Contains(n) ? ExplosiveOnly
                : reachedBy == 0 ? Unreached
                : reachedBy == teams ? AllTeams
                : SomeTeams;
            Blend(image, x - (scale / 2), y - (scale / 2), Math.Max(1, scale), colour);
            drawn++;
        }

        return drawn;
    }

    /// <summary>
    /// Blends a square of colour at <see cref="Opacity"/> over the image, so
    /// the map under a dense field of dots stays readable.
    /// </summary>
    private static void Blend(RgbaImage image, int x0, int y0, int size, Rgba colour)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(image.Height, y0 + size); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(image.Width, x0 + size); x++)
            {
                Rgba under = image.Get(x, y);
                image.Set(x, y, new Rgba(Mix(under.R, colour.R), Mix(under.G, colour.G), Mix(under.B, colour.B)));
            }
        }
    }

    private static byte Mix(byte under, byte over) => (byte)(((under * (100 - Opacity)) + (over * Opacity) + 50) / 100);

    /// <summary>How much of a dot's colour shows over the map, in percent.</summary>
    public const int Opacity = 55;
}
