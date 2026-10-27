using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using NxInfinity.Runtime.Services;

namespace NxInfinity;

public sealed class DuneLibraryShelf : UserControl
{
    private readonly Canvas _canvas = new Canvas
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top
    };

    private readonly ScrollViewer _scroll;

    private readonly Func<GameLibraryEntry, double, Button> _factory;

    private readonly Action<GameLibraryEntry> _select;

    private readonly Dictionary<int, Button> _realized = new Dictionary<int, Button>();

    private IReadOnlyList<GameLibraryEntry> _games;

    private int _columns = 4;

    private double _size = 100.0;

    private string? _selected;

    private double? _restoreOffset;

    private int? _pendingIndex;

    public int RealizedCount => _realized.Count;

    public int Columns => _columns;

    public double CoverSize => _size;

    public double Offset => _scroll.Offset.Y;

    public event Action<double>? Scrolled;

    public DuneLibraryShelf(IReadOnlyList<GameLibraryEntry> games, Func<GameLibraryEntry, double, Button> factory, Action<GameLibraryEntry> select, string? selected, double offset = 0.0)
    {
        Name = "GameShelf";
        _restoreOffset = offset;
        _games = games;
        _factory = factory;
        _select = select;
        _selected = selected;
        _scroll = new ScrollViewer
        {
            Content = _canvas,
            BringIntoViewOnFocusChange = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(0.0, 0.0, 6.0, 0.0)
        };
        Content = _scroll;
        SizeChanged += (object? _, SizeChangedEventArgs _) =>
        {
            LayoutShelf();
        };
        _scroll.ScrollChanged += (object? _, ScrollChangedEventArgs _) =>
        {
            Realize();
            double? restoreOffset = _restoreOffset;
            if (!restoreOffset.HasValue)
            {
                Scrolled?.Invoke(_scroll.Offset.Y);
            }
        };
        AttachedToVisualTree += (object? _, VisualTreeAttachmentEventArgs _) =>
        {
            LayoutShelf();
        };
        LayoutUpdated += (object? _, EventArgs _) =>
        {
            int? pendingIndex = _pendingIndex;
            if (pendingIndex.HasValue)
            {
                ApplyPendingSelection();
            }
            else
            {
                double? restoreOffset = _restoreOffset;
                if (restoreOffset.HasValue)
                {
                    double valueOrDefault = restoreOffset.GetValueOrDefault();
                    if (!(_scroll.Viewport.Height <= 0.0) && !(_scroll.Extent.Height <= 0.0))
                    {
                        _restoreOffset = null;
                        _scroll.Offset = new Vector(0.0, valueOrDefault);
                        Realize();
                    }
                }
            }
        };
    }

    public void Filter(IReadOnlyList<GameLibraryEntry> games)
    {
        if (_games.Select((GameLibraryEntry g) => g.EntryId).SequenceEqual(games.Select((GameLibraryEntry g) => g.EntryId), StringComparer.Ordinal))
        {
            _games = games;
            return;
        }
        _games = games;
        _restoreOffset = null;
        _pendingIndex = null;
        Clear();
        _scroll.Offset = default;
        LayoutShelf();
    }

    public void Select(string entryId)
    {
        _selected = entryId;
        foreach (var (index, button2) in _realized)
        {
            button2.Classes.Set("dune-selected", _games[index].EntryId == entryId);
        }
    }

    public void ScrollToIndex(int index)
    {
        if (_games.Count != 0)
        {
            index = Math.Clamp(index, 0, _games.Count - 1);
            _restoreOffset = null;
            _pendingIndex = index;
            _select(_games[index]);
            Select(_games[index].EntryId);
            _scroll.UpdateLayout();
            ApplyPendingSelection();
        }
    }

    private void ApplyPendingSelection()
    {
        int? pendingIndex = _pendingIndex;
        if (!pendingIndex.HasValue)
        {
            return;
        }
        int index = pendingIndex.GetValueOrDefault();
        if (_scroll.Viewport.Height <= 0.0 || _scroll.Extent.Height + 0.5 < _canvas.Height)
        {
            return;
        }
        _pendingIndex = null;
        double num = (double)(index / _columns) * (_size + 8.0);
        double num2 = num + _size;
        if (num < _scroll.Offset.Y)
        {
            _scroll.Offset = new Vector(0.0, num);
        }
        else if (num2 > _scroll.Offset.Y + _scroll.Viewport.Height)
        {
            _scroll.Offset = new Vector(0.0, num2 - _scroll.Viewport.Height);
        }
        Realize();
        Scrolled?.Invoke(_scroll.Offset.Y);
        if (!_realized.TryGetValue(index, out Button button))
        {
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!(_selected != _games[index].EntryId) && _realized.TryGetValue(index, out Button value) && value == button)
            {
                _canvas.UpdateLayout();
                button.Focus(NavigationMethod.Directional);
            }
        }, DispatcherPriority.Loaded);
    }

    private void Clear()
    {
        _canvas.Children.Clear();
        _realized.Clear();
    }

    private void LayoutShelf()
    {
        double num = Math.Max(100.0, Bounds.Width - 8.0);
        int num2;
        if (num >= 850.0)
        {
            num2 = 6;
        }
        else if (num >= 550.0)
        {
            num2 = 5;
        }
        else if (num >= 380.0)
        {
            num2 = 4;
        }
        else
        {
            num2 = ((num >= 270.0) ? 3 : 2);
        }
        double num3 = Math.Floor((num - (double)((num2 - 1) * 8)) / (double)num2);
        if (num2 != _columns || Math.Abs(num3 - _size) > 0.5)
        {
            _columns = num2;
            _size = num3;
            Clear();
        }
        _canvas.Width = num;
        _canvas.Height = Math.Ceiling((double)_games.Count / (double)_columns) * (_size + 8.0);
        Realize();
    }

    private void Realize()
    {
        if (Bounds.Height <= 0.0 || _size <= 0.0)
        {
            return;
        }
        int num = Math.Max(0, (int)(_scroll.Offset.Y / (_size + 8.0)) - 1);
        int num2 = (int)Math.Ceiling((_scroll.Offset.Y + Bounds.Height) / (_size + 8.0)) + 1;
        int first = num * _columns;
        int last = Math.Min(_games.Count, (num2 + 1) * _columns);
        int[] array = _realized.Keys.Where((int i) => i < first || i >= last).ToArray();
        foreach (int key in array)
        {
            _canvas.Children.Remove(_realized[key]);
            _realized.Remove(key);
        }
        for (int num4 = first; num4 < last; num4++)
        {
            if (_realized.ContainsKey(num4))
            {
                continue;
            }
            int index = num4;
            GameLibraryEntry game = _games[index];
            Button button = _factory(game, _size);
            double width = (button.Height = _size);
            button.Width = width;
            button.Classes.Add("dune-cover");
            button.Classes.Set("dune-selected", game.EntryId == _selected);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.VerticalContentAlignment = VerticalAlignment.Stretch;
            button.Click += (object? _, RoutedEventArgs _) =>
            {
                _select(game);
                Select(game.EntryId);
            };
            button.KeyDown += (object? _, KeyEventArgs e) =>
            {
                int num5 = e.Key switch
                {
                    Key.Left => index - 1,
                    Key.Right => index + 1,
                    Key.Up => index - _columns,
                    Key.Down => index + _columns,
                    Key.Home => 0,
                    Key.End => _games.Count - 1,
                    _ => -1,
                };
                bool flag = num5 < 0;
                if (flag)
                {
                    Key key2 = e.Key;
                    bool flag2 = (uint)(key2 - 23) <= 1u;
                    flag = !flag2;
                }
                if (!flag)
                {
                    e.Handled = true;
                    ScrollToIndex(num5);
                }
            };
            Canvas.SetLeft(button, (double)(index % _columns) * (_size + 8.0));
            Canvas.SetTop(button, (double)(index / _columns) * (_size + 8.0));
            _realized.Add(index, button);
            _canvas.Children.Add(button);
        }
    }
}
