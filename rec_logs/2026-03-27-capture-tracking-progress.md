# WinStudio Capture/Zoom Debug Progress (March 27, 2026)

## Scope
Window capture + cursor tracking + zoom follow pipeline in `ScreenStudioRecorderService`.

## What Was Fixed

1. Cursor/video coordinate alignment tightened.
   - Added raw video size probing via `ffprobe` and mapped cursor events into actual video space before zoom keyframe generation.
   - Goal: avoid mismatches when capture bounds and encoded dimensions differ.

2. FFmpeg post-processing failure fixed.
   - Error seen: `Error applying option 'eval' to filter 'crop': Option not found`.
   - Removed unsupported `crop:...:eval=frame` usage in this FFmpeg build.
   - Post-processing resumed and `.processed.mp4` files are produced.

3. Window bounds handling updated for maximized windows.
   - First attempt forced full monitor bounds for maximized windows (helped 1032 -> 1080 in some cases, but introduced right-edge crop for some layouts).
   - Revised to safer logic:
     - start from real window rect,
     - only extend vertical bounds to monitor bottom when left/right already align with monitor and only a bottom work-area gap exists.

4. Filter planner corrected to default back to full-frame outside zoom segments.
   - `x/y/zoom` fallback values now default to full-frame (`x=0`, `y=0`, `zoom=1`) instead of inheriting the last reduced zoom segment.
   - This directly targets:
     - zoom staying cropped after activity stops,
     - camera not resetting to the full window,
     - right-side crop persisting after zoom periods.

5. Segment reduction now preserves full-frame runs.
   - Reduction no longer averages full-frame and zoomed runs together.
   - Goal: stop the expression reducer from turning leading/trailing full-frame periods into a partial zoomed crop.

## Evidence From Latest Logs

- Older behavior (work-area height):
  - `resolvedBounds=0,0 1920x1032`
  - FFmpeg: `Capturing whole desktop as 1920x1032`.

- After maximized-window change:
  - `recording-20260326-184706.recording.ffmpeg.log`: `resolvedBounds=0,0 1920x1080`.
  - `recording-20260326-184738.recording.ffmpeg.log`: `resolvedBounds=0,0 1920x1080`.
  - Processing also reports 1920x1080 input and output.

- FFmpeg processing failure resolved:
  - Prior failure log contained `Error applying option 'eval' to filter 'crop': Option not found`.
  - New processing logs complete encode successfully with output mp4.

## Still Failing / Open Bugs

1. Right side of captured window still appears cropped in current user validation.
   - Even after bounds updates, user reports missing right side.

2. Zoom region can still appear locked/stuck in user-visible behavior.

3. Inactivity reset behavior is still wrong.
   - Expected: camera returns to full frame after no activity.
   - Actual: reported as not resetting to full screen.

4. Latest code patch not yet user-validated.
   - The fallback-to-full-frame and preserve-full-frame-run changes were implemented after the last failing user report and still need a fresh recording to confirm.

## Current Assessment

- Recording and post-processing pipeline is functional (files generated, FFmpeg exits cleanly).
- Remaining issues are correctness/behavior issues in capture region selection and zoom timeline behavior, not process crashes.

## Next Recommended Debug Steps

1. Add explicit debug logging of raw window rect + monitor rect + final chosen rect each session.
2. Record one failing run and inspect:
   - `recording-*.recording.ffmpeg.log`
   - `recording-*.raw.cursor.json`
   - `recording-*.raw.zoom.json`
   - `recording-*.processed.filter.txt`
3. Validate whether right-crop is introduced:
   - at raw capture stage (bounds wrong), or
   - at processed stage (zoom/filter expression clamping).
4. Validate idle timeout path in zoom keyframe timeline:
   - ensure full-frame keyframe exists after inactivity window.
