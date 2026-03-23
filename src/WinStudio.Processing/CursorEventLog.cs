using WinStudio.Common;

namespace WinStudio.Processing;

public sealed class CursorEventLog
{
    private readonly List<CursorEvent> _events = [];

    public CursorEventLog()
    {
    }

    public CursorEventLog(IEnumerable<CursorEvent> events)
    {
        _events.AddRange(events.OrderBy(static e => e.TimestampTicks));
    }

    public IReadOnlyList<CursorEvent> Events => _events;

    public void Add(CursorEvent cursorEvent)
    {
        _events.Add(cursorEvent);
        _events.Sort(static (a, b) => a.TimestampTicks.CompareTo(b.TimestampTicks));
    }
}

