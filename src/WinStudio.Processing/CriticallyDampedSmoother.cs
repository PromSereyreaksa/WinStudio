namespace WinStudio.Processing;

internal static class CriticallyDampedSmoother
{
    public static float SmoothDamp(
        float current,
        float target,
        ref float currentVelocity,
        float smoothTime,
        float maxSpeed,
        float deltaTime)
    {
        var clampedSmoothTime = Math.Max(0.0001f, smoothTime);
        var omega = 2f / clampedSmoothTime;
        var x = omega * deltaTime;
        var exp = 1f / (1f + x + (0.48f * x * x) + (0.235f * x * x * x));
        var change = current - target;
        var originalTarget = target;
        var maxChange = maxSpeed * clampedSmoothTime;
        change = Math.Clamp(change, -maxChange, maxChange);
        target = current - change;
        var temp = (currentVelocity + (omega * change)) * deltaTime;
        currentVelocity = (currentVelocity - (omega * temp)) * exp;
        var output = target + ((change + temp) * exp);

        if ((originalTarget - current > 0f) == (output > originalTarget))
        {
            output = originalTarget;
            currentVelocity = (output - originalTarget) / Math.Max(deltaTime, 0.0001f);
        }

        return output;
    }
}
