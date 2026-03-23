using WinStudio.Common;

namespace WinStudio.Processing;

public sealed class CursorHighlighter
{
    private static readonly long ZoomUpTicks = TimeSpan.FromMilliseconds(80).Ticks;
    private static readonly long ZoomDownTicks = TimeSpan.FromMilliseconds(120).Ticks;
    private static readonly long IdleTicks = TimeSpan.FromMilliseconds(1500).Ticks;
    

    public CursorRenderState GetRenderState(long timestampTicks, IReadOnlyList<CursorEvent> events)
    {
        var lastClick = events
            .Where(e => e.TimestampTicks <= timestampTicks && e.EventType == CursorEventType.LeftDown)
            .OrderByDescending(static e => e.TimestampTicks)
            .FirstOrDefault();

        var lastMove = events
            .Where(e => e.TimestampTicks <= timestampTicks && e.EventType == CursorEventType.Move)
            .OrderByDescending(static e => e.TimestampTicks)
            .FirstOrDefault();

        var scale = CalculateScale(timestampTicks, lastClick.TimestampTicks);
        var opacity = timestampTicks - lastMove.TimestampTicks > IdleTicks ? 0.3f : 1.0f;

        return new CursorRenderState(scale, opacity);
    }

    private static float CalculateScale(long nowTicks, long clickTicks)
    {
        if (clickTicks == default)
        {
            return 1f;
        }

        var elapsed = nowTicks - clickTicks;
        if (elapsed < 0)
        {
            return 1f;
        }

        if (elapsed <= ZoomUpTicks)
        {
            var t = elapsed / (float)ZoomUpTicks;
            return 1f + (0.6f * t);
        }

        if (elapsed <= ZoomUpTicks + ZoomDownTicks)
        {
            var t = (elapsed - ZoomUpTicks) / (float)ZoomDownTicks;
            return 1.6f - (0.6f * t);
        }

        return 1f;
    }
}

public readonly record struct CursorRenderState(float Scale, float Opacity);

