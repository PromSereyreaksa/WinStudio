namespace WinStudio.Export;

public sealed record ExportPreset(
    string Name,
    int Width,
    int Height,
    int FramesPerSecond,
    string VideoCodec,
    string AudioCodec,
    string Container);

