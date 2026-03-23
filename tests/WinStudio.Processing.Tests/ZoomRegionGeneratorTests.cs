using WinStudio.Common;
using WinStudio.Processing;
using Xunit;

namespace WinStudio.Processing.Tests;

public sealed class ZoomRegionGeneratorTests
{
    [Fact]
    public void Generate_WhenClicksExist_ProducesOrderedKeyframes()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 22, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 500f, 400f, CursorEventType.LeftDown),
            new CursorEvent(start + TimeSpan.FromSeconds(1.3).Ticks, 520f, 390f, CursorEventType.LeftDown),
            new CursorEvent(start + TimeSpan.FromSeconds(8).Ticks, 1200f, 800f, CursorEventType.LeftDown)
        };

        var keyframes = generator.Generate(events, 1920, 1080);

        Assert.NotEmpty(keyframes);
        Assert.True(keyframes.SequenceEqual(keyframes.OrderBy(static k => k.StartTicks)));
        Assert.Contains(keyframes, k => k.TargetRect.Width <= 1920f && k.TargetRect.Height <= 1080f);
    }

    [Fact]
    public void Generate_WhenSingleClickAndNoFurtherActivity_HoldsForAboutThreeSecondsThenReleases()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 900f, 500f, CursorEventType.LeftDown)
        };

        var keyframes = generator.Generate(events, 1920, 1080);
        var holdFrame = keyframes
            .Where(static k => k.TargetRect.Width < 1920f)
            .OrderByDescending(static k => k.EndTicks - k.StartTicks)
            .First();
        var holdDuration = TimeSpan.FromTicks(holdFrame.EndTicks - holdFrame.StartTicks);

        Assert.True(holdDuration >= TimeSpan.FromSeconds(2.8), $"Expected ~3s hold, got {holdDuration}.");
        Assert.True(holdDuration <= TimeSpan.FromSeconds(3.4), $"Expected ~3s hold, got {holdDuration}.");
    }

    [Fact]
    public void Generate_WhenClicksOverlapInTimeAndSpace_MergesKeyframes()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromMilliseconds(1000).Ticks, 300f, 300f, CursorEventType.LeftDown),
            new CursorEvent(start + TimeSpan.FromMilliseconds(1300).Ticks, 340f, 320f, CursorEventType.LeftDown)
        };

        var keyframes = generator.Generate(events, 1280, 720);
        var clusterEndTicks = start + TimeSpan.FromMilliseconds(2500).Ticks;
        var fullFrameSegmentsDuringCluster = keyframes.Where(
            k => k.StartTicks < clusterEndTicks
                && k.TargetRect.Width >= 1280f
                && k.TargetRect.Height >= 720f);

        Assert.Empty(fullFrameSegmentsDuringCluster);
    }

    [Fact]
    public void Generate_WhenClickIsNearTopEdge_AvoidsExtremeCornerZoom()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 950f, 59f, CursorEventType.LeftDown)
        };

        var keyframes = generator.Generate(events, 1920, 1032);
        var focusFrame = keyframes
            .Where(static k => k.TargetRect.Width < 1920f)
            .OrderByDescending(static k => k.EndTicks - k.StartTicks)
            .First();
        var centerX = focusFrame.TargetRect.X + (focusFrame.TargetRect.Width / 2f);
        var centerY = focusFrame.TargetRect.Y + (focusFrame.TargetRect.Height / 2f);

        Assert.InRange(centerX, 930f, 970f);
        Assert.InRange(centerY, 45f, 180f);
        Assert.True(focusFrame.TargetRect.Width >= 220f, $"Unexpected extreme zoom width: {focusFrame.TargetRect.Width}");
    }

    [Fact]
    public void Generate_WhenTypingAfterClick_ExtendsHoldDuration()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 1119f, 465f, CursorEventType.LeftDown),
            new CursorEvent(start + TimeSpan.FromSeconds(1.2).Ticks, 1119f, 465f, CursorEventType.KeyPress),
            new CursorEvent(start + TimeSpan.FromSeconds(1.35).Ticks, 1119f, 465f, CursorEventType.KeyPress),
            new CursorEvent(start + TimeSpan.FromSeconds(1.5).Ticks, 1119f, 465f, CursorEventType.KeyPress)
        };

        var keyframes = generator.Generate(events, 1920, 1032);
        var holdFrame = keyframes
            .Where(static k => k.TargetRect.Width < 1920f)
            .OrderByDescending(static k => k.EndTicks - k.StartTicks)
            .First();
        var holdDuration = TimeSpan.FromTicks(holdFrame.EndTicks - holdFrame.StartTicks);

        // Last keystroke at 1.5s + 2s idle timeout = hold ends at ~3.5s from click at 1s => ~2.5s hold
        // baseHold is ~1.6s, so the activity-extended hold should be longer
        Assert.True(holdDuration >= TimeSpan.FromSeconds(2), $"Expected extended hold, got {holdDuration}.");
    }

    [Fact]
    public void Generate_WhenTypingContinuouslyForSeveralSeconds_KeepsZoomHeld()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var events = new List<CursorEvent>
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 800f, 400f, CursorEventType.LeftDown)
        };

        // Simulate continuous typing every 300ms for 6 seconds after click
        for (var ms = 1300; ms <= 7000; ms += 300)
        {
            events.Add(new CursorEvent(start + TimeSpan.FromMilliseconds(ms).Ticks, 800f, 400f, CursorEventType.KeyPress));
        }

        var keyframes = generator.Generate(events.ToArray(), 1920, 1080);
        var holdFrame = keyframes
            .Where(static k => k.TargetRect.Width < 1920f)
            .OrderByDescending(static k => k.EndTicks - k.StartTicks)
            .First();
        var holdDuration = TimeSpan.FromTicks(holdFrame.EndTicks - holdFrame.StartTicks);

        // Typing until 7s + 2s idle timeout = hold should last at least ~8s from click
        Assert.True(holdDuration >= TimeSpan.FromSeconds(7), $"Expected prolonged hold during typing, got {holdDuration}.");
    }

    [Fact]
    public void Generate_WhenMouseMovesNearTarget_ExtendsHoldDuration()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 600f, 400f, CursorEventType.LeftDown),
            // Mouse moves near the click location over 4 seconds
            new CursorEvent(start + TimeSpan.FromSeconds(1.5).Ticks, 610f, 410f, CursorEventType.Move),
            new CursorEvent(start + TimeSpan.FromSeconds(2.5).Ticks, 620f, 390f, CursorEventType.Move),
            new CursorEvent(start + TimeSpan.FromSeconds(3.5).Ticks, 605f, 405f, CursorEventType.Move),
            new CursorEvent(start + TimeSpan.FromSeconds(4.5).Ticks, 615f, 395f, CursorEventType.Move)
        };

        var keyframes = generator.Generate(events, 1920, 1080);
        var holdFrame = keyframes
            .Where(static k => k.TargetRect.Width < 1920f)
            .OrderByDescending(static k => k.EndTicks - k.StartTicks)
            .First();
        var holdDuration = TimeSpan.FromTicks(holdFrame.EndTicks - holdFrame.StartTicks);

        // Last mouse move at 4.5s + 3s idle timeout should prolong focus significantly.
        Assert.True(holdDuration >= TimeSpan.FromSeconds(4), $"Expected extended hold from mouse activity, got {holdDuration}.");
    }

    [Fact]
    public void Generate_WhenTypingAfterClick_PinsFocusToTypingLocation()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var keyPressTicks = start + TimeSpan.FromSeconds(1.5).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 600f, 320f, CursorEventType.LeftDown),
            new CursorEvent(start + TimeSpan.FromSeconds(1.05).Ticks, 600f, 320f, CursorEventType.LeftUp),
            new CursorEvent(keyPressTicks, 1200f, 320f, CursorEventType.KeyPress),
            new CursorEvent(keyPressTicks + TimeSpan.FromMilliseconds(200).Ticks, 1200f, 320f, CursorEventType.KeyPress)
        };

        var keyframes = generator.Generate(events, 1920, 1032);
        var trackedFrame = keyframes.First(
            k => k.StartTicks <= keyPressTicks + TimeSpan.FromMilliseconds(1).Ticks
                && k.EndTicks > keyPressTicks + TimeSpan.FromMilliseconds(1).Ticks
                && k.TargetRect.Width < 1920f);
        var centerX = trackedFrame.TargetRect.X + (trackedFrame.TargetRect.Width / 2f);
        var centerY = trackedFrame.TargetRect.Y + (trackedFrame.TargetRect.Height / 2f);

        Assert.InRange(centerX, 1180f, 1220f);
        Assert.InRange(centerY, 300f, 340f);
    }

    [Fact]
    public void Generate_WhenDraggingSelection_FollowsCursorDirectly()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var dragMoveTicks = start + TimeSpan.FromSeconds(1.6).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 520f, 260f, CursorEventType.LeftDown),
            new CursorEvent(start + TimeSpan.FromSeconds(1.35).Ticks, 640f, 280f, CursorEventType.Move),
            new CursorEvent(dragMoveTicks, 980f, 340f, CursorEventType.Move),
            new CursorEvent(start + TimeSpan.FromSeconds(1.8).Ticks, 980f, 340f, CursorEventType.LeftUp)
        };

        var keyframes = generator.Generate(events, 1920, 1032);
        var trackedFrame = keyframes.First(
            k => k.StartTicks <= dragMoveTicks + TimeSpan.FromMilliseconds(1).Ticks
                && k.EndTicks > dragMoveTicks + TimeSpan.FromMilliseconds(1).Ticks
                && k.TargetRect.Width < 1920f);
        var centerX = trackedFrame.TargetRect.X + (trackedFrame.TargetRect.Width / 2f);
        var centerY = trackedFrame.TargetRect.Y + (trackedFrame.TargetRect.Height / 2f);

        Assert.InRange(centerX, 955f, 1005f);
        Assert.InRange(centerY, 315f, 365f);
    }

    [Fact]
    public void Generate_WhenCursorMovesWithoutDragging_KeepsFocusOnClick()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var moveTicks = start + TimeSpan.FromSeconds(1.7).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 540f, 210f, CursorEventType.LeftDown),
            new CursorEvent(start + TimeSpan.FromSeconds(1.05).Ticks, 540f, 210f, CursorEventType.LeftUp),
            new CursorEvent(moveTicks, 1080f, 252f, CursorEventType.Move),
            new CursorEvent(moveTicks + TimeSpan.FromMilliseconds(220).Ticks, 1120f, 258f, CursorEventType.Move)
        };

        var keyframes = generator.Generate(events, 1920, 1032);
        var trackedFrame = keyframes.First(
            k => k.StartTicks <= moveTicks + TimeSpan.FromMilliseconds(1).Ticks
                && k.EndTicks > moveTicks + TimeSpan.FromMilliseconds(1).Ticks
                && k.TargetRect.Width < 1920f);
        var centerX = trackedFrame.TargetRect.X + (trackedFrame.TargetRect.Width / 2f);
        var centerY = trackedFrame.TargetRect.Y + (trackedFrame.TargetRect.Height / 2f);

        Assert.InRange(centerX, 515f, 565f);
        Assert.InRange(centerY, 185f, 235f);
    }

    [Fact]
    public void Generate_WhenRepeatedClicksHitSameFocus_DoesNotCreateTimelineGap()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 574f, 239f, CursorEventType.LeftDown),
            new CursorEvent(start + TimeSpan.FromSeconds(1.2).Ticks, 574f, 239f, CursorEventType.LeftDown),
            new CursorEvent(start + TimeSpan.FromSeconds(1.6).Ticks, 574f, 239f, CursorEventType.KeyPress)
        };

        var keyframes = generator.Generate(events, 1920, 1032);
        var ordered = keyframes.OrderBy(static k => k.StartTicks).ToArray();
        for (var i = 0; i < ordered.Length - 1; i++)
        {
            Assert.True(
                ordered[i + 1].StartTicks <= ordered[i].EndTicks,
                $"Unexpected gap between segments {i} and {i + 1}.");
        }
    }

    [Fact]
    public void Generate_WhenFutureDragMoveExists_DoesNotJumpBeforeMoveOccurs()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 23, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var clickTicks = start + TimeSpan.FromSeconds(1).Ticks;
        var futureMoveTicks = clickTicks + TimeSpan.FromMilliseconds(600).Ticks;
        var events = new[]
        {
            new CursorEvent(clickTicks, 568f, 218f, CursorEventType.LeftDown),
            new CursorEvent(futureMoveTicks, 1032f, 213f, CursorEventType.Move),
            new CursorEvent(futureMoveTicks + TimeSpan.FromMilliseconds(50).Ticks, 1032f, 213f, CursorEventType.KeyPress)
        };

        var keyframes = generator.Generate(events, 1920, 1032);
        var frameBeforeMove = keyframes.First(
            k => k.StartTicks <= futureMoveTicks - TimeSpan.FromMilliseconds(1).Ticks
                && k.EndTicks > futureMoveTicks - TimeSpan.FromMilliseconds(1).Ticks
                && k.TargetRect.Width < 1920f);

        var centerX = frameBeforeMove.TargetRect.X + (frameBeforeMove.TargetRect.Width / 2f);
        var centerY = frameBeforeMove.TargetRect.Y + (frameBeforeMove.TargetRect.Height / 2f);

        Assert.InRange(centerX, 540f, 600f);
        Assert.InRange(centerY, 190f, 246f);
    }
}
