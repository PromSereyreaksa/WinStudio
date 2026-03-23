namespace WinStudio.Common;

public readonly record struct Timestamped<T>(long Ticks, T Value);

