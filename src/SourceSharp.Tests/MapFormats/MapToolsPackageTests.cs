//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// The <c>SourceSharp.MapTools</c> and <c>SourceSharp.RoomContracts</c>
/// packages the game server restores to build a map's navigation in its own
/// process: each, packed from the assembly this suite was built with, holds
/// that assembly and its documentation and nothing else, and declares exactly
/// the dependencies it should (the contracts none; MapTools the formats, the
/// contracts and YamlDotNet), so nothing of the CLI, the GPU backend or the
/// SQLite cache reaches the game.
/// </summary>
public sealed partial class MapToolsPackageTests
{
    [RepoSourceFact("src/SourceSharp.MapTools/SourceSharp.MapTools.csproj")]
    public Task TheMapToolsPackageHoldsItsAssemblyAndDependsOnTheFormatsTheContractsAndYaml() =>
        CheckAsync("SourceSharp.MapTools", ["SourceSharp.MapFormats", "SourceSharp.RoomContracts", "YamlDotNet"]);

    [RepoSourceFact("src/SourceSharp.RoomContracts/SourceSharp.RoomContracts.csproj")]
    public Task TheRoomContractsPackageHoldsItsAssemblyAndNoDependency() => CheckAsync("SourceSharp.RoomContracts", []);

    private static async Task CheckAsync(string name, string[] dependencies)
    {
        string project = RepoSourceFactAttribute.Find($"src/{name}/{name}.csproj")!;
        string version = XDocument.Load(project).Descendants("Version").Single().Value;
        string configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
        string output = Path.Combine(Path.GetTempPath(), "maptools-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProcessStartInfo start = new(Dotnet())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string arg in new[] { "pack", project, "--no-build", "--no-restore", "-c", configuration, "-o", output, "--nologo" })
            {
                start.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await stdout + await stderr);

            string package = Path.Combine(output, $"{name}.{version}.nupkg");
            Assert.True(File.Exists(package), $"no {package}; the folder holds {string.Join(", ", Directory.GetFiles(output))}");
            using ZipArchive zip = ZipFile.OpenRead(package);
            string[] libraries = [.. zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("lib/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
            Assert.Equal([$"lib/net10.0/{name}.dll", $"lib/net10.0/{name}.xml"], libraries);

            using StreamReader nuspec = new(zip.GetEntry($"{name}.nuspec")!.Open());
            string text = await nuspec.ReadToEndAsync();
            Assert.Contains($"<id>{name}</id>", text, StringComparison.Ordinal);
            Assert.Contains($"<version>{version}</version>", text, StringComparison.Ordinal);
            Assert.Equal(dependencies.Order(StringComparer.Ordinal), Dependency().Matches(text).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    /// <summary>The <c>dotnet</c> host of the runtime running this suite: three levels above the shared framework's folder.</summary>
    private static string Dotnet()
    {
        string name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
        {
            return host;
        }

        string runtime = RuntimeEnvironment.GetRuntimeDirectory();
        return Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", name));
    }

    [GeneratedRegex("<dependency id=\"([^\"]+)\"")]
    private static partial Regex Dependency();
}
