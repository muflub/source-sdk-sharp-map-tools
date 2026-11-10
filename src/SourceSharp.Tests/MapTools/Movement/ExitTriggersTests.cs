//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Movement;
using SourceSharp.Tests.MapPreview;
using Xunit;

namespace SourceSharp.Tests.MapTools.Movement;

/// <summary>
/// The triggers that take a player out of a place collision alone calls a
/// trap: what counts, what does not, and where each one is.
/// </summary>
public sealed class ExitTriggersTests
{
    private static BspEntity Entity(params (string Key, string Value)[] pairs)
    {
        BspEntity e = new();
        foreach ((string key, string value) in pairs)
        {
            e.Pairs.Add(new BspKeyValue(key, value));
        }

        return e;
    }

    /// <summary>Every brush model is the 64-unit box at the origin; *9 does not exist.</summary>
    private static (Vec3 Mins, Vec3 Maxs)? Box(int index) => index == 9 ? null : (new Vec3(0, 0, 0), new Vec3(64, 64, 64));

    private static IReadOnlyList<ExitTrigger> Of(params BspEntity[] entities) => ExitTriggers.FromEntities(entities, Box);

    [Theory]
    [InlineData("trigger_teleport", "teleports the player")]
    [InlineData("trigger_hurt", "hurts the player")]
    [InlineData("trigger_changelevel", "changes the level")]
    public void TeleportsHurtsAndLevelChangesAreExits(string className, string effect)
    {
        ExitTrigger found = Assert.Single(Of(Entity(("classname", className), ("targetname", "t"), ("target", "dest"), ("model", "*1"))));

        Assert.Equal(effect, found.Effect);
        Assert.Equal($"{className} \"t\" {effect}", found.Describe());
    }

    [Theory]
    [InlineData("OnStartTouch", "pit_exit,Transition,,0,-1", "fires pit_exit.Transition")]
    [InlineData("OnTrigger", "dest\u001bTeleport\u001b\u001b0\u001b-1", "fires dest.Teleport")]
    [InlineData("OnStartTouch", "mover,Activate,,0,-1", "fires mover.Activate")]
    public void ATriggerWhoseTouchFiresATeleportOrTransitionIsAnExit(string output, string value, string effect)
    {
        // "mover" is a point_teleport: its input is not a moving one, its class is.
        IReadOnlyList<ExitTrigger> found = Of(
            Entity(("classname", "trigger_multiple"), ("model", "*2"), (output, value)),
            Entity(("classname", "point_teleport"), ("targetname", "mover")));

        Assert.Equal(effect, Assert.Single(found).Effect);
        Assert.Equal($"trigger_multiple {effect}", found[0].Describe());
    }

    [Fact]
    public void TriggersThatCannotMoveAPlayerAreNotExits()
    {
        IReadOnlyList<ExitTrigger> found = Of(
            Entity(("classname", "trigger_teleport"), ("model", "*1")),                                    // no destination
            Entity(("classname", "trigger_hurt"), ("model", "*1"), ("StartDisabled", "1")),                // off at spawn
            Entity(("classname", "trigger_hurt"), ("model", "*1"), ("spawnflags", "2")),                   // NPCs only
            Entity(("classname", "trigger_hurt"), ("model", "*9")),                                        // no such model
            Entity(("classname", "trigger_hurt"), ("model", "models/x.mdl")),                              // not a brush model
            Entity(("classname", "trigger_hurt"), ("model", "*x")),                                        // not a number
            Entity(("classname", "trigger_hurt")),                                                          // no model
            Entity(("classname", "trigger_multiple"), ("model", "*1"), ("OnStartTouch", "door,Open,,0,-1")), // a touch that moves nothing
            Entity(("classname", "trigger_multiple"), ("model", "*1"), ("OnStartTouch", "broken")),         // a malformed output
            Entity(("classname", "trigger_multiple"), ("model", "*1"), ("OnEndTouch", "d,Teleport,,0,-1")), // not fired on touch
            Entity(("classname", "func_door"), ("model", "*1")));                                           // not a trigger

        Assert.Empty(found);
    }

    [Fact]
    public void AnExitsVolumeIsItsModelsBoxMovedByItsOrigin()
    {
        ExitTrigger moved = Assert.Single(Of(Entity(("classname", "trigger_hurt"), ("model", "*1"), ("origin", "100 0 -50"), ("spawnflags", "1"))));
        ExitTrigger bad = Assert.Single(Of(Entity(("classname", "trigger_hurt"), ("model", "*1"), ("origin", "1 2"))));

        Assert.Equal((new Vec3(100, 0, -50), new Vec3(164, 64, 14)), (moved.Mins, moved.Maxs));
        Assert.Equal("trigger_hurt hurts the player", moved.Describe());
        Assert.Equal(Vec3.Zero, bad.Mins);
    }

    [Theory]
    [InlineData(32, 32, 0, true)]
    [InlineData(32, 32, 64, false)]
    [InlineData(32, 32, -82, false)]
    [InlineData(32, 32, -81, true)]
    [InlineData(-24, 32, 0, false)]
    [InlineData(-23, 32, 0, true)]
    public void APlayerTouchesAnExitWhenTheirBoxesOverlap(float x, float y, float z, bool touches)
    {
        ExitTrigger box = new("trigger_hurt", string.Empty, "hurts the player", Vec3.Zero, new Vec3(64, 64, 64));

        Assert.Equal(touches, box.Touches(new Vec3(x, y, z), new Vec3(-24, -24, 0), new Vec3(24, 24, 82)));
    }

    [Fact]
    public async Task ACompiledMapsTriggerIsReadWithItsBrushModelsBounds()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false,
            new PreviewTestMaps.TestEntity("trigger_teleport", [("targetname", "floor"), ("target", "up"), ("spawnflags", "1")],
                (new Vec3(32, 32, 0), new Vec3(96, 96, 32)), "tools/toolstrigger"),
            new PreviewTestMaps.TestEntity("info_teleport_destination", [("targetname", "up"), ("origin", "128 128 16")]));

        ExitTrigger found = Assert.Single(ExitTriggers.FromBsp(bsp));

        Assert.Equal("trigger_teleport \"floor\" teleports the player", found.Describe());
        Assert.Equal(32, found.Mins.X, 0.5);
        Assert.Equal(96, found.Maxs.Y, 0.5);
    }
}
