namespace WinStudio.Common;

public readonly record struct CaptureCoordinateTransform(
    float DesktopOriginX,
    float DesktopOriginY,
    float DesktopWidth,
    float DesktopHeight,
    float ContentWidth,
    float ContentHeight)
{
    public bool TryMapDesktopPoint(
        float desktopX,
        float desktopY,
        bool clampToBounds,
        float tolerancePixels,
        out float contentX,
        out float contentY)
    {
        contentX = 0f;
        contentY = 0f;

        var safeDesktopWidth = Math.Max(1f, DesktopWidth);
        var safeDesktopHeight = Math.Max(1f, DesktopHeight);
        var safeContentWidth = Math.Max(1f, ContentWidth);
        var safeContentHeight = Math.Max(1f, ContentHeight);
        var relativeX = desktopX - DesktopOriginX;
        var relativeY = desktopY - DesktopOriginY;

        if (!clampToBounds)
        {
            if (relativeX < -tolerancePixels
                || relativeY < -tolerancePixels
                || relativeX > safeDesktopWidth + tolerancePixels
                || relativeY > safeDesktopHeight + tolerancePixels)
            {
                return false;
            }
        }

        relativeX = Math.Clamp(relativeX, 0f, safeDesktopWidth - 1f);
        relativeY = Math.Clamp(relativeY, 0f, safeDesktopHeight - 1f);

        var scaleX = safeContentWidth / safeDesktopWidth;
        var scaleY = safeContentHeight / safeDesktopHeight;
        contentX = Math.Clamp(relativeX * scaleX, 0f, safeContentWidth - 1f);
        contentY = Math.Clamp(relativeY * scaleY, 0f, safeContentHeight - 1f);
        return true;
    }
}

