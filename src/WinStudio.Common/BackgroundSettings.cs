namespace WinStudio.Common;

/// <summary>
/// Describes how the captured window is composited onto a background in the output video.
/// When <see cref="Mode"/> is not <see cref="BackgroundMode.None"/>, the window content is
/// inset with <see cref="PaddingFraction"/> padding on each side and the remaining area is
/// filled with the chosen background.
/// </summary>
public sealed record BackgroundSettings(
    BackgroundMode Mode = BackgroundMode.None,
    string ColorHex = "#1B2A3B",
    string? ImagePath = null,
    float PaddingFraction = 0.055f)
{
    // ── Built-in presets ──────────────────────────────────────────────────────

    public static readonly BackgroundSettings None =
        new(BackgroundMode.None);

    // Dark, minimal presets inspired by common screen-recording styles
    public static readonly BackgroundSettings Midnight =
        new(BackgroundMode.Solid, "#0D1117");

    public static readonly BackgroundSettings MidnightBlue =
        new(BackgroundMode.Solid, "#0A0F1E");

    public static readonly BackgroundSettings SlateBlue =
        new(BackgroundMode.Solid, "#1B2A3B");

    public static readonly BackgroundSettings Charcoal =
        new(BackgroundMode.Solid, "#1A1A1A");

    public static readonly BackgroundSettings DarkGreen =
        new(BackgroundMode.Solid, "#0E1A12");

    public static readonly BackgroundSettings DeepPurple =
        new(BackgroundMode.Solid, "#1C1028");

    public static readonly BackgroundSettings WarmEspresso =
        new(BackgroundMode.Solid, "#1F1410");

    public static readonly BackgroundSettings CoolGray =
        new(BackgroundMode.Solid, "#1C1E24");

    public static readonly IReadOnlyList<BackgroundSettings> BuiltInPresets =
    [
        Midnight,
        MidnightBlue,
        SlateBlue,
        Charcoal,
        DarkGreen,
        DeepPurple,
        WarmEspresso,
        CoolGray,
    ];
}
