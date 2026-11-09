//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapPreview.Render;

namespace SourceSharp.MapPreview.Overlays;

/// <summary>
/// A numbered point of interest drawn into views and listed in the report
/// under the same number, so an agent can point at "[3]" in a picture and find
/// its coordinates without estimating them.
/// </summary>
/// <param name="Number">The number shown, unique within a preview.</param>
/// <param name="Kind">What sort of finding it is (for example <c>missing_material</c>).</param>
/// <param name="Text">One line describing it.</param>
/// <param name="Position">World position.</param>
public sealed record PreviewMarker(int Number, string Kind, string Text, Vec3 Position);

/// <summary>
/// Draws the annotations a vision model needs to read a view correctly: a
/// label naming the view, a sense of scale, and numbered markers.
/// </summary>
/// <remarks>
/// <para>
/// Proportion is what a model misjudges most in a render, so every view gets
/// a scale: orthographic views a labelled bar, perspective views a wireframe
/// figure the size of the player's collision hull standing in the scene, drawn
/// with a depth test so walls in front hide it as they would a player.
/// </para>
/// <para>
/// Everything is drawn with integer pixel steps and the built-in bitmap font,
/// so annotated images are as deterministic as the renders under them.
/// </para>
/// </remarks>
public static class Annotator
{
    /// <summary>The label text colour.</summary>
    public static Rgba LabelInk => new(16, 16, 16);

    /// <summary>The label backing colour.</summary>
    public static Rgba LabelBacking => new(236, 236, 236);

    /// <summary>The scale figure's colour: a saturated orange that real map textures rarely use.</summary>
    public static Rgba FigureColour => new(255, 128, 0);

    /// <summary>A marker's tag colour.</summary>
    public static Rgba MarkerColour => new(255, 230, 0);

    /// <summary>A marker's tag colour when something is in front of the point.</summary>
    public static Rgba HiddenMarkerColour => new(170, 170, 170);

    /// <summary>The scale bar lengths tried, in world units, shortest first.</summary>
    private static ReadOnlySpan<int> BarLengths => [16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384];

    /// <summary>Draws a label across the top-left of an image.</summary>
    /// <param name="image">The image.</param>
    /// <param name="text">The text.</param>
    /// <param name="scale">Font scale.</param>
    public static void Label(RgbaImage image, string text, int scale)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(text);
        BitmapFont.Draw(image, text, 2 * scale, 2 * scale, scale, LabelInk, LabelBacking);
    }

    /// <summary>
    /// Picks the scale bar length: the longest listed length that fits in a
    /// quarter of the image width.
    /// </summary>
    /// <param name="unitsPerPixel">World units per pixel.</param>
    /// <param name="imageWidth">The image width.</param>
    /// <returns>The length in world units and in pixels.</returns>
    public static (int Units, int Pixels) BarLength(float unitsPerPixel, int imageWidth)
    {
        int chosen = BarLengths[0];
        foreach (int length in BarLengths)
        {
            if (length / unitsPerPixel <= imageWidth / 4f)
            {
                chosen = length;
            }
        }

        return (chosen, Math.Max(1, (int)((chosen / unitsPerPixel) + 0.5f)));
    }

    /// <summary>Draws a labelled scale bar at the bottom-left of an orthographic view.</summary>
    /// <param name="image">The image.</param>
    /// <param name="view">The camera view.</param>
    /// <param name="scale">Font scale.</param>
    public static void ScaleBar(RgbaImage image, CameraView view, int scale)
    {
        ArgumentNullException.ThrowIfNull(image);
        (int units, int pixels) = BarLength(view.UnitsPerPixel(0), image.Width);
        int x = 4 * scale;
        int y = image.Height - (14 * scale);
        image.FillRect(x - 1, y - 1, pixels + 2, (3 * scale) + 2, LabelInk);
        image.FillRect(x, y, pixels, 3 * scale, LabelBacking);
        string text = units.ToString(CultureInfo.InvariantCulture) + " UNITS";
        BitmapFont.Draw(image, text, x, y - (9 * scale), scale, LabelInk, LabelBacking);
    }

    /// <summary>
    /// Draws a wireframe box the size of the player's hull with its feet at a
    /// point, hidden where the scene is in front of it.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="view">The camera view.</param>
    /// <param name="buffer">The opaque pass, for the depth test.</param>
    /// <param name="feet">Where the figure stands.</param>
    /// <param name="width">The hull width.</param>
    /// <param name="height">The hull height.</param>
    public static void ScaleFigure(RgbaImage image, CameraView view, GBuffer buffer, Vec3 feet, float width, float height)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(buffer);
        float h = width * 0.5f;
        Span<Vec3> corners = stackalloc Vec3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = feet + new Vec3((i & 1) != 0 ? h : -h, (i & 2) != 0 ? h : -h, (i & 4) != 0 ? height : 0);
        }

        ReadOnlySpan<(int A, int B)> edges = [(0, 1), (1, 3), (3, 2), (2, 0), (4, 5), (5, 7), (7, 6), (6, 4), (0, 4), (1, 5), (2, 6), (3, 7)];
        foreach ((int a, int b) in edges)
        {
            DepthLine(image, view, buffer, corners[a], corners[b], FigureColour);
        }
    }

    /// <summary>Draws numbered markers that fall inside a view.</summary>
    /// <param name="image">The image.</param>
    /// <param name="view">The camera view.</param>
    /// <param name="buffer">The opaque pass, for telling hidden markers from visible ones.</param>
    /// <param name="markers">The markers.</param>
    /// <param name="scale">Font scale.</param>
    /// <returns>The numbers drawn.</returns>
    public static IReadOnlyList<int> Markers(RgbaImage image, CameraView view, GBuffer buffer, IReadOnlyList<PreviewMarker> markers, int scale)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(markers);
        List<int> drawn = [];
        foreach (PreviewMarker marker in markers)
        {
            if (view.Project(marker.Position) is not { } p)
            {
                continue;
            }

            int x = (int)p.X, y = (int)p.Y;
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            {
                continue;
            }

            float depth = buffer.Depth[(y * image.Width) + x];
            bool hidden = p.Depth > depth + MathF.Max(8f, depth * 0.02f);
            if (hidden && !view.Camera.IsOrthographic)
            {
                // A player's-eye view shows only what is in sight; a marker
                // behind a wall there is noise. Overviews keep every marker,
                // greyed when something is over it.
                continue;
            }

            Rgba colour = hidden ? HiddenMarkerColour : MarkerColour;
            string text = marker.Number.ToString(CultureInfo.InvariantCulture);
            int tagX = Math.Clamp(x + (4 * scale), scale, image.Width - BitmapFont.Measure(text, scale) - scale);
            int tagY = Math.Clamp(y - (10 * scale), scale, image.Height - (8 * scale));

            // The leader first, so the dot it starts from stays visible on top.
            Line(image, x, y, tagX, tagY + (4 * scale), LabelInk);
            image.FillRect(x - (2 * scale), y - (2 * scale), 4 * scale, 4 * scale, LabelInk);
            image.FillRect(x - scale, y - scale, 2 * scale, 2 * scale, colour);
            BitmapFont.Draw(image, text, tagX, tagY, scale, LabelInk, colour);
            drawn.Add(marker.Number);
        }

        return drawn;
    }

    /// <summary>Draws a one-pixel line between two pixel positions (Bresenham), clipped to the image.</summary>
    /// <param name="image">The image.</param>
    /// <param name="x0">Start column.</param>
    /// <param name="y0">Start row.</param>
    /// <param name="x1">End column.</param>
    /// <param name="y1">End row.</param>
    /// <param name="colour">The colour.</param>
    public static void Line(RgbaImage image, int x0, int y0, int x1, int y1, Rgba colour)
    {
        ArgumentNullException.ThrowIfNull(image);
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        for (int guard = 0; guard <= dx - dy + 1; guard++)
        {
            image.Set(x0, y0, colour);
            if (x0 == x1 && y0 == y1)
            {
                return;
            }

            int e2 = 2 * error;
            if (e2 >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>
    /// Draws a world-space segment two pixels wide, sampled per pixel along
    /// its screen length, keeping only samples not behind the scene.
    /// </summary>
    private static void DepthLine(RgbaImage image, CameraView view, GBuffer buffer, Vec3 a, Vec3 b, Rgba colour)
    {
        Vec3 va = view.ToView(a), vb = view.ToView(b);
        float near = view.Camera.IsOrthographic ? 0f : PreviewCamera.NearPlane;
        if (va.Z < near && vb.Z < near)
        {
            return;
        }

        if (va.Z < near)
        {
            va += (vb - va) * ((near - va.Z) / (vb.Z - va.Z));
        }
        else if (vb.Z < near)
        {
            vb += (va - vb) * ((near - vb.Z) / (va.Z - vb.Z));
        }

        (float ax, float ay, _) = view.ToScreen(va);
        (float bx, float by, _) = view.ToScreen(vb);
        int steps = (int)MathF.Min(4096f, MathF.Max(MathF.Abs(bx - ax), MathF.Abs(by - ay))) + 1;
        for (int i = 0; i <= steps; i++)
        {
            Vec3 v = va + ((vb - va) * (i / (float)steps));
            (float x, float y, float depth) = view.ToScreen(v);
            int px = (int)x, py = (int)y;
            for (int oy = 0; oy < 2; oy++)
            {
                for (int ox = 0; ox < 2; ox++)
                {
                    int qx = px + ox, qy = py + oy;
                    if (qx < 0 || qy < 0 || qx >= image.Width || qy >= image.Height)
                    {
                        continue;
                    }

                    if (depth <= buffer.Depth[(qy * image.Width) + qx] + 1f)
                    {
                        image.Set(qx, qy, colour);
                    }
                }
            }
        }
    }
}
