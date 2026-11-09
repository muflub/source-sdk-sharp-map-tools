//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapPreview.Report;

/// <summary>
/// Everything a preview found, written as <c>index.json</c> beside the images
/// (see <see cref="PreviewReportWriter"/>): what each view shows, the numbered
/// markers, and the checks.
/// </summary>
/// <param name="Map">The map name.</param>
/// <param name="BspVersion">The BSP file version.</param>
/// <param name="Bounds">The visible content's bounds, [min, max], each [x, y, z].</param>
/// <param name="Counts">Sizes of the scene.</param>
/// <param name="Images">The image files written, sheet first.</param>
/// <param name="Cameras">The views.</param>
/// <param name="Markers">The numbered markers, by number.</param>
/// <param name="Checks">The checks.</param>
public sealed record PreviewReport(
    string Map,
    int BspVersion,
    float[][] Bounds,
    SceneCounts Counts,
    IReadOnlyList<string> Images,
    IReadOnlyList<CameraReport> Cameras,
    IReadOnlyList<MarkerReport> Markers,
    PreviewChecks Checks);

/// <summary>Sizes of the scene.</summary>
/// <param name="Triangles">Triangles drawn.</param>
/// <param name="Surfaces">Surfaces (faces, displacements, prop meshes).</param>
/// <param name="Materials">Distinct materials.</param>
/// <param name="Lightmaps">Face lightmaps.</param>
/// <param name="Props">Placed models.</param>
/// <param name="Entities">Entities in the lump.</param>
public sealed record SceneCounts(int Triangles, int Surfaces, int Materials, int Lightmaps, int Props, int Entities);

/// <summary>One view.</summary>
/// <param name="Id">The camera id.</param>
/// <param name="Kind">The camera kind, lower case.</param>
/// <param name="Description">What it shows.</param>
/// <param name="Position">The eye, [x, y, z].</param>
/// <param name="Pitch">Pitch in degrees, positive down.</param>
/// <param name="Yaw">Yaw in degrees.</param>
/// <param name="Fov">Horizontal field of view for a perspective view, else null.</param>
/// <param name="OrthoHalfHeight">Half the visible height for an orthographic view, else null.</param>
/// <param name="Size">[width, height] in pixels as rendered.</param>
/// <param name="VisibleEntities">Entities with visible pixels.</param>
/// <param name="VisibleModels">Models with visible pixels, with how many placements show.</param>
/// <param name="Markers">The marker numbers drawn in this view.</param>
/// <param name="Luminance">Brightness figures.</param>
public sealed record CameraReport(
    string Id,
    string Kind,
    string Description,
    float[] Position,
    float Pitch,
    float Yaw,
    float? Fov,
    float? OrthoHalfHeight,
    int[] Size,
    IReadOnlyList<EntityRef> VisibleEntities,
    IReadOnlyList<ModelCount> VisibleModels,
    IReadOnlyList<int> Markers,
    LuminanceReport Luminance);

/// <summary>An entity named in a report.</summary>
/// <param name="Index">Its index in the entity lump.</param>
/// <param name="Classname">Its class.</param>
/// <param name="Targetname">Its name, if it has one.</param>
public sealed record EntityRef(int Index, string Classname, string? Targetname);

/// <summary>A model and how many placements of it are counted.</summary>
/// <param name="Model">The model path.</param>
/// <param name="Count">The count.</param>
public sealed record ModelCount(string Model, int Count);

/// <summary>Brightness of a view's map surfaces, linear, after exposure.</summary>
/// <param name="Mean">The mean.</param>
/// <param name="P5">The 5th percentile.</param>
/// <param name="P95">The 95th percentile.</param>
/// <param name="BlackFraction">Share below 0.02.</param>
/// <param name="ClippedFraction">Share at or above 1.</param>
public sealed record LuminanceReport(float Mean, float P5, float P95, float BlackFraction, float ClippedFraction);

/// <summary>A numbered marker.</summary>
/// <param name="Number">The number drawn.</param>
/// <param name="Kind">What sort of finding.</param>
/// <param name="Text">The description.</param>
/// <param name="Position">Where, [x, y, z].</param>
/// <param name="Views">The views it is drawn in.</param>
public sealed record MarkerReport(int Number, string Kind, string Text, float[] Position, IReadOnlyList<string> Views);

/// <summary>The checks a preview runs.</summary>
/// <param name="MissingMaterials">Materials that draw as the checker.</param>
/// <param name="MissingModels">Models that draw as stand-in boxes.</param>
/// <param name="Lighting">Lightmap statistics.</param>
/// <param name="Skybox">The 2D skybox and which of its faces loaded.</param>
/// <param name="Spawns">The player spawns.</param>
/// <param name="EntityClasses">Entity counts by class, sorted by class.</param>
public sealed record PreviewChecks(
    IReadOnlyList<MissingMaterialReport> MissingMaterials,
    IReadOnlyList<MissingModelReport> MissingModels,
    LightingReport Lighting,
    SkyboxReport Skybox,
    IReadOnlyList<SpawnReport> Spawns,
    IReadOnlyDictionary<string, int> EntityClasses);

/// <summary>A material that draws as the checker.</summary>
/// <param name="Name">The material name.</param>
/// <param name="Problem">Why, lower case.</param>
/// <param name="Surfaces">How many surfaces use it.</param>
/// <param name="Marker">The marker number placed on its first use.</param>
public sealed record MissingMaterialReport(string Name, string Problem, int Surfaces, int Marker);

/// <summary>A model that did not load.</summary>
/// <param name="Model">The model path.</param>
/// <param name="Reason">Why.</param>
/// <param name="Placements">How many times the map places it.</param>
/// <param name="Marker">The marker number placed on its first placement.</param>
public sealed record MissingModelReport(string Model, string Reason, int Placements, int Marker);

/// <summary>Lightmap statistics over every lit face.</summary>
/// <param name="Luxels">Samples counted.</param>
/// <param name="Mean">Mean linear luminance.</param>
/// <param name="BlackFraction">Share of samples below 0.02.</param>
/// <param name="ClippedFraction">Share at or above 1.</param>
/// <param name="UnlitSurfaces">Lit-shader world surfaces with no lightmap at all (fullbright in game).</param>
/// <param name="Hdr">Whether HDR lighting was read.</param>
public sealed record LightingReport(long Luxels, float Mean, float BlackFraction, float ClippedFraction, int UnlitSurfaces, bool Hdr);

/// <summary>The 2D skybox.</summary>
/// <param name="Name">The worldspawn skyname, or null.</param>
/// <param name="FacesLoaded">How many of the six faces loaded.</param>
public sealed record SkyboxReport(string? Name, int FacesLoaded);

/// <summary>A player spawn.</summary>
/// <param name="Classname">The class.</param>
/// <param name="Team">The team number.</param>
/// <param name="Origin">[x, y, z].</param>
/// <param name="Yaw">The facing.</param>
public sealed record SpawnReport(string Classname, int Team, float[] Origin, float Yaw);
