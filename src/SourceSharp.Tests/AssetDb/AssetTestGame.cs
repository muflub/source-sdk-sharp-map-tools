//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.AssetDb;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Io;
using SourceSharp.Tests.MapFormats.Assets;

namespace SourceSharp.Tests.AssetDb;

/// <summary>
/// A fake mounted game for the asset database facts: content files in
/// memory behind a real <see cref="DirectoryContentMount"/>, so enumeration,
/// case folding and resolution are the production code's own.
/// </summary>
internal sealed class AssetTestGame
{
    /// <summary>A unit cube's corners, for models that need real area in every view.</summary>
    public static readonly Vec3[] Cube =
    [
        new(-8, -8, 0), new(8, -8, 0), new(8, 8, 0), new(-8, 8, 0),
        new(-8, -8, 16), new(8, -8, 16), new(8, 8, 16), new(-8, 8, 16),
    ];

    /// <summary>The cube's twelve triangles.</summary>
    public static readonly int[] CubeIndices =
    [
        0, 1, 2, 0, 2, 3, // bottom
        4, 6, 5, 4, 7, 6, // top
        0, 5, 1, 0, 4, 5, // -Y
        3, 2, 6, 3, 6, 7, // +Y
        0, 3, 7, 0, 7, 4, // -X
        1, 5, 6, 1, 6, 2, // +X
    ];

    /// <summary>Gets the content's backing files.</summary>
    public InMemoryFileSystem Disk { get; } = new();

    /// <summary>Adds a texture written by the production VTF writer, full mip chain.</summary>
    public AssetTestGame AddTexture(
        string name,
        int width,
        int height,
        (byte R, byte G, byte B, byte A)? colour = null,
        uint flags = 0)
    {
        byte[] bytes = VtfWriter.Write(
            width, height, ImageFormat.Bgra8888, flags,
            (_, _) => colour ?? ((byte)10, (byte)20, (byte)30, (byte)255));
        Disk.AddFile($"materials/{name}.vtf", bytes);
        return this;
    }

    /// <summary>Adds a texture in a format the decoder does not handle.</summary>
    public AssetTestGame AddUndecodableTexture(string name)
    {
        Disk.AddFile($"materials/{name}.vtf", VtfTests.Synthetic(ImageFormat.Rgba16161616F, 4, 4, mips: 1, envMap: false));
        return this;
    }

    /// <summary>Adds a material's text verbatim.</summary>
    public AssetTestGame AddMaterial(string name, string text)
    {
        Disk.AddText($"materials/{name}.vmt", text);
        return this;
    }

    /// <summary>Adds arbitrary bytes at a content path.</summary>
    public AssetTestGame AddRaw(string path, byte[] bytes)
    {
        Disk.AddFile(path, bytes);
        return this;
    }

    /// <summary>Adds a model triple at <c>models/&lt;stem&gt;</c>.</summary>
    public AssetTestGame AddModel(
        string stem,
        (byte[] Mdl, byte[] Vvd, byte[] Vtx) files,
        string vtxExtension = "dx90.vtx",
        bool withVvd = true,
        bool withVtx = true)
    {
        Disk.AddFile($"models/{stem}.mdl", files.Mdl);
        if (withVvd)
        {
            Disk.AddFile($"models/{stem}.vvd", files.Vvd);
        }

        if (withVtx)
        {
            Disk.AddFile($"models/{stem}.{vtxExtension}", files.Vtx);
        }

        return this;
    }

    /// <summary>A cube model skinned with one texture name, with UVs spanning the texture.</summary>
    public static (byte[] Mdl, byte[] Vvd, byte[] Vtx) CubeModel(
        string textureName = "crate",
        string searchPath = "models/props/",
        int flags = 0x10,
        string? keyValues = null,
        int version = StudioIdents.MdlVersion,
        int skinFamilies = 0,
        int skinRefs = 0,
        string[]? textureNames = null)
    {
        (float, float)[] uvs = [.. Cube.Select(p => ((p.X + 8) / 16f, (p.Z + 8) / 32f))];
        return StudioTestModel.Build(
            Cube,
            CubeIndices,
            name: "props/cube.mdl",
            textureName: textureName,
            searchPath: searchPath,
            skinFamilies: skinFamilies,
            skinRefs: skinRefs,
            uvs: uvs,
            flags: flags,
            textureNames: textureNames,
            keyValues: keyValues,
            version: version);
    }

    /// <summary>Mounts everything added so far as a game's content view.</summary>
    public ContentFileSystem Mount()
    {
        DirectoryContentMount mount = DirectoryContentMount
            .MountAsync(Disk, VPath.Empty)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return new ContentFileSystem([mount]);
    }

    /// <summary>Options with two workers, so the parallel path runs, and small views so facts stay quick.</summary>
    public static AssetExportOptions Options(ModelSelection models = ModelSelection.Props) => new()
    {
        MaxParallelism = 2,
        ViewSize = 64,
        Models = models,
    };
}
