using WinStudio.Common;

namespace WinStudio.Processing;

public sealed class CursorSmoother
{
    public IReadOnlyList<CursorEvent> Smooth(IReadOnlyList<CursorEvent> events, int framesPerSecond = 60)
    {
        if (events.Count < 3)
        {
            return events;
        }

        var frameTicks = TimeSpan.TicksPerSecond / Math.Max(1, framesPerSecond);
        var protectedTicks = frameTicks * 2;
        var clickTimes = events
            .Where(static e => e.EventType is CursorEventType.LeftDown or CursorEventType.LeftUp or CursorEventType.RightDown or CursorEventType.RightUp or CursorEventType.KeyPress)
            .Select(static e => e.TimestampTicks)
            .ToArray();

        var output = new CursorEvent[events.Count];
        var hasSmoothedPoint = false;
        var smoothedX = 0f;
        var smoothedY = 0f;
        var lastMoveTicks = 0L;

        for (var i = 0; i < events.Count; i++)
        {
            var current = events[i];
            if (current.EventType != CursorEventType.Move || IsNearClick(current.TimestampTicks, clickTimes, protectedTicks))
            {
                output[i] = current;
                if (current.EventType != CursorEventType.Move)
                {
                    hasSmoothedPoint = false;
                }

                continue;
            }

            if (!hasSmoothedPoint)
            {
                smoothedX = current.X;
                smoothedY = current.Y;
                lastMoveTicks = current.TimestampTicks;
                hasSmoothedPoint = true;
                output[i] = current;
                continue;
            }

            var deltaSeconds = Math.Max(
                0.0001f,
                (current.TimestampTicks - lastMoveTicks) / (float)TimeSpan.TicksPerSecond);
            var distance = MathF.Sqrt(MathF.Pow(current.X - smoothedX, 2f) + MathF.Pow(current.Y - smoothedY, 2f));
            var speedPixelsPerSecond = distance / deltaSeconds;
            var smoothingWindowSeconds = Lerp(0.05f, 0.012f, Math.Clamp(speedPixelsPerSecond / 900f, 0f, 1f));
            var alpha = 1f - MathF.Exp(-deltaSeconds / smoothingWindowSeconds);

            smoothedX = Lerp(smoothedX, current.X, alpha);
            smoothedY = Lerp(smoothedY, current.Y, alpha);
            lastMoveTicks = current.TimestampTicks;
            output[i] = current with
            {
                X = smoothedX,
                Y = smoothedY
            };
        }

        return output;
    }

    private static bool IsNearClick(long ticks, IReadOnlyList<long> clickTimes, long protectedTicks)
    {
        for (var i = 0; i < clickTimes.Count; i++)
        {
            if (Math.Abs(ticks - clickTimes[i]) <= protectedTicks)
            {
                return true;
            }
        }

        return false;
    }

    private static float Lerp(float from, float to, float t)
    {
        return from + ((to - from) * Math.Clamp(t, 0f, 1f));
    }
}
