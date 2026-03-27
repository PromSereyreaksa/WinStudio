namespace WinStudio.Common;

public enum BackgroundMode
{
    /// <summary>Raw capture fills the output frame (no backdrop).</summary>
    None,

    /// <summary>A solid color surrounds the captured window.</summary>
    Solid,

    /// <summary>A user-selected image file is used as the backdrop.</summary>
    Image,
}
