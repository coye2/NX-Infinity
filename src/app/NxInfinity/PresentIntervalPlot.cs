using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NxInfinity.Runtime;

namespace NxInfinity;

public sealed class PresentIntervalPlot : Control
{
    public IReadOnlyList<PerformanceInterval> Samples { get; set; } = Array.Empty<PerformanceInterval>();

    public double ScaleMaximum => Math.Max(40.0, Math.Ceiling(((Samples.Count == 0) ? 0.0 : Samples.Max((PerformanceInterval sample) => sample.Milliseconds)) / 10.0) * 10.0);

    public override void Render(DrawingContext context)
    {
        Rect rect = new Rect(Bounds.Size);
        context.FillRectangle(VisualTheme.Canvas, rect, 8f);
        double left = 12.0;
        double top = 12.0;
        double width = Math.Max(1.0, Bounds.Width - 24.0);
        double height = Math.Max(1.0, Bounds.Height - 24.0);
        double maximum = ScaleMaximum;
        double[] array = new double[4] { 16.6667, 33.3333, 50.0, 100.0 };
        foreach (double num in array)
        {
            if (!(num > maximum))
            {
                double y = top + height * (1.0 - num / maximum);
                context.DrawLine(new Pen(VisualTheme.Line), new Point(left, y), new Point(left + width, y));
            }
        }
        if (Samples.Count == 0)
        {
            return;
        }
        for (int j = 0; j < Samples.Count; j++)
        {
            Point point = At(j);
            IBrush brush = ((Samples[j].Milliseconds > 50.0) ? VisualTheme.Red : VisualTheme.Blue);
            if (j > 0 && Samples[j].SegmentId == Samples[j - 1].SegmentId && Samples[j].Sequence == Samples[j - 1].Sequence + 1)
            {
                context.DrawLine(new Pen(brush, 1.7), At(j - 1), point);
            }
            else
            {
                context.DrawEllipse(brush, null, point, 2.2, 2.2);
            }
        }
        Point At(int index)
        {
            return new Point(left + ((Samples.Count <= 1) ? (width / 2.0) : (width * (double)index / (double)(Samples.Count - 1))), top + height * (1.0 - Samples[index].Milliseconds / maximum));
        }
    }
}
