namespace WinStudio.Export;

public static class ExportPresets
{
    public static ExportPreset DemoHD { get; } = new(
        Name: "DemoHD",
        Width: 1920,
        Height: 1080,
        FramesPerSecond: 30,
        VideoCodec: "h264-crf18",
        AudioCodec: "aac-192k",
        Container: "mp4");

    public static ExportPreset VerticalShort { get; } = new(
        Name: "VerticalShort",
        Width: 1080,
        Height: 1920,
        FramesPerSecond: 30,
        VideoCodec: "h264-crf18",
        AudioCodec: "aac-192k",
        Container: "mp4");

    public static ExportPreset Gif { get; } = new(
        Name: "Gif",
        Width: 800,
        Height: 450,
        FramesPerSecond: 15,
        VideoCodec: "gif-palette-256",
        AudioCodec: "none",
        Container: "gif");
}

