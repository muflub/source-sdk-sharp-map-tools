//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Numerics;

namespace SourceSharp.MapPreview.Scene;

/// <summary>
/// The point and spot lights a model is lit by directly, from the map's
/// world lights: the part of model lighting the ambient cubes do not carry.
/// </summary>
/// <remarks>
/// <para>
/// The engine lights a model with the ambient cube at its centre plus a few
/// of the strongest local lights, evaluated per vertex with no shadowing.
/// Lights with a switchable or flickering style are among them, and those are
/// often the only light in a torch-lit room: the ambient cubes hold style 0
/// alone, so without this term such a room's props come out black.
/// </para>
/// <para>
/// Each light falls off as 1 / (constant + linear d + quadratic d²) and is
/// cut at its radius when it has one; a spot light is full inside its inner
/// cone, zero outside its outer cone, and in between scales by the cone
/// fraction raised to its exponent. A world light's intensity is already
/// linear light (unlike a lightmap sample it carries no 1/255 scale), so
/// intensity times falloff is directly the light reaching the surface, where
/// 1.0 shows a texture at its authored brightness.
/// </para>
/// </remarks>
public sealed class LocalLights
{
    /// <summary>How many lights a model takes, strongest at its centre first, as the engine limits it.</summary>
    public const int PerModel = 4;

    private readonly DWorldLight[] _lights;

    private LocalLights(DWorldLight[] lights) => _lights = lights;

    /// <summary>Gets how many point and spot lights the map has.</summary>
    public int Count => _lights.Length;

    /// <summary>Reads a map's point and spot lights.</summary>
    /// <param name="bsp">The map.</param>
    /// <param name="hdr">Whether to read the HDR set (falling back to LDR when absent).</param>
    /// <returns>The lights.</returns>
    public static LocalLights FromBsp(BspData bsp, bool hdr)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        BspLump lump = hdr && bsp[BspLump.WorldLightsHdr].Data.Length > 0 ? BspLump.WorldLightsHdr : BspLump.WorldLights;
        if (bsp[lump].Data.Length == 0)
        {
            lump = lump == BspLump.WorldLights ? BspLump.WorldLightsHdr : BspLump.WorldLights;
        }

        DWorldLight[] all = BspStructView.As<DWorldLight>(bsp[lump]).ToArray();
        return new LocalLights([.. all.Where(l => l.Type is (int)EmitType.Point or (int)EmitType.Spotlight)]);
    }

    /// <summary>The strongest lights at a point, for one model.</summary>
    /// <param name="centre">The model's centre.</param>
    /// <returns>Up to <see cref="PerModel"/> light indices.</returns>
    public int[] Pick(Vec3 centre)
    {
        List<(float Strength, int Index)> lit = [];
        for (int i = 0; i < _lights.Length; i++)
        {
            float strength = Strength(_lights[i], centre);
            if (strength > 1e-4f)
            {
                lit.Add((strength, i));
            }
        }

        lit.Sort((a, b) => a.Strength != b.Strength ? b.Strength.CompareTo(a.Strength) : a.Index.CompareTo(b.Index));
        return [.. lit.Take(PerModel).Select(l => l.Index)];
    }

    /// <summary>The direct light picked lights give a surface point.</summary>
    /// <param name="picked">The lights from <see cref="Pick"/>.</param>
    /// <param name="point">The point.</param>
    /// <param name="normal">Its unit normal.</param>
    /// <returns>Linear light.</returns>
    public Vec3 At(int[] picked, Vec3 point, Vec3 normal)
    {
        ArgumentNullException.ThrowIfNull(picked);
        Vec3 sum = Vec3.Zero;
        foreach (int i in picked)
        {
            DWorldLight light = _lights[i];
            Vec3 toLight = light.Origin - point;
            float distance = toLight.Length();
            if (distance <= 0)
            {
                continue;
            }

            Vec3 l = toLight * (1f / distance);
            float lambert = Vec3.Dot(normal, l);
            if (lambert <= 0)
            {
                continue;
            }

            sum += light.Intensity * (lambert * Falloff(light, distance) * Cone(light, l));
        }

        return sum;
    }

    private static float Strength(DWorldLight light, Vec3 point)
    {
        Vec3 toLight = light.Origin - point;
        float distance = toLight.Length();
        Vec3 l = distance > 0 ? toLight * (1f / distance) : new Vec3(0, 0, 1);
        float luminance = (0.2126f * light.Intensity.X) + (0.7152f * light.Intensity.Y) + (0.0722f * light.Intensity.Z);
        return luminance * Falloff(light, distance) * Cone(light, l);
    }

    private static float Falloff(DWorldLight light, float distance)
    {
        if (light.Radius > 0 && distance > light.Radius)
        {
            return 0;
        }

        float denominator = light.ConstantAttn + (light.LinearAttn * distance) + (light.QuadraticAttn * distance * distance);
        return denominator > 0 ? 1f / denominator : 0;
    }

    private static float Cone(DWorldLight light, Vec3 towardLight)
    {
        if (light.Type != (int)EmitType.Spotlight)
        {
            return 1;
        }

        float dot = -Vec3.Dot(towardLight, light.Normal);
        if (dot <= light.StopDot2)
        {
            return 0;
        }

        if (dot >= light.StopDot || light.StopDot <= light.StopDot2)
        {
            return 1;
        }

        float fraction = (dot - light.StopDot2) / (light.StopDot - light.StopDot2);
        return light.Exponent > 0 && light.Exponent != 1 ? DetMathF.Pow(fraction, light.Exponent) : fraction;
    }
}
