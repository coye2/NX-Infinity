using System;

namespace NxInfinity;

public static class DesktopBackgroundDecodePlan
{
    public const int MaximumSide = 3840;

    public static int RequestedWidth(double viewportWidth, double renderScaling)
    {
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0.0)
        {
            viewportWidth = 1920.0;
        }
        if (!double.IsFinite(renderScaling) || renderScaling <= 0.0)
        {
            renderScaling = 1.0;
        }
        return Math.Clamp((int)Math.Ceiling(Math.Min(3840.0, viewportWidth * renderScaling) / 256.0) * 256, 256, 3840);
    }
}
