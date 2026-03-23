namespace WinStudio.Common;

public static class EasingFunctions
{
    public static float Evaluate(EasingType easing, float t)
    {
        var clamped = Math.Clamp(t, 0f, 1f);

        return easing switch
        {
            EasingType.EaseInOutCubic => clamped < 0.5f
                ? 4f * clamped * clamped * clamped
                : 1f - MathF.Pow(-2f * clamped + 2f, 3f) / 2f,
            EasingType.EaseOutCubic => 1f - MathF.Pow(1f - clamped, 3f),
            _ => clamped
        };
    }
}

