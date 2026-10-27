using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NxInfinity;

public sealed class NavGlyph : Control
{
    private static readonly IReadOnlyDictionary<string, Geometry[]> Geometries = IconData.Paths.ToDictionary((KeyValuePair<string, string[]> p) => p.Key, (KeyValuePair<string, string[]> p) => p.Value.Select(Geometry.Parse).ToArray(), StringComparer.Ordinal);

    public required string Kind { get; init; }

    public IBrush Stroke { get; set; } = VisualTheme.Ink;

    public override void Render(DrawingContext context)
    {
        string key = Kind switch
        {
            "Library" => "grid-3x3",
            "Orbit" => "gamepad-2",
            "Enhancements" => "sliders-horizontal",
            "Content" => "folder-open",
            "Saves" => "archive",
            "Performance" => "activity",
            "Diagnostics" => "search",
            "Settings" => "settings",
            "Controls" => "gamepad-2",
            _ => Kind,
        };
        if (!Geometries.TryGetValue(key, out Geometry[] value))
        {
            return;
        }
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24.0, Bounds.Height / 24.0)))
        {
            Pen pen = new Pen(Stroke, 1.8, null, PenLineCap.Round, PenLineJoin.Round);
            Geometry[] array = value;
            foreach (Geometry geometry in array)
            {
                context.DrawGeometry(null, pen, geometry);
            }
        }
    }
}
