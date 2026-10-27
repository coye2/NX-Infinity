using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NxInfinity;

public sealed class BrandMark : Control
{
    private static readonly Geometry Left = Geometry.Parse("M300 180 H360 V844 H300 C140 844 72 764 72 644 V380 C72 260 140 180 300 180 Z M300 272 C206 272 132 306 132 396 V628 C132 722 206 756 300 756 Z");

    private static readonly Geometry Back = Geometry.Parse("M380 180 L646 464 L380 816 Z");

    private static readonly Geometry Front = Geometry.Parse("M380 180 L646 464 V784 L380 464 Z");

    private static readonly Geometry Right = Geometry.Parse("M666 180 H716 C876 180 952 258 952 384 V640 C952 768 876 844 716 844 H666 Z M884 544 A76 76 0 1 0 732 544 A76 76 0 1 0 884 544 Z");

    private static readonly IBrush Red = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.0, 0.0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1.0, 1.0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#ff2b32"), 0.0),
            new GradientStop(Color.Parse("#ed1326"), 1.0)
        }
    };

    private static readonly IBrush White = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.0, 0.0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1.0, 1.0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Colors.White, 0.0),
            new GradientStop(Color.Parse("#eeeeef"), 1.0)
        }
    };

    public bool Api { get; init; }

    public override void Render(DrawingContext context)
    {
        double num = Math.Min(Bounds.Width, Bounds.Height);
        using (context.PushTransform(Matrix.CreateScale(num / 1024.0, num / 1024.0) * Matrix.CreateTranslation((Bounds.Width - num) / 2.0, (Bounds.Height - num) / 2.0)))
        {
            context.DrawGeometry(Red, null, Left);
            context.DrawEllipse(Red, null, new Point(207.0, 412.0), 66.0, 66.0);
            context.DrawGeometry(Brush.Parse("#aa071d"), null, Back);
            context.DrawGeometry(Red, null, Front);
            context.DrawGeometry(White, null, Right);
        }
    }
}
