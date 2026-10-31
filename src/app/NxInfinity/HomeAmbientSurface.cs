using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NxInfinity;

internal sealed class HomeAmbientSurface(HomeTheme theme) : Control
{
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(theme.Canvas, new Rect(Bounds.Size));
        Pen pen = new Pen(new SolidColorBrush(Color.Parse(theme.Palette.Accent), 0.065));
        if (theme.Choice.Pattern == "Dots")
        {
            for (double num = 24.0; num < Bounds.Width; num += 44.0)
            {
                for (double num2 = 24.0; num2 < Bounds.Height; num2 += 44.0)
                {
                    context.DrawEllipse(pen.Brush, null, new Point(num, num2), 1.5, 1.5);
                }
            }
        }
        else if (theme.Choice.Pattern == "Grid")
        {
            for (double num3 = 0.0; num3 < Bounds.Width; num3 += 64.0)
            {
                context.DrawLine(pen, new Point(num3, 0.0), new Point(num3, Bounds.Height));
            }
            for (double num4 = 0.0; num4 < Bounds.Height; num4 += 64.0)
            {
                context.DrawLine(pen, new Point(0.0, num4), new Point(Bounds.Width, num4));
            }
        }
        else if (theme.Choice.Pattern == "Orbits")
        {
            for (int i = 1; i <= 7; i++)
            {
                context.DrawEllipse(null, pen, new Point(Bounds.Width * 0.82, Bounds.Height * 0.68), i * 100, i * 70);
            }
        }
    }
}
