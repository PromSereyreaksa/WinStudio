using WinStudio.Common;

namespace WinStudio.Processing;

public sealed class CursorSmoother
{
    public IReadOnlyList<CursorEvent> Smooth(IReadOnlyList<CursorEvent> events, int framesPerSecond = 60)
    {
        if (events.Count < 5)
        {
            return events;
        }

        var frameTicks = TimeSpan.TicksPerSecond / Math.Max(1, framesPerSecond);
        var protectedTicks = frameTicks * 2;

        var clickTimes = events
            .Where(static e => e.EventType is CursorEventType.LeftDown or CursorEventType.LeftUp or CursorEventType.RightDown or CursorEventType.RightUp or CursorEventType.KeyPress)
            .Select(static e => e.TimestampTicks)
            .ToArray();

        // Extract indices of only Move events to use as control points
        var moveIndices = new List<int>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].EventType == CursorEventType.Move)
            {
                moveIndices.Add(i);
            }
        }

        var output = new CursorEvent[events.Count];

        for (var i = 0; i < events.Count; i++)
        {
            var current = events[i];

            if (current.EventType != CursorEventType.Move || IsNearClick(current.TimestampTicks, clickTimes, protectedTicks))
            {
                output[i] = current;
                continue;
            }

            var mIndex = moveIndices.BinarySearch(i);
            if (mIndex < 2 || mIndex > moveIndices.Count - 3)
            {
                output[i] = current;
                continue;
            }

            var p0 = events[moveIndices[mIndex - 2]];
            var p1 = events[moveIndices[mIndex - 1]];
            var p2 = events[moveIndices[mIndex + 1]];
            var p3 = events[moveIndices[mIndex + 2]];
            var smoothed = CatmullRom(p0, p1, p2, p3, 0.5f);

            output[i] = current with
            {
                X = smoothed.X,
                Y = smoothed.Y
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

    private static (float X, float Y) CatmullRom(CursorEvent p0, CursorEvent p1, CursorEvent p2, CursorEvent p3, float t)
    {
        static float Calculate(float tValue, float a, float b, float c, float d)
        {
            var t2 = tValue * tValue;
            var t3 = t2 * tValue;
            return 0.5f * ((2f * b) + (-a + c) * tValue + ((2f * a) - (5f * b) + (4f * c) - d) * t2 + (-a + (3f * b) - (3f * c) + d) * t3);
        }

        return (
            Calculate(t, p0.X, p1.X, p2.X, p3.X),
            Calculate(t, p0.Y, p1.Y, p2.Y, p3.Y));
    }
}
