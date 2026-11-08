//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Text.Json;

namespace SourceSharp.MapPreview.Report;

/// <summary>
/// Writes a <see cref="PreviewReport"/> as indented JSON with snake_case keys.
/// </summary>
/// <remarks>
/// Written by hand with <see cref="Utf8JsonWriter"/> rather than a source
/// generated serializer: a generated context keeps a lazily created static
/// default instance, and the libraries hold no mutable static state. Writing
/// by hand also fixes the key order, so two reports of the same map are
/// byte-identical and diff cleanly. Floats are written with their shortest
/// round-tripping form.
/// </remarks>
public static class PreviewReportWriter
{
    /// <summary>Serializes a report.</summary>
    /// <param name="report">The report.</param>
    /// <returns>UTF-8 JSON bytes, ending in a newline.</returns>
    public static byte[] Write(PreviewReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter w = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("map", report.Map);
            w.WriteNumber("bsp_version", report.BspVersion);
            w.WritePropertyName("bounds");
            w.WriteStartArray();
            foreach (float[] corner in report.Bounds)
            {
                Vector(w, corner);
            }

            w.WriteEndArray();
            w.WriteStartObject("counts");
            w.WriteNumber("triangles", report.Counts.Triangles);
            w.WriteNumber("surfaces", report.Counts.Surfaces);
            w.WriteNumber("materials", report.Counts.Materials);
            w.WriteNumber("lightmaps", report.Counts.Lightmaps);
            w.WriteNumber("props", report.Counts.Props);
            w.WriteNumber("entities", report.Counts.Entities);
            w.WriteEndObject();
            w.WriteStartArray("images");
            foreach (string image in report.Images)
            {
                w.WriteStringValue(image);
            }

            w.WriteEndArray();
            w.WriteStartArray("cameras");
            foreach (CameraReport camera in report.Cameras)
            {
                Camera(w, camera);
            }

            w.WriteEndArray();
            w.WriteStartArray("markers");
            foreach (MarkerReport marker in report.Markers)
            {
                w.WriteStartObject();
                w.WriteNumber("number", marker.Number);
                w.WriteString("kind", marker.Kind);
                w.WriteString("text", marker.Text);
                w.WritePropertyName("position");
                Vector(w, marker.Position);
                Strings(w, "views", marker.Views);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            Checks(w, report.Checks);
            w.WriteEndObject();
        }

        byte[] json = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(json);
        json[^1] = (byte)'\n';
        return json;
    }

    private static void Camera(Utf8JsonWriter w, CameraReport camera)
    {
        w.WriteStartObject();
        w.WriteString("id", camera.Id);
        w.WriteString("kind", camera.Kind);
        w.WriteString("description", camera.Description);
        w.WritePropertyName("position");
        Vector(w, camera.Position);
        w.WriteNumber("pitch", camera.Pitch);
        w.WriteNumber("yaw", camera.Yaw);
        if (camera.Fov is { } fov)
        {
            w.WriteNumber("fov", fov);
        }

        if (camera.OrthoHalfHeight is { } half)
        {
            w.WriteNumber("ortho_half_height", half);
        }

        w.WriteStartArray("size");
        w.WriteNumberValue(camera.Size[0]);
        w.WriteNumberValue(camera.Size[1]);
        w.WriteEndArray();
        w.WriteStartArray("visible_entities");
        foreach (EntityRef entity in camera.VisibleEntities)
        {
            w.WriteStartObject();
            w.WriteNumber("index", entity.Index);
            w.WriteString("classname", entity.Classname);
            if (entity.Targetname is { } name)
            {
                w.WriteString("targetname", name);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        ModelCounts(w, "visible_models", camera.VisibleModels);
        w.WriteStartArray("markers");
        foreach (int marker in camera.Markers)
        {
            w.WriteNumberValue(marker);
        }

        w.WriteEndArray();
        w.WriteStartObject("luminance");
        w.WriteNumber("mean", camera.Luminance.Mean);
        w.WriteNumber("p5", camera.Luminance.P5);
        w.WriteNumber("p95", camera.Luminance.P95);
        w.WriteNumber("black_fraction", camera.Luminance.BlackFraction);
        w.WriteNumber("clipped_fraction", camera.Luminance.ClippedFraction);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void Checks(Utf8JsonWriter w, PreviewChecks checks)
    {
        w.WriteStartObject("checks");
        w.WriteStartArray("missing_materials");
        foreach (MissingMaterialReport m in checks.MissingMaterials)
        {
            w.WriteStartObject();
            w.WriteString("name", m.Name);
            w.WriteString("problem", m.Problem);
            w.WriteNumber("surfaces", m.Surfaces);
            w.WriteNumber("marker", m.Marker);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("missing_models");
        foreach (MissingModelReport m in checks.MissingModels)
        {
            w.WriteStartObject();
            w.WriteString("model", m.Model);
            w.WriteString("reason", m.Reason);
            w.WriteNumber("placements", m.Placements);
            w.WriteNumber("marker", m.Marker);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartObject("lighting");
        w.WriteNumber("luxels", checks.Lighting.Luxels);
        w.WriteNumber("mean", checks.Lighting.Mean);
        w.WriteNumber("black_fraction", checks.Lighting.BlackFraction);
        w.WriteNumber("clipped_fraction", checks.Lighting.ClippedFraction);
        w.WriteNumber("unlit_surfaces", checks.Lighting.UnlitSurfaces);
        w.WriteBoolean("hdr", checks.Lighting.Hdr);
        w.WriteEndObject();
        w.WriteStartObject("skybox");
        if (checks.Skybox.Name is { } sky)
        {
            w.WriteString("name", sky);
        }
        else
        {
            w.WriteNull("name");
        }

        w.WriteNumber("faces_loaded", checks.Skybox.FacesLoaded);
        w.WriteEndObject();
        w.WriteStartArray("spawns");
        foreach (SpawnReport spawn in checks.Spawns)
        {
            w.WriteStartObject();
            w.WriteString("classname", spawn.Classname);
            w.WriteNumber("team", spawn.Team);
            w.WritePropertyName("origin");
            Vector(w, spawn.Origin);
            w.WriteNumber("yaw", spawn.Yaw);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartObject("entity_classes");
        foreach (KeyValuePair<string, int> pair in checks.EntityClasses)
        {
            w.WriteNumber(pair.Key, pair.Value);
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void ModelCounts(Utf8JsonWriter w, string name, IReadOnlyList<ModelCount> models)
    {
        w.WriteStartArray(name);
        foreach (ModelCount model in models)
        {
            w.WriteStartObject();
            w.WriteString("model", model.Model);
            w.WriteNumber("count", model.Count);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void Strings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (string value in values)
        {
            w.WriteStringValue(value);
        }

        w.WriteEndArray();
    }

    private static void Vector(Utf8JsonWriter w, float[] v)
    {
        w.WriteStartArray();
        foreach (float f in v)
        {
            w.WriteNumberValue(MathF.Round(f, 2));
        }

        w.WriteEndArray();
    }
}
