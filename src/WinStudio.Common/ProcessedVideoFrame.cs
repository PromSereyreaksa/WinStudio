namespace WinStudio.Common;

public sealed record ProcessedVideoFrame(
    long TimestampTicks,
    int Width,
    int Height,
    byte[] PixelBuffer,
    RectF ActiveZoomRect,
    float CursorScale,
    float CursorOpacity);

