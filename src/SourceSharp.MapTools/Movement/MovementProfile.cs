//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Movement;

/// <summary>
/// The numbers a game's player movement runs on: hull sizes, speeds, gravity
/// and the thresholds the movement code tests against.
/// </summary>
/// <remarks>
/// <para>
/// One record per game (and per class where speed differs), so the simulator
/// is the same code for every game and a wrong number is a data fix. The
/// values are the games' shipped defaults (their server variables at their
/// default settings and their class data), and the movement calibration map
/// pins the behaviour they produce.
/// </para>
/// <para>
/// The hull is given as its half-width and heights with the origin at the
/// player's feet, the engine's convention: the standing box runs from
/// (-w, -w, 0) to (w, w, StandHeight).
/// </para>
/// </remarks>
public sealed record MovementProfile
{
    /// <summary>Gets the profile's name (<c>tf2_soldier</c>, <c>hl2mp</c>, ...).</summary>
    public required string Name { get; init; }

    /// <summary>Gets half the hull's width.</summary>
    public float HalfWidth { get; init; } = 16f;

    /// <summary>Gets the standing hull's height.</summary>
    public float StandHeight { get; init; } = 72f;

    /// <summary>Gets the crouched hull's height.</summary>
    public float DuckHeight { get; init; } = 36f;

    /// <summary>Gets the eye height standing, above the feet.</summary>
    public float EyeHeight { get; init; } = 64f;

    /// <summary>Gets the highest step walked up without jumping.</summary>
    public float StepHeight { get; init; } = 18f;

    /// <summary>Gets the gravity, units per second squared.</summary>
    public float Gravity { get; init; } = 800f;

    /// <summary>Gets the ground friction.</summary>
    public float Friction { get; init; } = 4f;

    /// <summary>Gets the speed below which friction acts as if moving at this speed (so the player comes to rest).</summary>
    public float StopSpeed { get; init; } = 100f;

    /// <summary>Gets the ground acceleration.</summary>
    public float Accelerate { get; init; } = 10f;

    /// <summary>Gets the air acceleration.</summary>
    public float AirAccelerate { get; init; } = 10f;

    /// <summary>Gets the cap on the wish speed air acceleration works toward (what makes strafing in the air work as it does).</summary>
    public float AirSpeedCap { get; init; } = 30f;

    /// <summary>Gets the most speed on any axis.</summary>
    public float MaxVelocity { get; init; } = 3500f;

    /// <summary>Gets the running speed.</summary>
    public float MaxSpeed { get; init; } = 320f;

    /// <summary>Gets the speed multiplier while crouched on the ground.</summary>
    public float DuckSpeedScale { get; init; } = 1f / 3f;

    /// <summary>Gets the upward speed a jump starts with.</summary>
    public float JumpSpeed { get; init; } = 268.3281f;

    /// <summary>Gets a value indicating whether a player crouched on the ground can jump.</summary>
    public bool JumpWhileDucked { get; init; } = true;

    /// <summary>Gets the upward speed above which the player is not on the ground even touching it.</summary>
    public float NonJumpVelocity { get; init; } = 140f;

    /// <summary>Gets the steepest ground stood on: a surface whose normal's z is at least this is floor.</summary>
    public float FloorNormalZ { get; init; } = 0.7f;

    /// <summary>Gets the most times a player can crouch in one stretch of air (zero for no limit).</summary>
    public int AirDuckLimit { get; init; }

    /// <summary>Gets a value indicating whether the class can jump once more in the air (TF2's scout).</summary>
    public bool DoubleJump { get; init; }

    /// <summary>The hull's lower corner.</summary>
    /// <param name="ducked">Whether crouched.</param>
    /// <returns>The corner, relative to the feet.</returns>
    public Vec3 Mins(bool ducked) => new(-HalfWidth, -HalfWidth, 0);

    /// <summary>The hull's upper corner.</summary>
    /// <param name="ducked">Whether crouched.</param>
    /// <returns>The corner, relative to the feet.</returns>
    public Vec3 Maxs(bool ducked) => new(HalfWidth, HalfWidth, ducked ? DuckHeight : StandHeight);

    /// <summary>Half-Life 2 Deathmatch's player.</summary>
    public static MovementProfile Hl2mp { get; } = new() { Name = "hl2mp", MaxSpeed = 320f };

    /// <summary>
    /// A TF2 class: the 48 x 48 x 82 hull (62 crouched), the eye at 68, a jump
    /// of 289 units per second (about 52 units high, 72 crouch-jumping), no
    /// jumping while crouched on the ground, two air crouches per jump, the
    /// ground left above 250 units per second upward, and the class's speed.
    /// </summary>
    /// <param name="playerClass">The class name: scout, soldier, pyro, demoman, heavy, engineer, medic, sniper or spy.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="ArgumentException">Not a TF2 class.</exception>
    public static MovementProfile Tf2(string playerClass)
    {
        ArgumentNullException.ThrowIfNull(playerClass);
        float speed = playerClass.ToLowerInvariant() switch
        {
            "scout" => 400f,
            "soldier" => 240f,
            "pyro" => 300f,
            "demoman" or "demo" => 280f,
            "heavy" or "heavyweapons" => 230f,
            "engineer" => 300f,
            "medic" => 320f,
            "sniper" => 300f,
            "spy" => 320f,
            _ => throw new ArgumentException($"\"{playerClass}\" is not a TF2 class", nameof(playerClass)),
        };

        return new MovementProfile
        {
            Name = "tf2_" + playerClass.ToLowerInvariant(),
            HalfWidth = 24f,
            StandHeight = 82f,
            DuckHeight = 62f,
            EyeHeight = 68f,
            MaxSpeed = speed,
            JumpSpeed = 289f,
            JumpWhileDucked = false,
            NonJumpVelocity = 250f,
            AirDuckLimit = 2,
            DoubleJump = playerClass.Equals("scout", StringComparison.OrdinalIgnoreCase),
        };
    }
}
