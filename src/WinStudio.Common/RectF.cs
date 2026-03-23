namespace WinStudio.Common;

public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public static RectF FromCenter(float centerX, float centerY, float width, float height)
    {
        return new RectF(centerX - (width / 2f), centerY - (height / 2f), width, height);
    }

    public RectF ClampWithin(float boundsWidth, float boundsHeight)
    {
        var clampedWidth = MathF.Max(1f, MathF.Min(Width, boundsWidth));
        var clampedHeight = MathF.Max(1f, MathF.Min(Height, boundsHeight));
        var clampedX = MathF.Max(0f, MathF.Min(X, boundsWidth - clampedWidth));
        var clampedY = MathF.Max(0f, MathF.Min(Y, boundsHeight - clampedHeight));
        return new RectF(clampedX, clampedY, clampedWidth, clampedHeight);
    }

    public static RectF Union(RectF a, RectF b)
    {
        var x = MathF.Min(a.X, b.X);
        var y = MathF.Min(a.Y, b.Y);
        var right = MathF.Max(a.Right, b.Right);
        var bottom = MathF.Max(a.Bottom, b.Bottom);
        return new RectF(x, y, right - x, bottom - y);
    }

    public bool Intersects(RectF other)
    {
        return X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
    }
}

