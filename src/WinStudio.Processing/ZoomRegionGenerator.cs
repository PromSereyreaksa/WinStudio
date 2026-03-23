using WinStudio.Common;

namespace WinStudio.Processing;

public sealed class ZoomRegionGenerator
{
    private static readonly long BaseHoldTicks = TimeSpan.FromMilliseconds(1600).Ticks;
    private static readonly long BaseZoomInTicks = TimeSpan.FromMilliseconds(320).Ticks;
    private static readonly long BaseZoomOutTicks = TimeSpan.FromMilliseconds(480).Ticks;
    private static readonly long FocusPersistenceTicks = TimeSpan.FromMilliseconds(2200).Ticks;
    private static readonly long ActivityIdleTimeoutTicks = TimeSpan.FromSeconds(3).Ticks;
    private static readonly long ClickBurstMergeTicks = TimeSpan.FromMilliseconds(80).Ticks;
    private static readonly long HoldFollowMinStepTicks = TimeSpan.FromMilliseconds(70).Ticks;
    private static readonly long TypingStickTicks = TimeSpan.FromMilliseconds(900).Ticks;
    private const int TransitionSamples = 6;
    private const float ClickBurstMergeDistance = 20f;
    private const float HoldFollowDistanceThreshold = 18f;
    private const float DragFollowDistanceThreshold = 3f;
    private const float MaxEdgeZoomScale = 8f;
    private const float EdgeZoomPadding = 4f;
    private const float MinTargetWidthRatio = 0.14f;

    public IReadOnlyList<ZoomKeyframe> Generate(
        IReadOnlyList<CursorEvent> events,
        int captureWidth,
        int captureHeight,
        float zoomIntensity = 1.4f,
        float zoomSensitivity = 1.2f,
        float followSpeed = 1.15f)
    {
        if (captureWidth <= 0 || captureHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureWidth), "Capture dimensions must be positive.");
        }

        if (events.Count == 0)
        {
            return [];
        }

        var ordered = events.OrderBy(static e => e.TimestampTicks).ToArray();
        var clickEvents = ordered
            .Where(static e => e.EventType == CursorEventType.LeftDown)
            .OrderBy(static e => e.TimestampTicks)
            .ToArray();
        if (clickEvents.Length == 0)
        {
            return [];
        }
        clickEvents = MergeClickBursts(clickEvents);

        var allOrderedEvents = ordered;

        var safeIntensity = Math.Clamp(zoomIntensity, 0.6f, 2.2f);
        var safeSensitivity = Math.Clamp(zoomSensitivity, 0.6f, 2.0f);
        var safeSpeed = Math.Clamp(followSpeed, 0.6f, 2.0f);
        var zoomScale = Math.Clamp(1.12f + (safeIntensity * 0.52f), 1.18f, 2.25f);
        var zoomInTicks = (long)Math.Clamp(BaseZoomInTicks / safeSpeed, TimeSpan.FromMilliseconds(200).Ticks, TimeSpan.FromMilliseconds(420).Ticks);
        var zoomOutTicks = (long)Math.Clamp(BaseZoomOutTicks / safeSpeed, TimeSpan.FromMilliseconds(300).Ticks, TimeSpan.FromMilliseconds(680).Ticks);
        var baseHoldTicks = (long)Math.Clamp(BaseHoldTicks * safeSensitivity, TimeSpan.FromMilliseconds(900).Ticks, TimeSpan.FromMilliseconds(2600).Ticks);
        var followMinStepTicks = (long)Math.Clamp(HoldFollowMinStepTicks / safeSpeed, TimeSpan.FromMilliseconds(40).Ticks, TimeSpan.FromMilliseconds(90).Ticks);
        var passiveFollowThreshold = Math.Clamp(HoldFollowDistanceThreshold - ((safeSpeed - 1f) * 4f), 10f, 18f);

        var fullFrame = new RectF(0f, 0f, captureWidth, captureHeight);
        var timeline = new List<ZoomKeyframe>();
        var currentRect = fullFrame;

        for (var i = 0; i < clickEvents.Length; i++)
        {
            var clickEvent = clickEvents[i];
            var nextClickTicks = i < clickEvents.Length - 1
                ? clickEvents[i + 1].TimestampTicks
                : long.MaxValue;
            var targetRect = BuildTargetRect(clickEvent.X, clickEvent.Y, captureWidth, captureHeight, zoomScale);
            var needsTransition = !RectsClose(currentRect, targetRect);
            var holdStartTicks = clickEvent.TimestampTicks;
            var zoomInEndTicks = clickEvent.TimestampTicks + zoomInTicks;
            var holdEndTicks = zoomInEndTicks + ComputeActivityExtendedHoldTicks(clickEvent, allOrderedEvents, targetRect, baseHoldTicks);

            if (nextClickTicks < zoomInEndTicks)
            {
                zoomInEndTicks = nextClickTicks;
            }

            if (needsTransition)
            {
                AppendTransition(
                    timeline,
                    currentRect,
                    targetRect,
                    clickEvent.TimestampTicks,
                    zoomInEndTicks,
                    captureWidth,
                    captureHeight);

                holdStartTicks = zoomInEndTicks;
            }

            currentRect = targetRect;

            if (nextClickTicks < holdEndTicks)
            {
                holdEndTicks = nextClickTicks;
            }

            currentRect = AppendDynamicHoldSegments(
                timeline,
                allOrderedEvents,
                holdStartTicks,
                holdEndTicks,
                targetRect,
                captureWidth,
                captureHeight,
                followMinStepTicks,
                passiveFollowThreshold);

            if (nextClickTicks == long.MaxValue)
            {
                AppendTransition(
                    timeline,
                    currentRect,
                    fullFrame,
                    holdEndTicks,
                    holdEndTicks + zoomOutTicks,
                    captureWidth,
                    captureHeight);
                break;
            }

            var idleGapTicks = nextClickTicks - holdEndTicks;
            if (idleGapTicks <= FocusPersistenceTicks)
            {
                AppendSegment(
                    timeline,
                    holdEndTicks,
                    nextClickTicks,
                    currentRect,
                    EasingType.EaseInOutCubic,
                    EasingType.EaseOutCubic);
                continue;
            }

            var zoomOutEndTicks = holdEndTicks + zoomOutTicks;
            if (zoomOutEndTicks <= nextClickTicks)
            {
                AppendTransition(
                    timeline,
                    currentRect,
                    fullFrame,
                    holdEndTicks,
                    zoomOutEndTicks,
                    captureWidth,
                    captureHeight);
                currentRect = fullFrame;
            }
        }

        return timeline;
    }

    private static CursorEvent[] MergeClickBursts(IReadOnlyList<CursorEvent> clickEvents)
    {
        if (clickEvents.Count <= 1)
        {
            return clickEvents.ToArray();
        }

        var merged = new List<CursorEvent>(clickEvents.Count) { clickEvents[0] };
        for (var i = 1; i < clickEvents.Count; i++)
        {
            var current = clickEvents[i];
            var previous = merged[^1];
            var deltaTicks = current.TimestampTicks - previous.TimestampTicks;
            if (deltaTicks <= ClickBurstMergeTicks
                && Distance(previous.X, previous.Y, current.X, current.Y) <= ClickBurstMergeDistance)
            {
                merged[^1] = current;
                continue;
            }

            merged.Add(current);
        }

        return merged.ToArray();
    }

    private static RectF AppendDynamicHoldSegments(
        List<ZoomKeyframe> timeline,
        IReadOnlyList<CursorEvent> allEvents,
        long startTicks,
        long endTicks,
        RectF startRect,
        int captureWidth,
        int captureHeight,
        long followMinStepTicks,
        float passiveFollowDistanceThreshold)
    {
        if (endTicks <= startTicks)
        {
            return startRect;
        }

        var width = startRect.Width;
        var height = startRect.Height;
        var centerX = startRect.X + (width / 2f);
        var centerY = startRect.Y + (height / 2f);
        var lastAcceptedTicks = startTicks;
        var isDragging = IsLeftButtonDownAt(allEvents, startTicks);
        var typingStickyUntilTicks = long.MinValue;
        var anchors = new List<(long Ticks, float X, float Y)> { (startTicks, centerX, centerY) };

        for (var i = 0; i < allEvents.Count; i++)
        {
            var evt = allEvents[i];
            if (evt.TimestampTicks <= startTicks)
            {
                continue;
            }

            if (evt.TimestampTicks >= endTicks)
            {
                break;
            }

            var targetX = Math.Clamp(evt.X, 0f, captureWidth);
            var targetY = Math.Clamp(evt.Y, 0f, captureHeight);
            var distanceToTarget = Distance(centerX, centerY, targetX, targetY);

            switch (evt.EventType)
            {
                case CursorEventType.LeftDown:
                    isDragging = true;
                    if (evt.TimestampTicks - lastAcceptedTicks < followMinStepTicks || distanceToTarget < DragFollowDistanceThreshold)
                    {
                        continue;
                    }

                    centerX = targetX;
                    centerY = targetY;
                    break;

                case CursorEventType.LeftUp:
                    isDragging = false;
                    if (evt.TimestampTicks - lastAcceptedTicks < followMinStepTicks || distanceToTarget < DragFollowDistanceThreshold)
                    {
                        continue;
                    }

                    centerX = targetX;
                    centerY = targetY;
                    break;

                case CursorEventType.KeyPress:
                    typingStickyUntilTicks = evt.TimestampTicks + TypingStickTicks;
                    if (evt.TimestampTicks - lastAcceptedTicks < followMinStepTicks || distanceToTarget < DragFollowDistanceThreshold)
                    {
                        continue;
                    }

                    // Typing is a strong attention signal. Pin directly to the current mouse location.
                    centerX = targetX;
                    centerY = targetY;
                    break;

                case CursorEventType.Scroll:
                case CursorEventType.RightDown:
                    if (evt.TimestampTicks - lastAcceptedTicks < followMinStepTicks || distanceToTarget < passiveFollowDistanceThreshold)
                    {
                        continue;
                    }

                    centerX = targetX;
                    centerY = targetY;
                    break;

                case CursorEventType.Move:
                    if (evt.TimestampTicks <= typingStickyUntilTicks)
                    {
                        continue;
                    }

                    if (evt.TimestampTicks - lastAcceptedTicks < followMinStepTicks)
                    {
                        continue;
                    }

                    if (isDragging)
                    {
                        if (distanceToTarget < DragFollowDistanceThreshold)
                        {
                            continue;
                        }

                        // During click-drag selection, keep the crop locked to the cursor.
                        centerX = targetX;
                        centerY = targetY;
                    }
                    else
                    {
                        // Plain cursor travel between actions should not pull the camera off the
                        // clicked focus point. Keep hold duration extended by activity, but do not
                        // recenter unless the user is actively dragging.
                        continue;
                    }

                    break;

                default:
                    continue;
            }

            anchors.Add((evt.TimestampTicks, centerX, centerY));
            lastAcceptedTicks = evt.TimestampTicks;
        }

        anchors.Add((endTicks, centerX, centerY));
        for (var i = 0; i < anchors.Count - 1; i++)
        {
            var segmentStart = anchors[i].Ticks;
            var segmentEnd = anchors[i + 1].Ticks;
            if (segmentEnd <= segmentStart)
            {
                continue;
            }

            var fromRect = RectF.FromCenter(anchors[i].X, anchors[i].Y, width, height).ClampWithin(captureWidth, captureHeight);
            var toRect = RectF.FromCenter(anchors[i + 1].X, anchors[i + 1].Y, width, height).ClampWithin(captureWidth, captureHeight);
            // Keep this as a single segment to avoid huge FFmpeg expressions.
            AppendSegment(
                timeline,
                segmentStart,
                segmentEnd,
                fromRect,
                EasingType.EaseInOutCubic,
                EasingType.EaseOutCubic);
        }

        var finalAnchor = anchors[^1];
        return RectF.FromCenter(finalAnchor.X, finalAnchor.Y, width, height).ClampWithin(captureWidth, captureHeight);
    }

    private static bool IsLeftButtonDownAt(IReadOnlyList<CursorEvent> allEvents, long ticks)
    {
        var isDown = false;
        for (var i = 0; i < allEvents.Count; i++)
        {
            var evt = allEvents[i];
            if (evt.TimestampTicks > ticks)
            {
                break;
            }

            if (evt.EventType == CursorEventType.LeftDown)
            {
                isDown = true;
            }
            else if (evt.EventType == CursorEventType.LeftUp)
            {
                isDown = false;
            }
        }

        return isDown;
    }

    private static long ComputeActivityExtendedHoldTicks(
        CursorEvent clickEvent,
        IReadOnlyList<CursorEvent> allEvents,
        RectF targetRect,
        long baseHoldTicks)
    {
        _ = targetRect;

        // Start with the click itself as the last known activity.
        var lastActivityTicks = clickEvent.TimestampTicks;
        var idleDeadlineTicks = lastActivityTicks + ActivityIdleTimeoutTicks;

        for (var i = 0; i < allEvents.Count; i++)
        {
            var evt = allEvents[i];

            // Skip events at or before the click.
            if (evt.TimestampTicks <= clickEvent.TimestampTicks)
            {
                continue;
            }

            // Once we pass the idle deadline, no more activity can extend it.
            if (evt.TimestampTicks > idleDeadlineTicks)
            {
                break;
            }

            if (!IsActivityEvent(evt.EventType))
            {
                continue;
            }

            // This event counts as activity, so slide the idle window forward.
            lastActivityTicks = evt.TimestampTicks;
            idleDeadlineTicks = lastActivityTicks + ActivityIdleTimeoutTicks;
        }

        // The hold extends from the click until the idle deadline expires.
        var totalHoldTicks = idleDeadlineTicks - clickEvent.TimestampTicks;
        return Math.Max(baseHoldTicks, totalHoldTicks);
    }

    private static bool IsActivityEvent(CursorEventType eventType)
    {
        return eventType is CursorEventType.Move
            or CursorEventType.LeftDown
            or CursorEventType.LeftUp
            or CursorEventType.RightDown
            or CursorEventType.RightUp
            or CursorEventType.Scroll
            or CursorEventType.KeyPress;
    }

    private static RectF BuildTargetRect(float centerX, float centerY, int captureWidth, int captureHeight, float zoomScale)
    {
        if (zoomScale <= 1.03f)
        {
            return new RectF(0f, 0f, captureWidth, captureHeight);
        }

        var clampedCenterX = Math.Clamp(centerX, 0f, captureWidth);
        var clampedCenterY = Math.Clamp(centerY, 0f, captureHeight);
        var minDistanceX = MathF.Min(clampedCenterX, captureWidth - clampedCenterX);
        var minDistanceY = MathF.Min(clampedCenterY, captureHeight - clampedCenterY);
        var requiredScaleX = captureWidth / MathF.Max(2f, (2f * minDistanceX) + EdgeZoomPadding);
        var requiredScaleY = captureHeight / MathF.Max(2f, (2f * minDistanceY) + EdgeZoomPadding);
        var edgeAwareScale = Math.Max(zoomScale, Math.Min(MaxEdgeZoomScale, Math.Max(requiredScaleX, requiredScaleY)));

        var width = captureWidth / edgeAwareScale;
        var minWidth = captureWidth * MinTargetWidthRatio;
        if (width < minWidth)
        {
            width = minWidth;
        }

        var height = width * captureHeight / captureWidth;

        return RectF.FromCenter(clampedCenterX, clampedCenterY, width, height).ClampWithin(captureWidth, captureHeight);
    }

    private static void AppendTransition(
        List<ZoomKeyframe> timeline,
        RectF fromRect,
        RectF toRect,
        long startTicks,
        long endTicks,
        int captureWidth,
        int captureHeight)
    {
        if (endTicks <= startTicks)
        {
            AppendSegment(timeline, startTicks, endTicks, toRect, EasingType.EaseInOutCubic, EasingType.EaseOutCubic);
            return;
        }

        var durationTicks = endTicks - startTicks;
        for (var i = 0; i < TransitionSamples; i++)
        {
            var segmentStart = startTicks + ((durationTicks * i) / TransitionSamples);
            var segmentEnd = startTicks + ((durationTicks * (i + 1)) / TransitionSamples);
            var easedProgress = EaseInOutCubic((i + 1f) / TransitionSamples);
            var rect = LerpRect(fromRect, toRect, easedProgress).ClampWithin(captureWidth, captureHeight);
            AppendSegment(timeline, segmentStart, segmentEnd, rect, EasingType.EaseInOutCubic, EasingType.EaseOutCubic);
        }
    }

    private static void AppendSegment(
        List<ZoomKeyframe> timeline,
        long startTicks,
        long endTicks,
        RectF rect,
        EasingType easingIn,
        EasingType easingOut)
    {
        if (endTicks <= startTicks)
        {
            return;
        }

        if (timeline.Count > 0)
        {
            var last = timeline[^1];
            if (last.EndTicks == startTicks && RectsClose(last.TargetRect, rect))
            {
                timeline[^1] = last with { EndTicks = endTicks };
                return;
            }
        }

        timeline.Add(
            new ZoomKeyframe
            {
                StartTicks = startTicks,
                EndTicks = endTicks,
                TargetRect = rect,
                EasingIn = easingIn,
                EasingOut = easingOut,
                IsAutoGenerated = true
            });
    }

    private static RectF LerpRect(RectF from, RectF to, float t)
    {
        return new RectF(
            Lerp(from.X, to.X, t),
            Lerp(from.Y, to.Y, t),
            Lerp(from.Width, to.Width, t),
            Lerp(from.Height, to.Height, t));
    }

    private static bool RectsClose(RectF a, RectF b)
    {
        return Math.Abs(a.X - b.X) <= 1f
            && Math.Abs(a.Y - b.Y) <= 1f
            && Math.Abs(a.Width - b.Width) <= 1f
            && Math.Abs(a.Height - b.Height) <= 1f;
    }

    private static float EaseInOutCubic(float t)
    {
        var clamped = Math.Clamp(t, 0f, 1f);
        return clamped < 0.5f
            ? 4f * clamped * clamped * clamped
            : 1f - (MathF.Pow(-2f * clamped + 2f, 3f) / 2f);
    }

    private static float Distance(float x1, float y1, float x2, float y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static float Lerp(float from, float to, float t)
    {
        return from + ((to - from) * Math.Clamp(t, 0f, 1f));
    }
}
