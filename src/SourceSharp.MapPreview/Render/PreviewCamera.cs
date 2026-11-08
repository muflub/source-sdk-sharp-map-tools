//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Numerics;

namespace SourceSharp.MapPreview.Render;

/// <summary>What a camera is for, which decides how it is described and labelled.</summary>
public enum CameraKind
{
    /// <summary>Top-down orthographic view of the whole map.</summary>
    Overview,

    /// <summary>Orthographic three-quarter view of the whole map.</summary>
    Isometric,

    /// <summary>Player's-eye view from a spawn point.</summary>
    Spawn,

    /// <summary>View into a room or area.</summary>
    Room,

    /// <summary>View placed by an entity in the map.</summary>
    Authored,

    /// <summary>View the caller asked for explicitly.</summary>
    Custom,

    /// <summary>View aimed at a flagged problem.</summary>
    Problem,
}

/// <summary>
/// A camera: where it is, which way it looks, and how it projects.
/// </summary>
/// <param name="Id">A short identifier, unique within one preview (used in file names and labels).</param>
/// <param name="Kind">What the camera is for.</param>
/// <param name="Position">The eye position.</param>
/// <param name="Forward">The view direction, unit length.</param>
/// <param name="Up">An up hint; made orthogonal to <paramref name="Forward"/> when the basis is built.</param>
/// <param name="FovDegrees">The horizontal field of view for a perspective camera.</param>
/// <param name="OrthoHalfHeight">
/// For an orthographic camera, half the visible height in world units; zero or
/// less means perspective.
/// </param>
/// <param name="Width">The image width in pixels.</param>
/// <param name="Height">The image height in pixels.</param>
/// <param name="Description">One line saying what the view shows, for the label and the report.</param>
public sealed record PreviewCamera(
    string Id,
    CameraKind Kind,
    Vec3 Position,
    Vec3 Forward,
    Vec3 Up,
    float FovDegrees,
    float OrthoHalfHeight,
    int Width,
    int Height,
    string Description)
{
    /// <summary>The nearest a perspective camera draws, in world units.</summary>
    public const float NearPlane = 1f;

    /// <summary>Gets a value indicating whether the camera projects orthographically.</summary>
    public bool IsOrthographic => OrthoHalfHeight > 0;

    /// <summary>
    /// Makes a perspective camera from Source angles: pitch positive looks
    /// down, yaw counter-clockwise from +X, as entity <c>angles</c> keys store them.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="position">The eye position.</param>
    /// <param name="pitch">Pitch in degrees.</param>
    /// <param name="yaw">Yaw in degrees.</param>
    /// <param name="fovDegrees">The horizontal field of view.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="description">What it shows.</param>
    /// <returns>The camera.</returns>
    public static PreviewCamera FromAngles(
        string id, CameraKind kind, Vec3 position, float pitch, float yaw, float fovDegrees, int width, int height, string description) =>
        new(id, kind, position, Direction(pitch, yaw), new Vec3(0, 0, 1), fovDegrees, 0, width, height, description);

    /// <summary>The unit vector Source angles point along.</summary>
    /// <param name="pitch">Pitch in degrees, positive down.</param>
    /// <param name="yaw">Yaw in degrees.</param>
    /// <returns>The direction.</returns>
    public static Vec3 Direction(float pitch, float yaw)
    {
        float p = pitch * (MathF.PI / 180f);
        float y = yaw * (MathF.PI / 180f);
        float cp = DetMathF.Cos(p);
        return new Vec3(cp * DetMathF.Cos(y), cp * DetMathF.Sin(y), -DetMathF.Sin(p));
    }

    /// <summary>The camera's pitch and yaw in Source degrees, recovered from <see cref="Forward"/>.</summary>
    /// <returns>Pitch (positive down) and yaw.</returns>
    public (float Pitch, float Yaw) Angles()
    {
        float pitch = -DetMathF.Asin(Math.Clamp(Forward.Z, -1f, 1f)) * (180f / MathF.PI);
        float yaw = DetMathF.Atan2(Forward.Y, Forward.X) * (180f / MathF.PI);
        return (pitch, yaw);
    }

    /// <summary>Builds the projection used to rasterise.</summary>
    /// <returns>The view.</returns>
    public CameraView View() => new(this);
}

/// <summary>
/// A camera's orthonormal basis and projection constants, and the point
/// transforms the rasteriser and overlays share.
/// </summary>
public readonly struct CameraView
{
    /// <summary>Builds the view of a camera.</summary>
    /// <param name="camera">The camera.</param>
    public CameraView(PreviewCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Camera = camera;
        Forward = Unit(camera.Forward);
        Vec3 right = Vec3.Cross(Forward, camera.Up);
        if (right.LengthSquared() < 1e-8f)
        {
            // Looking straight along the up hint: pick +Y as the screen's up
            // so a top-down view has north at the top.
            right = Vec3.Cross(Forward, new Vec3(0, 1, 0));
        }

        Right = Unit(right);
        Up = Vec3.Cross(Right, Forward);
        float aspect = camera.Height / (float)camera.Width;
        if (camera.IsOrthographic)
        {
            HalfHeight = camera.OrthoHalfHeight;
            HalfWidth = camera.OrthoHalfHeight / aspect;
        }
        else
        {
            HalfWidth = DetMathF.Tan(camera.FovDegrees * 0.5f * (MathF.PI / 180f));
            HalfHeight = HalfWidth * aspect;
        }
    }

    /// <summary>Gets the camera.</summary>
    public PreviewCamera Camera { get; }

    /// <summary>Gets the unit view direction.</summary>
    public Vec3 Forward { get; }

    /// <summary>Gets the unit screen-right direction.</summary>
    public Vec3 Right { get; }

    /// <summary>Gets the unit screen-up direction.</summary>
    public Vec3 Up { get; }

    /// <summary>
    /// Gets half the view's width: in world units for an orthographic camera,
    /// as a tangent (half-width at unit depth) for a perspective one.
    /// </summary>
    public float HalfWidth { get; }

    /// <summary>Gets half the view's height, in the same terms as <see cref="HalfWidth"/>.</summary>
    public float HalfHeight { get; }

    /// <summary>A world point in view space: across, up, and depth along the view direction.</summary>
    /// <param name="world">The point.</param>
    /// <returns>View-space coordinates.</returns>
    public Vec3 ToView(Vec3 world)
    {
        Vec3 d = world - Camera.Position;
        return new Vec3(Vec3.Dot(d, Right), Vec3.Dot(d, Up), Vec3.Dot(d, Forward));
    }

    /// <summary>
    /// A view-space point on screen, in pixels from the top-left, with its
    /// depth; the caller must have clipped points behind a perspective camera.
    /// </summary>
    /// <param name="view">View-space coordinates.</param>
    /// <returns>Pixel x, pixel y and depth.</returns>
    public (float X, float Y, float Depth) ToScreen(Vec3 view)
    {
        float nx, ny;
        if (Camera.IsOrthographic)
        {
            nx = view.X / HalfWidth;
            ny = view.Y / HalfHeight;
        }
        else
        {
            nx = view.X / (view.Z * HalfWidth);
            ny = view.Y / (view.Z * HalfHeight);
        }

        return ((nx + 1f) * 0.5f * Camera.Width, (1f - ny) * 0.5f * Camera.Height, view.Z);
    }

    /// <summary>Projects a world point, or answers null when it is behind a perspective camera.</summary>
    /// <param name="world">The point.</param>
    /// <returns>Pixel x, y and depth, or null.</returns>
    public (float X, float Y, float Depth)? Project(Vec3 world)
    {
        Vec3 view = ToView(world);
        if (!Camera.IsOrthographic && view.Z < PreviewCamera.NearPlane)
        {
            return null;
        }

        return ToScreen(view);
    }

    /// <summary>World units per pixel at a depth (constant for an orthographic camera).</summary>
    /// <param name="depth">The depth.</param>
    /// <returns>The scale.</returns>
    public float UnitsPerPixel(float depth) =>
        Camera.IsOrthographic ? (2f * HalfHeight) / Camera.Height : (2f * HalfHeight * Math.Max(depth, PreviewCamera.NearPlane)) / Camera.Height;

    /// <summary>Whether a surface with this normal at this point faces the camera.</summary>
    /// <param name="point">A point on the surface.</param>
    /// <param name="normal">The surface's normal.</param>
    /// <returns>True when its front side is toward the eye.</returns>
    public bool Faces(Vec3 point, Vec3 normal) =>
        Camera.IsOrthographic ? Vec3.Dot(normal, Forward) < 0 : Vec3.Dot(normal, Camera.Position - point) > 0;

    private static Vec3 Unit(Vec3 v)
    {
        float length = v.Length();
        return length > 0 ? v * (1f / length) : new Vec3(1, 0, 0);
    }
}
