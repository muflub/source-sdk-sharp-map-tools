//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;

namespace SourceSharp.MapTools.Nav;

/// <summary>One phase of a navigation build: its name, how long it took, and the CPU the host's clock saw it use.</summary>
/// <param name="Name">The phase (<c>read</c>, <c>geometry</c>, <c>cells</c>, ...).</param>
/// <param name="Wall">The time from the end of the phase before it to its own end.</param>
/// <param name="Cpu">The CPU time the timer's clock moved by over the phase; null when the timer has no clock.</param>
public readonly record struct NavPhaseTime(string Name, TimeSpan Wall, TimeSpan? Cpu);

/// <summary>
/// Where a navigation build's time goes: each phase's wall time (and CPU
/// time, when the host gives a clock), and for the cell phase, the work the
/// cells did between them and the slowest cell. Given in
/// <see cref="BspNavOptions.Timer"/>; it never changes the file.
/// </summary>
/// <remarks>
/// <para>
/// A phase ends at a <see cref="Mark"/>: its wall time runs from the mark
/// before it (or the timer's creation). The build marks its own phases; a
/// host marks the ones around it (reading the map, loading the models,
/// writing the file), so one timer covers the whole command.
/// </para>
/// <para>
/// <b>The CPU clock is the host's.</b> A library has no business reading
/// the process's CPU time (a service runs many builds in one process, and
/// the process's time is theirs together), so the clock is a function the
/// host hands in: <c>ssmap nav3d -timings</c> gives the process's, which is
/// right for a command that runs one build.
/// </para>
/// <para>
/// One timer per build: it holds that build's numbers and nothing else.
/// Cells are timed from the threads that build them, so <see cref="Cell"/>
/// may be called concurrently.
/// </para>
/// </remarks>
public sealed class NavBuildTimer
{
    private readonly Func<TimeSpan>? cpu;
    private readonly long start = Stopwatch.GetTimestamp();
    private readonly List<NavPhaseTime> phases = [];
    private readonly List<(int Index, TimeSpan Time)> cellTimes = [];
    private readonly Lock gate = new();
    private long lastWall;
    private TimeSpan lastCpu;
    private TimeSpan cellWork;
    private TimeSpan slowest;
    private int slowestCell = -1;
    private int cells;

    /// <summary>Creates a timer that starts now.</summary>
    /// <param name="cpu">The CPU clock to read at each mark (a host's process CPU time), or null to time wall clock only.</param>
    public NavBuildTimer(Func<TimeSpan>? cpu = null)
    {
        this.cpu = cpu;
        lastWall = start;
        lastCpu = cpu?.Invoke() ?? TimeSpan.Zero;
    }

    /// <summary>The phases ended so far, in the order they ended.</summary>
    public IReadOnlyList<NavPhaseTime> Phases
    {
        get
        {
            lock (gate)
            {
                return [.. phases];
            }
        }
    }

    /// <summary>The wall time from the timer's creation to its last mark.</summary>
    public TimeSpan Total
    {
        get
        {
            lock (gate)
            {
                return Stopwatch.GetElapsedTime(start, lastWall);
            }
        }
    }

    /// <summary>How many cells were timed.</summary>
    public int Cells
    {
        get
        {
            lock (gate)
            {
                return cells;
            }
        }
    }

    /// <summary>The cells' times added up: the work the cell phase spread over its threads.</summary>
    public TimeSpan CellWork
    {
        get
        {
            lock (gate)
            {
                return cellWork;
            }
        }
    }

    /// <summary>Every timed cell's index and time, in the order they were recorded.</summary>
    public IReadOnlyList<(int Index, TimeSpan Time)> CellTimes
    {
        get
        {
            lock (gate)
            {
                return [.. cellTimes];
            }
        }
    }

    /// <summary>The slowest cell's index and time; index -1 when no cell was timed.</summary>
    public (int Index, TimeSpan Time) SlowestCell
    {
        get
        {
            lock (gate)
            {
                return (slowestCell, slowest);
            }
        }
    }

    /// <summary>Ends a phase: the time since the last mark is its.</summary>
    /// <param name="phase">The phase's name.</param>
    public void Mark(string phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        long now = Stopwatch.GetTimestamp();
        TimeSpan? used = null;
        TimeSpan cpuNow = TimeSpan.Zero;
        if (cpu is not null)
        {
            cpuNow = cpu();
        }

        lock (gate)
        {
            if (cpu is not null)
            {
                used = cpuNow - lastCpu;
                lastCpu = cpuNow;
            }

            phases.Add(new NavPhaseTime(phase, Stopwatch.GetElapsedTime(lastWall, now), used));
            lastWall = now;
        }
    }

    /// <summary>Records one cell's build time; safe to call from several threads at once.</summary>
    /// <param name="index">The cell's index in the grid.</param>
    /// <param name="elapsed">How long it took.</param>
    public void Cell(int index, TimeSpan elapsed)
    {
        lock (gate)
        {
            cells++;
            cellWork += elapsed;
            cellTimes.Add((index, elapsed));
            if (elapsed > slowest || slowestCell < 0)
            {
                slowest = elapsed;
                slowestCell = index;
            }
        }
    }
}
