//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Nav;

/// <summary>Where an agent's origin is taken to be when a voxel is asked whether it fits.</summary>
public enum Nav3dFit
{
    /// <summary>Anywhere in the voxel (<see cref="Nav3dReader.Passable"/>, <see cref="Nav3dReader.Standable"/>): the file's own meaning of a fit.</summary>
    Anywhere,

    /// <summary>At the voxel's horizontal centre, the feet on its floor (<see cref="Nav3dReader.FitsAtCentre"/>).</summary>
    Centre,
}

/// <summary>A walking agent: its box standing and crouched, its clip class, and how a voxel's fit is asked.</summary>
/// <param name="Width">The box's width and depth.</param>
/// <param name="StandHeight">Its height standing.</param>
/// <param name="CrouchHeight">Its height crouched: a floor it fits on only crouched is still one it reaches.</param>
/// <param name="ClipClass">Which clip brushes stop it.</param>
/// <param name="Fit">Where its origin is taken to be in a voxel.</param>
public sealed record Nav3dWalker(float Width, float StandHeight, float CrouchHeight, Nav3dClipClass ClipClass, Nav3dFit Fit)
{
    /// <summary>The Source player: 32 wide, 72 standing, 36 crouched, the player clip class, fitted at the voxel's centre.</summary>
    public static Nav3dWalker Player => new(32, 72, 36, Nav3dClipClass.Player, Nav3dFit.Centre);
}

/// <summary>
/// Which leaves a walking agent can get between: a reference derivation over
/// a reader's answers of the graph a walking bot plans on, for checking a
/// navigation and for a game to compare its own graph with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Places.</b> A leaf is a place to <b>stand</b> when its floor is
/// walkable and the agent fits on it standing or crouched, and a place to
/// <b>climb</b> when it is a ladder leaf the agent fits in crouched at its
/// bottom voxel (a climbing player's box is held against the ladder, where
/// the file marks ladder leaves). Every other leaf is not a place.
/// </para>
/// <para>
/// <b>Moves</b>, each both ways:
/// </para>
/// <list type="bullet">
/// <item><description><b>walk</b>: a place to stand and one in a column beside it (east, north, west, south) whose floors are within the step height;</description></item>
/// <item><description><b>jump</b>: the file's jump links of the agent's class between two places to stand;</description></item>
/// <item><description><b>climb</b>: a place to climb and the places to stand or climb right above or below it in its column;</description></item>
/// <item><description>
/// <b>mount and dismount</b>: a place to climb and a place to stand whose
/// floor is within the step height of the climb's span, in a column the
/// agent's box reaches from the climb's column: within its half-width and
/// half a voxel, so a ladder's top meets the floor it leads to across the
/// gap a box held against the ladder leaves.
/// </description></item>
/// </list>
/// <para>
/// A drop deeper than a jump link is one way, so it joins nothing here. Every
/// obstacle is open unless <c>blocking</c> says otherwise. Components are
/// numbered in order of their first leaf, so the answer is the same on every
/// run.
/// </para>
/// </remarks>
public static class Nav3dWalk
{
    /// <summary>Each leaf's walking component, -1 for a leaf that is no place to stand or climb.</summary>
    /// <param name="nav">The navigation.</param>
    /// <param name="walker">The agent.</param>
    /// <param name="blocking">Per dynamic obstacle, whether it is in the way; empty for every obstacle open.</param>
    /// <returns>The components, and how many there are.</returns>
    public static (int[] Component, int Count) Components(Nav3dReader nav, Nav3dWalker walker, ReadOnlySpan<bool> blocking = default)
    {
        ArgumentNullException.ThrowIfNull(nav);
        ArgumentNullException.ThrowIfNull(walker);
        int leaves = nav.LeafCount;
        bool[] stand = new bool[leaves];
        bool[] climb = new bool[leaves];
        for (int l = 0; l < leaves; l++)
        {
            stand[l] = IsStand(nav, walker, l, blocking);
            climb[l] = IsClimb(nav, walker, l, blocking);
        }

        int[] parent = new int[leaves];
        for (int l = 0; l < leaves; l++)
        {
            parent[l] = l;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            int ra = Find(a);
            int rb = Find(b);
            if (ra != rb)
            {
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        double s = nav.VoxelSize;
        int reach = (int)Math.Ceiling(((walker.Width * 0.5) + (s * 0.5)) / s);
        for (int l = 0; l < leaves; l++)
        {
            if (!stand[l] && !climb[l])
            {
                continue;
            }

            (int gx, int gy) = Global(nav, l);
            Nav3dLeaf leaf = nav.Leaf(l);
            if (stand[l])
            {
                float floor = leaf.FloorZ(walker.ClipClass);
                for (int d = 0; d < 4; d++)
                {
                    (int dx, int dy) = d switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
                    (int first, int end) = Column(nav, gx + dx, gy + dy);
                    for (int o = first; o < end; o++)
                    {
                        if (stand[o] && Math.Abs(nav.Leaf(o).FloorZ(walker.ClipClass) - floor) <= nav.StepHeight)
                        {
                            Union(l, o);
                        }
                    }
                }
            }

            if (!climb[l])
            {
                continue;
            }

            // Up and down the column.
            int below = nav.LeafBelow(l);
            if (below >= 0 && (stand[below] || climb[below]))
            {
                Union(l, below);
            }

            if (l + 1 < leaves && nav.LeafBelow(l + 1) == l && (stand[l + 1] || climb[l + 1]))
            {
                Union(l, l + 1);
            }

            // On and off: floors the box reaches at the climb's span.
            double bottom = nav.Origin.Z + (leaf.ZLo * s) - nav.StepHeight;
            double top = nav.Origin.Z + ((leaf.ZHi + 1) * s) + nav.StepHeight;
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    (int first, int end) = Column(nav, gx + dx, gy + dy);
                    for (int o = first; o < end; o++)
                    {
                        float floor = nav.Leaf(o).FloorZ(walker.ClipClass);
                        if (stand[o] && floor >= bottom && floor <= top)
                        {
                            Union(l, o);
                        }
                    }
                }
            }
        }

        byte bit = (byte)(walker.ClipClass == Nav3dClipClass.Npc ? 2 : 1);
        for (int j = 0; j < nav.JumpCount; j++)
        {
            Nav3dJump jump = nav.Jump(j);
            if ((jump.ClassMask & bit) != 0 && stand[jump.LeafA] && stand[jump.LeafB])
            {
                Union((int)jump.LeafA, (int)jump.LeafB);
            }
        }

        int[] component = new int[leaves];
        int[] number = new int[leaves];
        Array.Fill(number, -1);
        int count = 0;
        for (int l = 0; l < leaves; l++)
        {
            if (!stand[l] && !climb[l])
            {
                component[l] = -1;
                continue;
            }

            int root = Find(l);
            if (number[root] < 0)
            {
                number[root] = count++;
            }

            component[l] = number[root];
        }

        return (component, count);
    }

    /// <summary>Whether a leaf is a place the agent stands, standing or crouched.</summary>
    /// <param name="nav">The navigation.</param>
    /// <param name="walker">The agent.</param>
    /// <param name="leaf">The leaf.</param>
    /// <param name="blocking">Per dynamic obstacle, whether it is in the way; empty for none.</param>
    /// <returns>True when it stands there.</returns>
    public static bool IsStand(Nav3dReader nav, Nav3dWalker walker, int leaf, ReadOnlySpan<bool> blocking = default)
    {
        ArgumentNullException.ThrowIfNull(nav);
        ArgumentNullException.ThrowIfNull(walker);
        if (!nav.Leaf(leaf).IsWalkable(walker.ClipClass))
        {
            return false;
        }

        return walker.Fit == Nav3dFit.Centre
            ? nav.StandableAtCentre(leaf, walker.Width, walker.StandHeight, walker.ClipClass, blocking)
                || nav.StandableAtCentre(leaf, walker.Width, walker.CrouchHeight, walker.ClipClass, blocking)
            : nav.Standable(leaf, nav.Leaf(leaf).ZLo, walker.Width, walker.StandHeight, walker.ClipClass, blocking)
                || nav.Standable(leaf, nav.Leaf(leaf).ZLo, walker.Width, walker.CrouchHeight, walker.ClipClass, blocking);
    }

    /// <summary>Whether a leaf is a place the agent climbs: a ladder leaf it fits in crouched at its bottom voxel.</summary>
    /// <param name="nav">The navigation.</param>
    /// <param name="walker">The agent.</param>
    /// <param name="leaf">The leaf.</param>
    /// <param name="blocking">Per dynamic obstacle, whether it is in the way; empty for none.</param>
    /// <returns>True when it climbs there.</returns>
    public static bool IsClimb(Nav3dReader nav, Nav3dWalker walker, int leaf, ReadOnlySpan<bool> blocking = default)
    {
        ArgumentNullException.ThrowIfNull(nav);
        ArgumentNullException.ThrowIfNull(walker);
        Nav3dLeaf l = nav.Leaf(leaf);
        if ((l.Flags & Nav3dLeafFlags.Ladder) == 0)
        {
            return false;
        }

        return walker.Fit == Nav3dFit.Centre
            ? nav.PassableAtCentre(leaf, l.ZLo, walker.Width, walker.CrouchHeight, walker.ClipClass, blocking)
            : nav.Passable(leaf, l.ZLo, walker.Width, walker.CrouchHeight, walker.ClipClass, blocking);
    }

    /// <summary>A leaf's column in voxel columns across the whole grid.</summary>
    private static (int X, int Y) Global(Nav3dReader nav, int leaf)
    {
        (int cell, int x, int y) = nav.LeafColumn(leaf);
        return (((cell % nav.Columns) * nav.CellVoxels) + x, ((cell / nav.Columns) * nav.CellVoxels) + y);
    }

    /// <summary>The leaves of a grid-wide voxel column, empty outside the grid.</summary>
    private static (int First, int End) Column(Nav3dReader nav, int gx, int gy)
    {
        int n = nav.CellVoxels;
        if (gx < 0 || gy < 0 || gx >= nav.Columns * n || gy >= nav.Rows * n)
        {
            return (0, 0);
        }

        return nav.ColumnLeaves(((gy / n) * nav.Columns) + (gx / n), gx % n, gy % n);
    }
}
