//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// <see cref="NavBuildTimer"/>: each mark ends a phase, the host's CPU clock
/// is read at each, the cells' times add up with the slowest kept, and a
/// timed build marks its own phases and gives the same file.
/// </summary>
public sealed class NavBuildTimerTests(BspNavMap map) : IClassFixture<BspNavMap>
{
    /// <summary>Each mark ends a phase, in order; the CPU of each is the clock's move since the mark before.</summary>
    [Fact]
    public void EachMarkEndsAPhaseWithTheClocksMoveSinceTheLast()
    {
        TimeSpan now = TimeSpan.FromSeconds(10);
        NavBuildTimer timer = new(() => now);
        now = TimeSpan.FromSeconds(13);
        timer.Mark("one");
        now = TimeSpan.FromSeconds(13.5);
        timer.Mark("two");

        Assert.Equal(["one", "two"], timer.Phases.Select(p => p.Name));
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(0.5)], timer.Phases.Select(p => p.Cpu!.Value));
        Assert.All(timer.Phases, p => Assert.True(p.Wall >= TimeSpan.Zero));
        // Each phase's time is rounded down to whole ticks on its own, so
        // the phases add up to the total less at most a tick each.
        Assert.InRange(timer.Total.Ticks - timer.Phases.Sum(p => p.Wall.Ticks), 0, timer.Phases.Count);
    }

    /// <summary>Without a clock a phase has no CPU time, only its wall time.</summary>
    [Fact]
    public void WithoutAClockAPhaseHasNoCpu()
    {
        NavBuildTimer timer = new();
        timer.Mark("only");
        Assert.Null(Assert.Single(timer.Phases).Cpu);
    }

    /// <summary>The cells' times add up, the slowest is kept with its index, and calls from many threads all count.</summary>
    [Fact]
    public void CellsAddUpAndTheSlowestIsKept()
    {
        NavBuildTimer timer = new();
        Assert.Equal(-1, timer.SlowestCell.Index);
        timer.Cell(4, TimeSpan.FromMilliseconds(5));
        timer.Cell(9, TimeSpan.FromMilliseconds(20));
        timer.Cell(2, TimeSpan.FromMilliseconds(7));
        Assert.Equal((9, TimeSpan.FromMilliseconds(20)), timer.SlowestCell);
        Assert.Equal(TimeSpan.FromMilliseconds(32), timer.CellWork);
        Assert.Equal([(4, TimeSpan.FromMilliseconds(5)), (9, TimeSpan.FromMilliseconds(20)), (2, TimeSpan.FromMilliseconds(7))], timer.CellTimes);

        System.Threading.Tasks.Parallel.For(0, 1000, i => timer.Cell(100 + i, TimeSpan.FromTicks(1)));
        Assert.Equal(1003, timer.Cells);
        Assert.Equal(1003, timer.CellTimes.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(32) + TimeSpan.FromTicks(1000), timer.CellWork);
    }

    /// <summary>A timed build marks its phases in order, times every cell of the grid once, and writes the same bytes as an untimed one.</summary>
    [Fact]
    public void ATimedBuildMarksItsPhasesAndChangesNothing()
    {
        byte[] quiet = Nav3dWriter.Write(map.Build().Level);
        NavBuildTimer timer = new();
        BspNavResult result = map.Build(new BspNavOptions { MaxDegree = 4, Timer = timer });

        Assert.Equal(
            ["brushes", "props", "displacements", "geometry", "play space", "tree", "cells", "stitch", "jumps", "level"],
            timer.Phases.Select(p => p.Name));
        Assert.Equal(result.Level.Columns * result.Level.Rows, timer.Cells);
        Assert.InRange(timer.SlowestCell.Index, 0, timer.Cells - 1);
        Assert.Equal(quiet, Nav3dWriter.Write(result.Level));
    }
}
