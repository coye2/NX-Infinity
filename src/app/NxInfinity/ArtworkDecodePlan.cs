using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;

namespace NxInfinity;

internal readonly record struct ArtworkDecodePlan(bool ByHeight, int Target, int Width, int Height)
{
    public long MaximumBytes => (long)Width * (long)Height * 4;

    public string CacheKey => (ByHeight ? "h" : "w") + Target;

    public static ArtworkDecodePlan ForViewport(int width, int height, double viewportWidth, double viewportHeight, double scaling, int maximumSide)
    {
        if (!double.IsFinite(scaling) || scaling <= 0.0)
        {
            scaling = 1.0;
        }
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0.0)
        {
            viewportWidth = 256.0;
        }
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0.0)
        {
            viewportHeight = viewportWidth;
        }
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException("width");
        }
        double num = Math.Min(1.0, Math.Max(viewportWidth / (double)width, viewportHeight / (double)height) * scaling);
        int val = (int)Math.Min(maximumSide, Math.Ceiling((double)width * num / 64.0) * 64.0);
        return Create(width, height, Math.Max(1, val), maximumSide);
    }

    public static ArtworkDecodePlan Create(int width, int height, int requestedWidth, int maximumSide)
    {
        if (width <= 0 || height <= 0 || requestedWidth <= 0 || maximumSide <= 0)
        {
            throw new ArgumentOutOfRangeException("width", "Artwork dimensions and preview limits must be positive.");
        }
        int num = Math.Min(width, Math.Min(requestedWidth, maximumSide));
        if ((double)height * (double)num / (double)width > (double)maximumSide)
        {
            return new ArtworkDecodePlan(ByHeight: true, maximumSide, Math.Max(1, (int)Math.Ceiling((double)width * (double)maximumSide / (double)height)), maximumSide);
        }
        return new ArtworkDecodePlan(ByHeight: false, num, num, Math.Max(1, (int)Math.Ceiling((double)height * (double)num / (double)width)));
    }

    public Bitmap Decode(Stream source)
    {
        if (Width == 1 || Height == 1)
        {
            using (Bitmap bitmap = new Bitmap(source))
            {
                return bitmap.CreateScaledBitmap(new PixelSize(Width, Height));
            }
        }
        if (!ByHeight)
        {
            return Bitmap.DecodeToWidth(source, Target);
        }
        return Bitmap.DecodeToHeight(source, Target);
    }
}
