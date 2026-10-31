using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace NxInfinity;

internal sealed class LibraryTilePanel(double minimumTileWidth) : Panel
{
    private const double Gap = 16.0;

    private readonly List<(Button Card, Control Artwork)> _tiles = new List<(Button, Control)>();

    private double _tileWidth;

    private int _columns = 1;

    private double[] _rowHeights = Array.Empty<double>();

    internal void Add(Button card, Control artwork)
    {
        _tiles.Add((card, artwork));
        Children.Add(card);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double num = (double.IsFinite(availableSize.Width) ? Math.Max(1.0, availableSize.Width) : minimumTileWidth);
        _columns = Math.Clamp((int)((num + 16.0) / (minimumTileWidth + 16.0)), 1, 6);
        _tileWidth = (num - (double)(_columns - 1) * 16.0) / (double)_columns;
        _rowHeights = new double[(_tiles.Count + _columns - 1) / _columns];
        for (int i = 0; i < _tiles.Count; i++)
        {
            var (button, control) = _tiles[i];
            double width = (control.Height = Math.Max(1.0, _tileWidth - 18.0));
            control.Width = width;
            button.Width = _tileWidth;
            button.Measure(new Size(_tileWidth, double.PositiveInfinity));
            _rowHeights[i / _columns] = Math.Max(_rowHeights[i / _columns], button.DesiredSize.Height);
        }
        return new Size(num, _rowHeights.Sum() + (double)Math.Max(0, _rowHeights.Length - 1) * 16.0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double num = 0.0;
        for (int i = 0; i < _rowHeights.Length; i++)
        {
            for (int j = 0; j < _columns; j++)
            {
                int num2 = i * _columns + j;
                if (num2 >= _tiles.Count)
                {
                    break;
                }
                _tiles[num2].Card.Arrange(new Rect((double)j * (_tileWidth + 16.0), num, _tileWidth, _rowHeights[i]));
            }
            num += _rowHeights[i] + 16.0;
        }
        return finalSize;
    }
}
