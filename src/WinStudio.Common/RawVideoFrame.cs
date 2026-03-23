namespace WinStudio.Common;

public sealed record RawVideoFrame(
    long TimestampTicks,
    int Width,
    int Height,
    byte[] PixelBuffer);

