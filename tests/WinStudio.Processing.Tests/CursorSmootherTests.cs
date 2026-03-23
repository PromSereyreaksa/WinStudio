using WinStudio.Common;
using WinStudio.Processing;
using Xunit;

namespace WinStudio.Processing.Tests;

public sealed class CursorSmootherTests
{
    [Fact]
    public void Smooth_WhenNearClick_PreservesClickAdjacentPoint()
    {
        var smoother = new CursorSmoother();
        var start = DateTime.UtcNow.Ticks;
        var events = new[]
        {
            new CursorEvent(start + 0, 0f, 0f, CursorEventType.Move),
            new CursorEvent(start + 1000, 10f, 20f, CursorEventType.Move),
            new CursorEvent(start + 2000, 20f, 40f, CursorEventType.LeftDown),
            new CursorEvent(start + 3000, 30f, 60f, CursorEventType.Move),
            new CursorEvent(start + 4000, 40f, 80f, CursorEventType.Move)
        }; 

        var output = smoother.Smooth(events, framesPerSecond: 60);

        Assert.Equal(events[3].X, output[3].X);
        Assert.Equal(events[3].Y, output[3].Y);
    } 

    [Fact]
    public void Smooth_WhenKeyPressInterleavedWithMoves_DoesNotWarpMoveEvents()
    {
        var smoother = new CursorSmoother();
        var tick = TimeSpan.FromMilliseconds(100).Ticks;

        // Move events follow a straight line from (100,100) to (500,500)
        // A KeyPress event at (900, 50) is interleaved — very far from the line
        var events = new[]
        {
            new CursorEvent(tick * 1, 100f, 100f, CursorEventType.Move),
            new CursorEvent(tick * 2, 200f, 200f, CursorEventType.Move),
            new CursorEvent(tick * 3, 300f, 300f, CursorEventType.Move),
            new CursorEvent(tick * 4, 900f, 50f,  CursorEventType.KeyPress), // far-off coords
            new CursorEvent(tick * 5, 400f, 400f, CursorEventType.Move),
            new CursorEvent(tick * 6, 500f, 500f, CursorEventType.Move),
            new CursorEvent(tick * 7, 600f, 600f, CursorEventType.Move),
            new CursorEvent(tick * 8, 700f, 700f, CursorEventType.Move),
        };

        var output = smoother.Smooth(events, framesPerSecond: 60);

        // The KeyPress event itself must be untouched
        Assert.Equal(900f, output[3].X);
        Assert.Equal(50f, output[3].Y);

        // Move events near the KeyPress must NOT be warped toward (900, 50)
        // They should stay close to the original straight-line path
        for (var i = 0; i < events.Length; i++)
        {
            if (events[i].EventType != CursorEventType.Move)
            {
                continue;
            }

            var dx = Math.Abs(output[i].X - events[i].X);
            var dy = Math.Abs(output[i].Y - events[i].Y);

            Assert.True(dx < 50f, $"Move event at index {i} was warped on X by {dx}");
            Assert.True(dy < 50f, $"Move event at index {i} was warped on Y by {dy}");
        }
    }
}
