//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Numerics;

namespace SourceSharp.MapTools.Movement;

/// <summary>One tick's controls, as a player's command carries them.</summary>
/// <param name="Yaw">The view's yaw in degrees; movement is relative to it.</param>
/// <param name="Forward">Forward speed asked for, units per second (negative backwards).</param>
/// <param name="Side">Rightward speed asked for, units per second (negative left).</param>
/// <param name="Jump">Whether jump is held.</param>
/// <param name="Duck">Whether crouch is held.</param>
public readonly record struct MoveInput(float Yaw, float Forward, float Side, bool Jump, bool Duck);

/// <summary>A player's movement state between ticks.</summary>
/// <param name="Origin">The feet.</param>
/// <param name="Velocity">Units per second.</param>
/// <param name="OnGround">Whether standing on floor.</param>
/// <param name="Ducked">Whether the hull is the crouched one.</param>
/// <param name="JumpHeld">Whether jump was held last tick (a jump needs a fresh press).</param>
/// <param name="AirDucks">Crouches made since leaving the ground.</param>
/// <param name="AirJumped">Whether the air jump (scout) has been used since leaving the ground.</param>
public readonly record struct PlayerState(
    Vec3 Origin,
    Vec3 Velocity,
    bool OnGround,
    bool Ducked = false,
    bool JumpHeld = false,
    int AirDucks = 0,
    bool AirJumped = false);

/// <summary>A surface the player touched during a tick, with the speed it cost.</summary>
/// <param name="Normal">The surface's normal.</param>
/// <param name="Brush">The solid touched.</param>
/// <param name="Position">Where the player's feet were.</param>
/// <param name="SpeedLost">How much horizontal speed the tick's move lost, units per second.</param>
public readonly record struct MoveContact(Vec3 Normal, int Brush, Vec3 Position, float SpeedLost);

/// <summary>
/// The Source engine's player movement, one tick at a time, against a
/// <see cref="HullTraceWorld"/>: walking, air control, stepping, sliding
/// along walls and floors, jumping and crouching.
/// </summary>
/// <remarks>
/// <para>
/// A tick applies half its gravity, checks for a jump, applies friction when
/// on the ground, accelerates toward the wished direction (ground or air
/// acceleration), moves, finds the ground again, and applies the other half
/// of the gravity when airborne; the order is the engine's, and so are the
/// rules within each step:
/// </para>
/// <list type="bullet">
/// <item>Friction: speed falls by friction times the larger of the speed and
/// the stop speed, per second, so a walking player stops in a few ticks.</item>
/// <item>Acceleration adds up to accelerate × wish speed × tick toward the
/// wished direction, never pushing past the wish speed along it. In the air
/// the wish speed it aims at is capped (<see cref="MovementProfile.AirSpeedCap"/>)
/// while the amount added is not, which is what lets strafing curve a
/// flight.</item>
/// <item>The slide move tries the whole move; on contact it moves up to the
/// surface, removes the velocity into it, and tries the rest, up to four
/// times. Against two surfaces at once it slides along their crease; against
/// three, or when sliding would turn it back, it stops.</item>
/// <item>A walk that is blocked tries again from one step height up and
/// drops back down, taking whichever went further, unless it lands on
/// ground too steep to stand on.</item>
/// <item>Ground is a surface under the feet, within two units, whose normal
/// is steep enough to stand on, and only while not rising faster than
/// <see cref="MovementProfile.NonJumpVelocity"/>; standing players are
/// pulled down onto ground up to a step below.</item>
/// <item>Crouching in the air raises the feet (the head stays), so a crouch
/// jump clears higher ledges; crouching on the ground slows the walk.</item>
/// </list>
/// <para>
/// Pure arithmetic over floats with <see cref="DetMathF"/> for the view's
/// sine and cosine, so a run of ticks is the same on every machine.
/// </para>
/// </remarks>
public sealed class PlayerMoveSim
{
    /// <summary>The tick length the games run at: 66 ticks a second.</summary>
    public const float TickInterval = 1f / 66f;

    /// <summary>How far below the feet ground is looked for.</summary>
    public const float GroundProbe = 2f;

    private const int MaxClipPlanes = 5;

    /// <summary>Creates a simulator.</summary>
    /// <param name="world">The collision.</param>
    /// <param name="profile">The game's movement numbers.</param>
    public PlayerMoveSim(HullTraceWorld world, MovementProfile profile)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(profile);
        World = world;
        Profile = profile;
    }

    /// <summary>Gets the collision.</summary>
    public HullTraceWorld World { get; }

    /// <summary>Gets the movement numbers.</summary>
    public MovementProfile Profile { get; }

    /// <summary>Places a player standing at a point: on the ground below it, when there is ground within a step.</summary>
    /// <param name="feet">Where to put the feet.</param>
    /// <returns>The state.</returns>
    /// <remarks>
    /// The player is dropped from one unit above the point onto whatever lies
    /// within a step below it, so the feet rest the trace epsilon clear of
    /// the floor as they always do in the game; feet placed exactly on a
    /// surface would count as inside it.
    /// </remarks>
    public PlayerState Spawn(Vec3 feet)
    {
        Vec3 from = feet + new Vec3(0, 0, 1);
        HullTrace drop = World.Trace(from, feet - new Vec3(0, 0, Profile.StepHeight), Profile.Mins(false), Profile.Maxs(false));
        Vec3 at = drop.StartSolid ? feet : drop.Hit ? drop.End : feet;
        return Categorize(new PlayerState(at, Vec3.Zero, false));
    }

    /// <summary>Runs one tick.</summary>
    /// <param name="state">The state before.</param>
    /// <param name="input">The tick's controls.</param>
    /// <param name="contacts">Receives the surfaces touched, or null.</param>
    /// <returns>The state after.</returns>
    public PlayerState Tick(PlayerState state, MoveInput input, List<MoveContact>? contacts = null)
    {
        float dt = TickInterval;
        state = Duck(state, input.Duck);

        // Half the gravity before the move, half after: the engine's split,
        // which makes a jump's arc independent of the tick length.
        Vec3 v = state.Velocity;
        v = new Vec3(v.X, v.Y, v.Z - (Profile.Gravity * 0.5f * dt));
        state = state with { Velocity = Clamp(v) };

        state = Jump(state, input);
        if (state.OnGround)
        {
            state = state with { Velocity = new Vec3(state.Velocity.X, state.Velocity.Y, 0) };
            state = Friction(state);
        }

        (Vec3 wishDir, float wishSpeed) = Wish(state, input);
        state = state.OnGround ? Walk(state, wishDir, wishSpeed, contacts) : Air(state, wishDir, wishSpeed, contacts);
        state = Categorize(state);
        if (!state.OnGround)
        {
            v = state.Velocity;
            state = state with { Velocity = Clamp(new Vec3(v.X, v.Y, v.Z - (Profile.Gravity * 0.5f * dt))) };
        }
        else
        {
            state = state with { Velocity = new Vec3(state.Velocity.X, state.Velocity.Y, 0), AirDucks = 0, AirJumped = false };
        }

        return state with { JumpHeld = input.Jump };
    }

    /// <summary>
    /// Removes the part of a velocity going into a surface, with an
    /// overbounce factor (1 slides along it), and a second pass so float
    /// rounding never leaves the velocity pointing into the surface.
    /// </summary>
    /// <param name="velocity">The velocity.</param>
    /// <param name="normal">The surface's unit normal.</param>
    /// <param name="overbounce">The factor on the removed part.</param>
    /// <returns>The clipped velocity.</returns>
    public static Vec3 ClipVelocity(Vec3 velocity, Vec3 normal, float overbounce)
    {
        float backoff = Vec3.Dot(velocity, normal) * overbounce;
        Vec3 result = velocity - (normal * backoff);
        float adjust = Vec3.Dot(result, normal);
        return adjust < 0 ? result - (normal * adjust) : result;
    }

    private Vec3 Mins(PlayerState s) => Profile.Mins(s.Ducked);

    private Vec3 Maxs(PlayerState s) => Profile.Maxs(s.Ducked);

    private Vec3 Clamp(Vec3 v)
    {
        float m = Profile.MaxVelocity;
        return new Vec3(Math.Clamp(v.X, -m, m), Math.Clamp(v.Y, -m, m), Math.Clamp(v.Z, -m, m));
    }

    private PlayerState Duck(PlayerState s, bool duck)
    {
        float rise = Profile.StandHeight - Profile.DuckHeight;
        if (duck && !s.Ducked)
        {
            if (!s.OnGround)
            {
                if (Profile.AirDuckLimit > 0 && s.AirDucks >= Profile.AirDuckLimit)
                {
                    return s;
                }

                // In the air the feet come up and the head stays where it was.
                Vec3 raised = s.Origin + new Vec3(0, 0, rise);
                return World.IsStuck(raised, Profile.Mins(true), Profile.Maxs(true))
                    ? s
                    : s with { Origin = raised, Ducked = true, AirDucks = s.AirDucks + 1 };
            }

            return s with { Ducked = true };
        }

        if (!duck && s.Ducked)
        {
            // Standing up needs the room to: on the ground the head rises; in
            // the air the feet drop back.
            Vec3 at = s.OnGround ? s.Origin : s.Origin - new Vec3(0, 0, rise);
            return World.IsStuck(at, Profile.Mins(false), Profile.Maxs(false)) ? s : s with { Origin = at, Ducked = false };
        }

        return s;
    }

    private PlayerState Jump(PlayerState s, MoveInput input)
    {
        if (!input.Jump || s.JumpHeld)
        {
            return s;
        }

        if (s.OnGround)
        {
            if (s.Ducked && !Profile.JumpWhileDucked)
            {
                return s;
            }

            // The jump's own half-step of gravity: the engine finishes the
            // tick's gravity at the jump as well as at the end of the tick, so
            // the launch tick loses a whole step, which is what puts a TF2
            // jump's peak at 52 units rather than 54.
            float launch = Profile.JumpSpeed - (Profile.Gravity * 0.5f * TickInterval);
            return s with { Velocity = new Vec3(s.Velocity.X, s.Velocity.Y, launch), OnGround = false };
        }

        if (Profile.DoubleJump && !s.AirJumped)
        {
            // The air jump redirects: horizontal speed becomes the wished
            // direction at running speed, and the climb starts afresh.
            (Vec3 dir, float speed) = Wish(s, input);
            Vec3 horizontal = dir * Math.Min(speed, Profile.MaxSpeed);
            return s with { Velocity = new Vec3(horizontal.X, horizontal.Y, Profile.JumpSpeed), AirJumped = true };
        }

        return s;
    }

    private PlayerState Friction(PlayerState s)
    {
        Vec3 v = s.Velocity;
        float speed = v.Length();
        if (speed < 0.1f)
        {
            return s;
        }

        float control = speed < Profile.StopSpeed ? Profile.StopSpeed : speed;
        float drop = control * Profile.Friction * TickInterval;
        float scale = Math.Max(speed - drop, 0) / speed;
        return s with { Velocity = v * scale };
    }

    private (Vec3 Dir, float Speed) Wish(PlayerState s, MoveInput input)
    {
        float yaw = input.Yaw * (MathF.PI / 180f);
        float c = DetMathF.Cos(yaw), sn = DetMathF.Sin(yaw);
        Vec3 forward = new(c, sn, 0);
        Vec3 right = new(sn, -c, 0);
        Vec3 wish = (forward * input.Forward) + (right * input.Side);
        float speed = wish.Length();
        if (speed <= 0)
        {
            return (Vec3.Zero, 0);
        }

        Vec3 dir = wish * (1f / speed);
        float max = Profile.MaxSpeed * (s.Ducked && s.OnGround ? Profile.DuckSpeedScale : 1f);
        return (dir, Math.Min(speed, max));
    }

    private static Vec3 Accelerate(Vec3 v, Vec3 dir, float wishSpeed, float aimSpeed, float accel)
    {
        float current = Vec3.Dot(v, dir);
        float add = aimSpeed - current;
        if (add <= 0)
        {
            return v;
        }

        float step = Math.Min(accel * TickInterval * wishSpeed, add);
        return v + (dir * step);
    }

    private PlayerState Walk(PlayerState s, Vec3 dir, float wishSpeed, List<MoveContact>? contacts)
    {
        Vec3 v = Accelerate(new Vec3(s.Velocity.X, s.Velocity.Y, 0), dir, wishSpeed, wishSpeed, Profile.Accelerate);
        v = new Vec3(v.X, v.Y, 0);
        if (v.Length() < 1f)
        {
            return s with { Velocity = Vec3.Zero };
        }

        s = s with { Velocity = v };
        Vec3 dest = s.Origin + (v * TickInterval);
        HullTrace direct = World.Trace(s.Origin, dest, Mins(s), Maxs(s));
        if (!direct.Hit)
        {
            return StayOnGround(s with { Origin = direct.End });
        }

        return StayOnGround(StepMove(s, contacts));
    }

    private PlayerState Air(PlayerState s, Vec3 dir, float wishSpeed, List<MoveContact>? contacts)
    {
        Vec3 v = Accelerate(s.Velocity, dir, wishSpeed, Math.Min(wishSpeed, Profile.AirSpeedCap), Profile.AirAccelerate);
        return SlideMove(s with { Velocity = v }, contacts);
    }

    /// <summary>
    /// A blocked walk: the plain slide, and the same slide lifted a step and
    /// dropped back, keeping whichever covered more ground (the lifted one
    /// only when it lands on standable ground).
    /// </summary>
    private PlayerState StepMove(PlayerState s, List<MoveContact>? contacts)
    {
        PlayerState down = SlideMove(s, contacts);

        Vec3 mins = Mins(s), maxs = Maxs(s);
        HullTrace lift = World.Trace(s.Origin, s.Origin + new Vec3(0, 0, Profile.StepHeight), mins, maxs);
        if (lift.AllSolid || lift.StartSolid)
        {
            return down;
        }

        PlayerState up = SlideMove(s with { Origin = lift.End }, null);
        HullTrace drop = World.Trace(up.Origin, up.Origin - new Vec3(0, 0, Profile.StepHeight), mins, maxs);
        if (drop.AllSolid || (drop.Hit && drop.Normal.Z < Profile.FloorNormalZ))
        {
            return down;
        }

        up = up with { Origin = drop.End };
        float downDist = Horizontal(down.Origin - s.Origin);
        float upDist = Horizontal(up.Origin - s.Origin);
        if (downDist >= upDist)
        {
            return down;
        }

        // Stepping up keeps the lower move's vertical speed.
        return up with { Velocity = new Vec3(up.Velocity.X, up.Velocity.Y, down.Velocity.Z) };
    }

    private PlayerState StayOnGround(PlayerState s)
    {
        Vec3 mins = Mins(s), maxs = Maxs(s);
        HullTrace up = World.Trace(s.Origin, s.Origin + new Vec3(0, 0, GroundProbe), mins, maxs);
        HullTrace down = World.Trace(up.End, s.Origin - new Vec3(0, 0, Profile.StepHeight), mins, maxs);
        if (down.Fraction > 0 && down.Fraction < 1 && !down.StartSolid && down.Normal.Z >= Profile.FloorNormalZ
            && MathF.Abs(s.Origin.Z - down.End.Z) > 0.5f * DistEpsilonUnit)
        {
            return s with { Origin = down.End };
        }

        return s;
    }

    private const float DistEpsilonUnit = 1f / 32f;

    /// <summary>The engine's four-bump slide move.</summary>
    private PlayerState SlideMove(PlayerState s, List<MoveContact>? contacts)
    {
        Vec3 mins = Mins(s), maxs = Maxs(s);
        Vec3 origin = s.Origin;
        Vec3 velocity = s.Velocity;
        Vec3 primal = velocity, original = velocity;
        Span<Vec3> planes = stackalloc Vec3[MaxClipPlanes];
        int planeCount = 0;
        float timeLeft = TickInterval;
        float allFraction = 0;
        float startSpeed = Horizontal(velocity);

        for (int bump = 0; bump < 4; bump++)
        {
            if (velocity.LengthSquared() == 0)
            {
                break;
            }

            Vec3 end = origin + (velocity * timeLeft);
            HullTrace trace = World.Trace(origin, end, mins, maxs);
            allFraction += trace.Fraction;
            if (trace.AllSolid)
            {
                return s with { Origin = origin, Velocity = Vec3.Zero };
            }

            if (trace.Fraction > 0)
            {
                origin = trace.End;
                original = velocity;
                planeCount = 0;
            }

            if (trace.Fraction >= 1)
            {
                break;
            }

            contacts?.Add(new MoveContact(trace.Normal, trace.Brush, origin, 0));
            timeLeft -= timeLeft * trace.Fraction;
            if (planeCount >= MaxClipPlanes)
            {
                velocity = Vec3.Zero;
                break;
            }

            planes[planeCount++] = trace.Normal;

            if (planeCount == 1 && !s.OnGround)
            {
                // A single surface in the air: slide along it (an overbounce
                // of exactly one, as on every surface with normal friction).
                velocity = ClipVelocity(original, planes[0], 1f);
                original = velocity;
            }
            else
            {
                int i;
                for (i = 0; i < planeCount; i++)
                {
                    velocity = ClipVelocity(original, planes[i], 1f);
                    int j;
                    for (j = 0; j < planeCount; j++)
                    {
                        if (j != i && Vec3.Dot(velocity, planes[j]) < 0)
                        {
                            break;
                        }
                    }

                    if (j == planeCount)
                    {
                        break;
                    }
                }

                if (i == planeCount)
                {
                    if (planeCount != 2)
                    {
                        velocity = Vec3.Zero;
                        break;
                    }

                    Vec3 crease = Vec3.Cross(planes[0], planes[1]);
                    float length = crease.Length();
                    crease = length > 0 ? crease * (1f / length) : Vec3.Zero;
                    velocity = crease * Vec3.Dot(crease, velocity);
                }

                if (Vec3.Dot(velocity, primal) <= 0)
                {
                    velocity = Vec3.Zero;
                    break;
                }
            }
        }

        if (allFraction == 0)
        {
            velocity = Vec3.Zero;
        }

        if (contacts is { Count: > 0 })
        {
            float lost = Math.Max(0, startSpeed - Horizontal(velocity));
            MoveContact last = contacts[^1];
            contacts[^1] = last with { SpeedLost = lost };
        }

        return s with { Origin = origin, Velocity = velocity };
    }

    private PlayerState Categorize(PlayerState s)
    {
        if (s.Velocity.Z > Profile.NonJumpVelocity)
        {
            return s with { OnGround = false };
        }

        HullTrace probe = World.Trace(s.Origin, s.Origin - new Vec3(0, 0, GroundProbe), Mins(s), Maxs(s));
        bool ground = probe.Hit && !probe.AllSolid && probe.Normal.Z >= Profile.FloorNormalZ;
        if (!ground)
        {
            return s with { OnGround = false };
        }

        return s with { OnGround = true, Origin = probe.StartSolid ? s.Origin : probe.End };
    }

    private static float Horizontal(Vec3 v) => MathF.Sqrt((v.X * v.X) + (v.Y * v.Y));
}
