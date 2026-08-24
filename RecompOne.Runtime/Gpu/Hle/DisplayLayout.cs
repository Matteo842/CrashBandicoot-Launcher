namespace RecompOne.Runtime.Hle;

/// <summary>Aspect-correct sizing shared by the desktop and Android presenters.</summary>
internal static class DisplayLayout
{
    public static (float Width, float Height) Fit(
        float availableWidth,
        float availableHeight,
        float aspect,
        int nativeHeight,
        bool integerScale)
    {
        if (availableWidth <= 0f || availableHeight <= 0f || aspect <= 0f)
            return (0f, 0f);

        float fittedHeight = MathF.Min(availableWidth / aspect, availableHeight);

        if (integerScale && nativeHeight > 0)
        {
            // PS1 framebuffer pixels are not necessarily square, so the raw texture
            // dimensions cannot also describe the displayed shape. Prefer whole source
            // scanlines, but never shrink the picture enough to introduce extra bars on
            // the limiting axis; in that case an aspect-correct fractional fit is better.
            float nativeDisplayWidth = nativeHeight * aspect;
            int scale = (int)MathF.Floor(MathF.Min(
                availableWidth / nativeDisplayWidth,
                availableHeight / nativeHeight));
            float integerHeight = nativeHeight * scale;
            if (scale >= 1 && fittedHeight - integerHeight < 0.5f)
                return (nativeDisplayWidth * scale, integerHeight);
        }

        return (fittedHeight * aspect, fittedHeight);
    }
}
