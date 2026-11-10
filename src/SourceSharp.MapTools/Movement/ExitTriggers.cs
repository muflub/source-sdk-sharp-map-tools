//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Movement;

/// <summary>A trigger volume that takes a player who touches it out of where they stand.</summary>
/// <param name="ClassName">The trigger's class.</param>
/// <param name="Name">Its targetname, or empty.</param>
/// <param name="Effect">What it does to the player, as a phrase ("teleports the player").</param>
/// <param name="Mins">Its brush model's lower corner, in the world.</param>
/// <param name="Maxs">Its brush model's upper corner, in the world.</param>
public sealed record ExitTrigger(string ClassName, string Name, string Effect, Vec3 Mins, Vec3 Maxs)
{
    /// <summary>The trigger and what it does, for a report: <c>trigger_teleport "pit_floor" teleports the player</c>.</summary>
    /// <returns>The phrase.</returns>
    public string Describe() => Name.Length > 0 ? $"{ClassName} \"{Name}\" {Effect}" : $"{ClassName} {Effect}";

    /// <summary>Whether a player's box standing at a position overlaps the trigger's bounds.</summary>
    /// <param name="feet">Where the player stands.</param>
    /// <param name="mins">The box's lower corner, relative to the feet.</param>
    /// <param name="maxs">The box's upper corner, relative to the feet.</param>
    /// <returns>True when they overlap.</returns>
    public bool Touches(Vec3 feet, Vec3 mins, Vec3 maxs) =>
        feet.X + maxs.X > Mins.X && feet.X + mins.X < Maxs.X
        && feet.Y + maxs.Y > Mins.Y && feet.Y + mins.Y < Maxs.Y
        && feet.Z + maxs.Z > Mins.Z && feet.Z + mins.Z < Maxs.Z;
}

/// <summary>
/// The triggers in a compiled map that move a player out of a place the
/// movement simulation alone calls a trap: teleports, hurt triggers,
/// level changes, and any trigger whose touch fires a teleport or a level
/// transition.
/// </summary>
/// <remarks>
/// The movement simulation sees only collision, so a pit whose way out is
/// a trigger (a fall into a shaft that sends the player to the next level,
/// a floor that teleports them back up, a kill pit) reads as a trap. A map
/// reviewer needs to tell those intended pits from places a player is
/// really stuck, so traps are checked against these volumes. A trigger
/// counts only when it can act on players: enabled at spawn, and with the
/// clients flag when it states its flags. Its volume is its brush model's
/// bounding box, which is exact for the box-shaped triggers maps use and
/// errs towards touching otherwise.
/// </remarks>
public static class ExitTriggers
{
    /// <summary>The <c>spawnflags</c> bit that lets a trigger act on players.</summary>
    private const int ClientsFlag = 1;

    /// <summary>Whether an output is one a trigger fires when a player touches it.</summary>
    private static bool IsTouchOutput(string output) =>
        output.ToLowerInvariant() is "onstarttouch" or "onstarttouchall" or "ontrigger" or "ontouching";

    /// <summary>Whether an input takes whoever fired it somewhere else.</summary>
    private static bool IsMovingInput(string input) =>
        input.ToLowerInvariant() is "teleport" or "transition" or "changelevel" or "hurt";

    /// <summary>Whether an entity class's job is to take a player somewhere else.</summary>
    private static bool IsMovingClass(string className) =>
        className.ToLowerInvariant() is "point_teleport" or "logic_level_transition" or "trigger_changelevel" or "point_hurt";

    /// <summary>Reads a compiled map's exit triggers.</summary>
    /// <param name="bsp">The map.</param>
    /// <returns>The triggers, in entity order.</returns>
    public static IReadOnlyList<ExitTrigger> FromBsp(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        DModel[] models = [.. BspStructView.As<DModel>(bsp[BspLump.Models])];
        return FromEntities(EntityLump.Parse(bsp[BspLump.Entities]), i => i > 0 && i < models.Length ? (models[i].Mins, models[i].Maxs) : null);
    }

    /// <summary>Finds the exit triggers among entities.</summary>
    /// <param name="entities">The map's entities.</param>
    /// <param name="brushModel">A brush model's bounds by index (<c>*N</c>), or null when there is none.</param>
    /// <returns>The triggers, in entity order.</returns>
    internal static IReadOnlyList<ExitTrigger> FromEntities(IReadOnlyList<BspEntity> entities, Func<int, (Vec3 Mins, Vec3 Maxs)?> brushModel)
    {
        Dictionary<string, string> classOf = new(StringComparer.OrdinalIgnoreCase);
        foreach (BspEntity e in entities)
        {
            if (e.Get("targetname") is { Length: > 0 } name)
            {
                classOf.TryAdd(name, e.ClassName);
            }
        }

        List<ExitTrigger> found = [];
        foreach (BspEntity e in entities)
        {
            if (!e.ClassName.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase)
                || e.Get("StartDisabled") == "1"
                || (e.Get("spawnflags") is { } flags && int.TryParse(flags, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) && (f & ClientsFlag) == 0)
                || e.Get("model") is not { Length: > 1 } model || model[0] != '*'
                || !int.TryParse(model.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                || brushModel(index) is not { } bounds)
            {
                continue;
            }

            if (EffectOf(e, classOf) is not { } effect)
            {
                continue;
            }

            Vec3 origin = ParseVector(e.Get("origin"));
            found.Add(new ExitTrigger(e.ClassName, e.Get("targetname") ?? string.Empty, effect, bounds.Mins + origin, bounds.Maxs + origin));
        }

        return found;
    }

    /// <summary>What a trigger does to a player who touches it, or null when it does not move them.</summary>
    private static string? EffectOf(BspEntity e, Dictionary<string, string> classOf)
    {
        switch (e.ClassName.ToLowerInvariant())
        {
            case "trigger_teleport" when e.Get("target") is { Length: > 0 }:
                return "teleports the player";
            case "trigger_hurt":
                return "hurts the player";
            case "trigger_changelevel":
                return "changes the level";
        }

        foreach (BspKeyValue pair in e.Pairs)
        {
            if (!IsTouchOutput(pair.Key))
            {
                continue;
            }

            // An output is "target,input,parameter,delay,times"; newer
            // compilers separate the fields with ESC instead of commas.
            string[] fields = pair.Value.Split(pair.Value.Contains('\u001b', StringComparison.Ordinal) ? '\u001b' : ',');
            if (fields.Length < 2)
            {
                continue;
            }

            string target = fields[0], input = fields[1];
            if (IsMovingInput(input) || (classOf.TryGetValue(target, out string? targetClass) && IsMovingClass(targetClass)))
            {
                return $"fires {target}.{input}";
            }
        }

        return null;
    }

    private static Vec3 ParseVector(string? text)
    {
        if (text is null)
        {
            return Vec3.Zero;
        }

        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
            ? new Vec3(x, y, z)
            : Vec3.Zero;
    }
}
