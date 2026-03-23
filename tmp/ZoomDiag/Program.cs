using System.Text.Json;
using WinStudio.Common;
using WinStudio.Processing;

var json = File.ReadAllText(@"c:\Users\User\Desktop\WinStudio\rec_logs\recording-20260323-062429.raw.cursor.json");
var events = JsonSerializer.Deserialize<List<CursorEvent>>(json)!;

Console.WriteLine($"Total cursor events: {events.Count}");
var clicks = events.Where(e => e.EventType == CursorEventType.LeftDown).ToList();
Console.WriteLine($"Click events: {clicks.Count}");
var kpCount = events.Count(e => e.EventType == CursorEventType.KeyPress);
Console.WriteLine($"KeyPress events: {kpCount}");

var gen = new ZoomRegionGenerator();
var keyframes = gen.Generate(events, 1920, 1032);

Console.WriteLine($"\nGenerated {keyframes.Count} zoom keyframes\n");

var firstEvent = events.Min(e => e.TimestampTicks);
foreach (var kf in keyframes)
{
    var startSec = TimeSpan.FromTicks(kf.StartTicks - firstEvent).TotalSeconds;
    var endSec = TimeSpan.FromTicks(kf.EndTicks - firstEvent).TotalSeconds;
    var dur = endSec - startSec;
    var isFullFrame = kf.TargetRect.Width >= 1919f && kf.TargetRect.Height >= 1031f;
    var label = isFullFrame ? "FULL" : "ZOOM";
    var cx = kf.TargetRect.X + kf.TargetRect.Width / 2f;
    var cy = kf.TargetRect.Y + kf.TargetRect.Height / 2f;
    Console.WriteLine($"[{label}] {startSec:F3}s-{endSec:F3}s ({dur:F3}s) center=({cx:F0},{cy:F0}) rect=({kf.TargetRect.X:F0},{kf.TargetRect.Y:F0} {kf.TargetRect.Width:F0}x{kf.TargetRect.Height:F0})");
}
