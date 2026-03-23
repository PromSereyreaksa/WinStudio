namespace WinStudio.Common;

public readonly record struct CursorEvent(long TimestampTicks, float X, float Y, CursorEventType EventType);

