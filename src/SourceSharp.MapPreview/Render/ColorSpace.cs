//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Numerics;

namespace SourceSharp.MapPreview.Render;

/// <summary>
/// Conversions between 8-bit sRGB and linear light, as lookup tables built
/// once per instance.
/// </summary>
/// <remarks>
/// <para>
/// Textures are authored in sRGB and lighting is linear, so every shaded pixel
/// converts twice. The curve needs a power function; going through
/// <see cref="DetMathF.Pow"/> keeps the result identical on every OS, and the
/// tables keep that cost to building them. The tables are instance fields,
/// not statics, because the libraries hold no mutable static state; one
/// instance is shared, read-only, by every thread of a render.
/// </para>
/// <para>
/// Encoding quantises linear input to <see cref="EncodeSteps"/> steps over
/// 0..1, fine enough that adjacent output bytes are never skipped in the dark
/// range where the curve is steepest.
/// </para>
/// </remarks>
public sealed class ColorSpace
{
    /// <summary>The number of steps the encode table divides 0..1 into.</summary>
    public const int EncodeSteps = 8192;

    private readonly float[] _toLinear = new float[256];
    private readonly byte[] _toSrgb = new byte[EncodeSteps + 1];

    /// <summary>Builds the tables.</summary>
    public ColorSpace()
    {
        for (int i = 0; i < 256; i++)
        {
            _toLinear[i] = SrgbToLinear(i / 255f);
        }

        for (int i = 0; i <= EncodeSteps; i++)
        {
            float encoded = LinearToSrgb(i / (float)EncodeSteps);
            _toSrgb[i] = (byte)Math.Clamp((int)((encoded * 255f) + 0.5f), 0, 255);
        }
    }

    /// <summary>Converts an 8-bit sRGB channel to linear.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>Linear light, 0..1.</returns>
    public float ToLinear(byte channel) => _toLinear[channel];

    /// <summary>Converts linear light to an 8-bit sRGB channel, clamping to 0..1 first.</summary>
    /// <param name="linear">Linear light.</param>
    /// <returns>The channel.</returns>
    public byte ToSrgb(float linear)
    {
        if (!(linear > 0f))
        {
            return 0;
        }

        if (linear >= 1f)
        {
            return 255;
        }

        return _toSrgb[(int)((linear * EncodeSteps) + 0.5f)];
    }

    /// <summary>The sRGB decode curve.</summary>
    /// <param name="c">An encoded value, 0..1.</param>
    /// <returns>The linear value.</returns>
    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : DetMathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>The sRGB encode curve.</summary>
    /// <param name="l">A linear value, 0..1.</param>
    /// <returns>The encoded value.</returns>
    public static float LinearToSrgb(float l) =>
        l <= 0.0031308f ? l * 12.92f : (1.055f * DetMathF.Pow(l, 1f / 2.4f)) - 0.055f;
}
