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
/// The <c>SourceSharp.MapFormats</c> package the game restores: packed from
/// the assembly this suite was built with, it holds that assembly and its
/// documentation and nothing else, declares no dependency, and is named by
/// the project's version.
/// </summary>
/// <remarks>
/// The pack runs with <c>--no-build</c>, so it reads the already-built
/// assembly beside this test binary and writes only into a scratch folder:
/// nothing the running suite has loaded is rebuilt under it.
/// </remarks>
public sealed partial class MapFormatsPackageTests
{
    private const string Project = "src/SourceSharp.MapFormats/SourceSharp.MapFormats.csproj";

    [RepoSourceFact(Project)]
    public async Task ThePackageHoldsTheFormatsAssemblyAndNoDependency()
    {
        string project = RepoSourceFactAttribute.Find(Project)!;
        string version = XDocument.Load(project).Descendants("Version").Single().Value;
        string configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
        string output = Path.Combine(Path.GetTempPath(), "mapformats-pack-" + Guid.NewGuid().ToString("N"));
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

            string package = Path.Combine(output, $"SourceSharp.MapFormats.{version}.nupkg");
            Assert.True(File.Exists(package), $"no {package}; the folder holds {string.Join(", ", Directory.GetFiles(output))}");
            using ZipArchive zip = ZipFile.OpenRead(package);
            string[] libraries = [.. zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("lib/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
            Assert.Equal(["lib/net10.0/SourceSharp.MapFormats.dll", "lib/net10.0/SourceSharp.MapFormats.xml"], libraries);

            using StreamReader nuspec = new(zip.GetEntry("SourceSharp.MapFormats.nuspec")!.Open());
            string text = await nuspec.ReadToEndAsync();
            Assert.Contains("<id>SourceSharp.MapFormats</id>", text, StringComparison.Ordinal);
            Assert.Contains($"<version>{version}</version>", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(Dependency(), text);

            // The packed assembly is the one this suite tested, byte for byte.
            using MemoryStream packed = new();
            await using (Stream entry = zip.GetEntry("lib/net10.0/SourceSharp.MapFormats.dll")!.Open())
            {
                await entry.CopyToAsync(packed);
            }

            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "SourceSharp.MapFormats.dll")),
                packed.ToArray());
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

    [GeneratedRegex("<dependency\\b")]
    private static partial Regex Dependency();
}
