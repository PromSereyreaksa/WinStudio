using WinStudio.Common;
using Xunit;

namespace WinStudio.Processing.Tests;

public sealed class CaptureCoordinateTransformTests
{
    [Fact]
    public void TryMapDesktopPoint_WhenDesktopRegionAndContentScaleDiffer_MapsIntoContentSpace()
    {
        var transform = new CaptureCoordinateTransform(
            DesktopOriginX: 100f,
            DesktopOriginY: 150f,
            DesktopWidth: 1500f,
            DesktopHeight: 900f,
            ContentWidth: 1000f,
            ContentHeight: 600f);

        var success = transform.TryMapDesktopPoint(
            desktopX: 850f,
            desktopY: 600f,
            clampToBounds: false,
            tolerancePixels: 24f,
            out var mappedX,
            out var mappedY);

        Assert.True(success);
        Assert.InRange(mappedX, 499f, 501f);
        Assert.InRange(mappedY, 299f, 301f);
    }

    [Fact]
    public void TryMapDesktopPoint_WhenDraggingOutsideCapture_ClampsToContentBounds()
    {
        var transform = new CaptureCoordinateTransform(
            DesktopOriginX: 200f,
            DesktopOriginY: 100f,
            DesktopWidth: 1200f,
            DesktopHeight: 800f,
            ContentWidth: 1200f,
            ContentHeight: 800f);

        var success = transform.TryMapDesktopPoint(
            desktopX: 1550f,
            desktopY: 950f,
            clampToBounds: true,
            tolerancePixels: 24f,
            out var mappedX,
            out var mappedY);

        Assert.True(success);
        Assert.Equal(1199f, mappedX);
        Assert.Equal(799f, mappedY);
    }
}
