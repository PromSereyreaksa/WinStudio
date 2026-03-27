using WinStudio.Common;
using WinStudio.Processing;
using Xunit;

namespace WinStudio.Processing.Tests;

public sealed class ZoomRegionGeneratorTests
{
    // Zoom activates on explicit trigger (click/scroll) and then follows cursor movement
    [Fact]
    public void Generate_WhenClickTriggersZoom_ZoomsAndFollows()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var events = new List<CursorEvent>();

        events.Add(new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 800f, 500f, CursorEventType.LeftDown));

        for (var i = 1; i <= 10; i++)
        {
            events.Add(
                new CursorEvent(
                    start + TimeSpan.FromSeconds(1).Ticks + TimeSpan.FromMilliseconds(100 + i * 50).Ticks,
                    800f + (i * 30f),
                    500f + (i * 20f),
                    CursorEventType.Move));
        }

        var keyframes = generator.Generate(events, 1920, 1032);

        Assert.Contains(keyframes, k => k.TargetRect.Width < 1919f);
    }

    // A phantom LeftUp at startup (orphan from a prior drag) must not zoom the camera.
    // Subsequent micro-movements that stay below the auto-activate threshold must also
    // not trigger zoom — only the LeftUp event itself is verified here.
    [Fact]
    public void Generate_WhenStreamStartsWithIsolatedMouseUp_DoesNotActivateZoom()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        // Moves are intentionally small (< autoActivateDistance) so only the
        // orphan LeftUp is the candidate trigger — and it must not activate zoom.
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks,   420f, 220f, CursorEventType.LeftUp),
            new CursorEvent(start + TimeSpan.FromSeconds(1.2).Ticks, 430f, 226f, CursorEventType.Move),
            new CursorEvent(start + TimeSpan.FromSeconds(1.5).Ticks, 439f, 231f, CursorEventType.Move)
        };

        var keyframes = generator.Generate(events, 1920, 1032);

        Assert.All(
            keyframes,
            keyframe => AssertFullFrame(keyframe.TargetRect, "An isolated startup mouse-up must not start zoom."));
    }

    [Fact]
    public void Generate_WhenPointerJumpsToNewClickLocation_CameraRecentersQuickly()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var clickTicks = start + TimeSpan.FromSeconds(1).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromMilliseconds(820).Ticks, 180f, 190f, CursorEventType.Move),
            new CursorEvent(start + TimeSpan.FromMilliseconds(900).Ticks, 230f, 210f, CursorEventType.Move),
            new CursorEvent(clickTicks, 1340f, 460f, CursorEventType.LeftDown)
        };

        var keyframes = generator.Generate(events, 1920, 1032);
        var focusCenter = GetCenterAt(keyframes, clickTicks + TimeSpan.FromMilliseconds(120).Ticks);

        Assert.InRange(focusCenter.X, 1275f, 1405f);
        Assert.InRange(focusCenter.Y, 405f, 515f);
    }

    [Fact]
    public void Generate_WhenPointerMovesRapidlyAfterClick_StaysNearPointer()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var actionStartTicks = start + TimeSpan.FromSeconds(1).Ticks;
        var events = new List<CursorEvent>
        {
            new(actionStartTicks, 420f, 280f, CursorEventType.LeftDown)
        };

        for (var i = 1; i <= 16; i++)
        {
            events.Add(
                new CursorEvent(
                    actionStartTicks + TimeSpan.FromMilliseconds(i * 40).Ticks,
                    420f + (i * 52f),
                    280f + (i * 14f),
                    CursorEventType.Move));
        }

        var keyframes = generator.Generate(events, 1920, 1032);
        foreach (var moveEvent in events.Where(static e => e.EventType == CursorEventType.Move).Skip(5))
        {
            var frame = GetFrameAt(keyframes, moveEvent.TimestampTicks);
            Assert.True(
                ContainsWithMargin(frame.TargetRect, moveEvent.X, moveEvent.Y, 110f, 70f),
                $"Expected camera to keep the moving pointer inside the follow zone. pointer=({moveEvent.X},{moveEvent.Y}) rect=({frame.TargetRect.X},{frame.TargetRect.Y} {frame.TargetRect.Width}x{frame.TargetRect.Height})");
        }
    }

    [Fact]
    public void Generate_WhenPointerMovesDuringZoomInTransition_RetargetsBeforeCursorEscapes()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var clickTicks = start + TimeSpan.FromSeconds(1).Ticks;
        var moveTicks = clickTicks + TimeSpan.FromMilliseconds(60).Ticks;
        var sampleTicks = clickTicks + TimeSpan.FromMilliseconds(320).Ticks;
        var events = new[]
        {
            new CursorEvent(clickTicks, 180f, 360f, CursorEventType.LeftDown),
            new CursorEvent(moveTicks, 1320f, 402f, CursorEventType.Move)
        };

        var keyframes = generator.Generate(events, 1920, 1032, zoomIntensity: 2.2f, followSpeed: 1.2f);
        var frame = GetFrameAt(keyframes, sampleTicks);

        Assert.True(
            ContainsWithMargin(frame.TargetRect, 1320f, 402f, 80f, 60f),
            $"Expected in-flight zoom to retarget toward the moved pointer. rect=({frame.TargetRect.X},{frame.TargetRect.Y} {frame.TargetRect.Width}x{frame.TargetRect.Height})");
    }

    [Fact]
    public void Generate_WhenPointerPushesTowardCropEdge_ShiftsRegionBeforeCursorEscapes()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var actionStartTicks = start + TimeSpan.FromSeconds(1).Ticks;
        var events = new List<CursorEvent>
        {
            new(actionStartTicks, 540f, 360f, CursorEventType.LeftDown)
        };

        for (var i = 1; i <= 14; i++)
        {
            events.Add(
                new CursorEvent(
                    actionStartTicks + TimeSpan.FromMilliseconds(520 + (i * 45)).Ticks,
                    540f + (i * 68f),
                    360f + (i * 24f),
                    CursorEventType.Move));
        }

        var keyframes = generator.Generate(events, 1920, 1032, zoomIntensity: 2.2f, followSpeed: 1.2f);
        foreach (var moveEvent in events.Where(static e => e.EventType == CursorEventType.Move).Skip(4))
        {
            var frame = GetFrameAt(keyframes, moveEvent.TimestampTicks);
            Assert.True(
                ContainsWithMargin(frame.TargetRect, moveEvent.X, moveEvent.Y, 90f, 64f),
                $"Expected camera to shift before pointer escaped. pointer=({moveEvent.X},{moveEvent.Y}) rect=({frame.TargetRect.X},{frame.TargetRect.Y} {frame.TargetRect.Width}x{frame.TargetRect.Height})");
        }
    }

    [Fact]
    public void Generate_WhenPointerKeepsMovingDuringActiveZoom_MovementExtendsTheZoomSession()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var clickTicks = start + TimeSpan.FromSeconds(1).Ticks;
        var events = new List<CursorEvent>
        {
            new(clickTicks, 360f, 220f, CursorEventType.LeftDown)
        };

        for (var i = 1; i <= 9; i++)
        {
            events.Add(
                new CursorEvent(
                    clickTicks + TimeSpan.FromMilliseconds(i * 420).Ticks,
                    360f + (i * 70f),
                    220f + (i * 22f),
                    CursorEventType.Move));
        }

        var keyframes = generator.Generate(events, 1920, 1032);
        var frameAfterInitialHoldWindow = GetFrameAt(keyframes, clickTicks + TimeSpan.FromSeconds(3.8).Ticks);

        Assert.True(frameAfterInitialHoldWindow.TargetRect.Width < 1920f, "Continued cursor movement during an active zoom session should keep zoom alive.");
    }

    // After a zoom-out, re-activation must be blocked for the refractory period
    // (~600 ms) so the camera does not oscillate zoom-out → immediate zoom-in.
    [Fact]
    public void Generate_WhenHoverMovesOccurWithinRefractoryAfterZoomExpires_DoesNotReactivateZoom()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var clickTicks = start + TimeSpan.FromSeconds(1).Ticks;
        // Zoom-out ends at ~clickTicks + 2.22 s (2 s idle + ~220 ms transition).
        // Place the hover move 200 ms after that — well inside the 600 ms refractory.
        var lateHoverTicks = clickTicks + TimeSpan.FromSeconds(2.42).Ticks;
        var events = new[]
        {
            new CursorEvent(clickTicks,      520f,  320f, CursorEventType.LeftDown),
            new CursorEvent(lateHoverTicks, 1180f,  650f, CursorEventType.Move)
        };

        var keyframes = generator.Generate(events, 1920, 1032);

        Assert.DoesNotContain(
            keyframes,
            keyframe => keyframe.TargetRect.Width < 1919f
                && keyframe.StartTicks <= lateHoverTicks + TimeSpan.FromMilliseconds(120).Ticks
                && keyframe.EndTicks > lateHoverTicks + TimeSpan.FromMilliseconds(120).Ticks);
    }

    [Fact]
    public void Generate_WhenActivityStopsAfterActivation_ZoomsOutAfterIdleTimeout()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var activityTicks = start + TimeSpan.FromSeconds(1).Ticks;
        var events = new[]
        {
            new CursorEvent(activityTicks, 880f, 420f, CursorEventType.LeftDown)
        };

        var keyframes = generator.Generate(events, 1920, 1032);
        var firstZoomedFrame = keyframes.First(k => k.StartTicks >= activityTicks && k.TargetRect.Width < 1919f);
        var firstFullFrameAfterZoom = keyframes.First(
            k => k.StartTicks > firstZoomedFrame.StartTicks
                && k.TargetRect.Width >= 1919f
                && k.TargetRect.Height >= 1031f);
        var elapsedSeconds = TimeSpan.FromTicks(firstFullFrameAfterZoom.StartTicks - activityTicks).TotalSeconds;

        Assert.InRange(elapsedSeconds, 2.0, 3.3);
    }

    [Fact]
    public void Generate_WhenOnlyMicroJitterFollowsClick_ZoomMayExpireDueToInactivity()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var clickTicks = start + TimeSpan.FromSeconds(1).Ticks;
        var events = new List<CursorEvent>
        {
            new(clickTicks, 880f, 420f, CursorEventType.LeftDown)
        };

        for (var i = 1; i <= 18; i++)
        {
            events.Add(
                new CursorEvent(
                    clickTicks + TimeSpan.FromMilliseconds(i * 180).Ticks,
                    880f + ((i % 2 == 0) ? 1.5f : -1.5f),
                    420f + ((i % 3 == 0) ? 1f : -1f),
                    CursorEventType.Move));
        }

        var keyframes = generator.Generate(events, 1920, 1032);
        var zoomedFrames = keyframes.Where(k => k.TargetRect.Width < 1919f || k.TargetRect.Height < 1031f).ToList();
        
        Assert.NotEmpty(zoomedFrames);
    }

    [Fact]
    public void Generate_WhenScrollAndTypingContinue_KeepsZoomActive()
    {
        var generator = new ZoomRegionGenerator();
        var start = new DateTime(2026, 03, 24, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var events = new[]
        {
            new CursorEvent(start + TimeSpan.FromSeconds(1).Ticks, 760f, 320f, CursorEventType.Scroll),
            new CursorEvent(start + TimeSpan.FromSeconds(2.4).Ticks, 760f, 320f, CursorEventType.KeyPress)
        };

        var keyframes = generator.Generate(events, 1920, 1032);
        var frameDuringExtendedActivity = GetFrameAt(keyframes, start + TimeSpan.FromSeconds(3.6).Ticks);

        Assert.True(frameDuringExtendedActivity.TargetRect.Width < 1920f, "Expected zoom to remain active while action signals continue.");
    }

    private static void AssertFullFrame(RectF rect, string because)
    {
        Assert.True(
            rect.X <= 0.01f
            && rect.Y <= 0.01f
            && rect.Width >= 1919f
            && rect.Height >= 1031f,
            $"{because} rect=({rect.X},{rect.Y} {rect.Width}x{rect.Height})");
    }

    private static ZoomKeyframe GetFrameAt(IReadOnlyList<ZoomKeyframe> keyframes, long ticks)
    {
        return keyframes.First(k => k.StartTicks <= ticks && k.EndTicks > ticks);
    }

    private static (float X, float Y) GetCenterAt(IReadOnlyList<ZoomKeyframe> keyframes, long ticks)
    {
        return GetCenter(GetFrameAt(keyframes, ticks).TargetRect);
    }

    private static (float X, float Y) GetCenter(RectF rect)
    {
        return (rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f));
    }

    private static bool ContainsWithMargin(RectF rect, float x, float y, float minMarginX, float minMarginY)
    {
        return x >= rect.X + minMarginX
            && x <= rect.Right - minMarginX
            && y >= rect.Y + minMarginY
            && y <= rect.Bottom - minMarginY;
    }
}
