//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapPreview.Render;
using Xunit;

namespace SourceSharp.Tests.MapPreview.Render;

/// <summary>Facts for <see cref="PreviewCamera"/>, <see cref="CameraView"/> and <see cref="ColorSpace"/>.</summary>
public class CameraTests
{
    [Theory]
    [InlineData(0, 0, 1, 0, 0)]
    [InlineData(0, 90, 0, 1, 0)]
    [InlineData(90, 0, 0, 0, -1)]
    [InlineData(-90, 0, 0, 0, 1)]
    public void SourceAnglesPointTheWayTheEngineDoes(float pitch, float yaw, float x, float y, float z)
    {
        Vec3 d = PreviewCamera.Direction(pitch, yaw);
        Assert.Equal(x, d.X, 4);
        Assert.Equal(y, d.Y, 4);
        Assert.Equal(z, d.Z, 4);
    }

    [Theory]
    [InlineData(8, 90)]
    [InlineData(-30, -120)]
    [InlineData(45, 10)]
    public void AnglesRoundTrip(float pitch, float yaw)
    {
        PreviewCamera camera = PreviewCamera.FromAngles("c", CameraKind.Custom, Vec3.Zero, pitch, yaw, 90, 64, 48, "c");
        (float p, float y) = camera.Angles();
        Assert.Equal(pitch, p, 2);
        Assert.Equal(yaw, y, 2);
    }

    [Fact]
    public void APointStraightAheadProjectsToTheCentre()
    {
        CameraView view = PreviewCamera.FromAngles("c", CameraKind.Custom, Vec3.Zero, 0, 0, 90, 64, 48, "c").View();

        (float x, float y, float depth) = view.Project(new Vec3(100, 0, 0))!.Value;

        Assert.Equal(32, x, 3);
        Assert.Equal(24, y, 3);
        Assert.Equal(100, depth, 3);
    }

    [Fact]
    public void NinetyDegreesHorizontalPutsAFortyFiveDegreePointOnTheEdge()
    {
        CameraView view = PreviewCamera.FromAngles("c", CameraKind.Custom, Vec3.Zero, 0, 0, 90, 64, 48, "c").View();

        Assert.Equal(64, view.Project(new Vec3(100, -100, 0))!.Value.X, 2);
        Assert.Equal(0, view.Project(new Vec3(100, 100, 0))!.Value.X, 2);
    }

    [Fact]
    public void APointBehindAPerspectiveCameraHasNoProjection()
    {
        CameraView view = PreviewCamera.FromAngles("c", CameraKind.Custom, Vec3.Zero, 0, 0, 90, 64, 48, "c").View();
        Assert.Null(view.Project(new Vec3(-10, 0, 0)));
    }

    [Fact]
    public void ATopDownCameraHasNorthUp()
    {
        PreviewCamera top = new("t", CameraKind.Overview, new Vec3(0, 0, 100), new Vec3(0, 0, -1), new Vec3(0, 0, 1), 0, 50, 100, 100, "t");
        CameraView view = top.View();

        (float _, float yNorth, float _) = view.Project(new Vec3(0, 40, 0))!.Value;
        (float xEast, float _, float _) = view.Project(new Vec3(40, 0, 0))!.Value;

        Assert.True(yNorth < 50, "north should be toward the top");
        Assert.True(xEast > 50, "east should be toward the right");
        Assert.Equal(1f, view.UnitsPerPixel(0));
    }

    [Fact]
    public void PerspectiveScaleGrowsWithDepth()
    {
        CameraView view = PreviewCamera.FromAngles("c", CameraKind.Custom, Vec3.Zero, 0, 0, 90, 100, 100, "c").View();
        Assert.Equal(2f, view.UnitsPerPixel(100), 3);
        Assert.Equal(4f, view.UnitsPerPixel(200), 3);
    }

    [Fact]
    public void FacingDependsOnProjection()
    {
        CameraView persp = PreviewCamera.FromAngles("c", CameraKind.Custom, Vec3.Zero, 0, 0, 90, 64, 48, "c").View();
        CameraView ortho = new PreviewCamera("t", CameraKind.Overview, new Vec3(0, 0, 100), new Vec3(0, 0, -1), new Vec3(0, 1, 0), 0, 50, 64, 64, "t").View();

        Assert.True(persp.Faces(new Vec3(100, 0, 0), new Vec3(-1, 0, 0)));
        Assert.False(persp.Faces(new Vec3(100, 0, 0), new Vec3(1, 0, 0)));
        Assert.True(ortho.Faces(Vec3.Zero, new Vec3(0, 0, 1)));
        Assert.False(ortho.Faces(Vec3.Zero, new Vec3(0, 0, -1)));
    }

    [Fact]
    public void SrgbTablesMatchTheCurveAndClamp()
    {
        ColorSpace space = new();

        Assert.Equal(0f, space.ToLinear(0));
        Assert.Equal(1f, space.ToLinear(255), 5);
        Assert.Equal(0.2159f, space.ToLinear(128), 3);
        Assert.Equal(0, space.ToSrgb(-1f));
        Assert.Equal(0, space.ToSrgb(float.NaN));
        Assert.Equal(255, space.ToSrgb(4f));
        for (int c = 0; c < 256; c++)
        {
            Assert.Equal(c, space.ToSrgb(space.ToLinear((byte)c)));
        }
    }

    [Fact]
    public void TheLinearSegmentsOfTheCurveAreUsedBelowTheirKnees()
    {
        Assert.Equal(0.01f / 12.92f, ColorSpace.SrgbToLinear(0.01f), 6);
        Assert.Equal(0.001f * 12.92f, ColorSpace.LinearToSrgb(0.001f), 6);
    }
}
