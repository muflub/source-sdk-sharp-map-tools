//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;
using SourceSharp.MapCompile;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Movement;
using SourceSharp.Tests.MapTools.Movement;
using Xunit;

namespace SourceSharp.Tests.MapCompile;

/// <summary>Facts for <c>ssmap movecal</c>: its arguments, the files it writes, and its comparison's exit codes.</summary>
public sealed class MoveCalCommandTests
{
    [Theory]
    [InlineData(new[] { "write", "/out" }, true, "write", "soldier")]
    [InlineData(new[] { "compare", "/log", "-class", "scout" }, true, "compare", "scout")]
    [InlineData(new[] { "write" }, false, null, "soldier")]
    [InlineData(new[] { "fly", "/out" }, false, null, "soldier")]
    [InlineData(new[] { "write", "/out", "-class", "chef" }, false, "write", "chef")]
    [InlineData(new[] { "write", "/out", "-class" }, false, "write", "soldier")]
    [InlineData(new[] { "write", "/out", "-extra" }, false, "write", "soldier")]
    public void ArgumentsAreAVerbAPathAndAClass(string[] args, bool ok, string? verb, string playerClass)
    {
        Assert.Equal(ok, MoveCalCommand.TryParse(args, out string? parsedVerb, out _, out string parsedClass));
        Assert.Equal(verb, parsedVerb);
        Assert.Equal(playerClass, parsedClass);
    }

    [Fact]
    public async Task WriteLeavesTheMapAndTheScript()
    {
        InMemoryFileSystem disk = new();
        StringWriter output = new();

        int code = await MoveCalCommand.RunAsync(disk, ["write", "/work", "-class", "demoman"], output);

        Assert.Equal(Program.ExitSuccess, code);
        string vmf = Encoding.ASCII.GetString(disk.GetBytes(VPath.Create("/work/ss_movecal.vmf"))!);
        string cfg = Encoding.ASCII.GetString(disk.GetBytes(VPath.Create("/work/movecal.cfg"))!);
        Assert.StartsWith("versioninfo", vmf, StringComparison.Ordinal);
        Assert.Contains("joinclass demoman", cfg, StringComparison.Ordinal);
        Assert.Contains($"{MovementCalibration.Lanes().Count} lanes", output.ToString(), StringComparison.Ordinal);
    }

    [RepoSourceFact(MovementCalibrationTests.RecordedRun)]
    public async Task CompareOfTheRecordedRunSucceeds()
    {
        InMemoryFileSystem disk = new();
        disk.AddText("/run/console.log", MovementCalibrationTests.RecordedLog());
        StringWriter output = new();

        int code = await MoveCalCommand.RunAsync(disk, ["compare", "/run/console.log"], output);

        Assert.Equal(Program.ExitSuccess, code);
        Assert.Contains("lanes agree", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareOfALogMissingLanesFails()
    {
        InMemoryFileSystem disk = new();
        disk.AddText("/run/console.log", "nothing here\n");
        StringWriter output = new();

        int code = await MoveCalCommand.RunAsync(disk, ["compare", "/run/console.log"], output);

        Assert.Equal(RoomCommands.ExitFailed, code);
        Assert.Contains("not in the log", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingLogOrBadArgumentsSayWhy()
    {
        StringWriter missing = new();
        Assert.Equal(RoomCommands.ExitFailed, await MoveCalCommand.RunAsync(new InMemoryFileSystem(), ["compare", "/nowhere.log"], missing));
        Assert.Contains("ssmap movecal: /nowhere.log", missing.ToString(), StringComparison.Ordinal);

        StringWriter usage = new();
        Assert.Equal(Program.ExitUsage, await MoveCalCommand.RunAsync(new InMemoryFileSystem(), ["compare"], usage));
        Assert.Contains("usage: ssmap movecal", usage.ToString(), StringComparison.Ordinal);
    }
}
