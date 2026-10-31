using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace NxInfinity;

internal sealed class StartupSequence : Grid
{
    private sealed class IntroBackdrop : Control
    {
        private static readonly Geometry Ribbon = Geometry.Parse("M -290 0 C -290 -138 -125 -138 0 0 C 125 138 290 138 290 0 C 290 -138 125 -138 0 0 C -125 138 -290 138 -290 0");

        public double Progress { get; set; }

        public override void Render(DrawingContext context)
        {
            double width = Bounds.Width;
            double height = Bounds.Height;
            double progress = Progress;
            context.FillRectangle(Brush.Parse("#07090E"), new Rect(Bounds.Size));
            double num = Math.Min(width / 900.0, height / 650.0);
            using (context.PushTransform(Matrix.CreateScale(num, num) * Matrix.CreateTranslation(width / 2.0, height / 2.0 - 24.0)))
            {
                double num2 = 1.0 - Math.Pow(1.0 - Math.Clamp(progress / 0.6, 0.0, 1.0), 3.0);
                using (context.PushOpacity(0.35 * Math.Sin(Math.PI * Math.Clamp(progress, 0.0, 1.0))))
                {
                    RadialGradientBrush brush = new RadialGradientBrush
                    {
                        GradientStops = new GradientStops
                        {
                            new GradientStop(Color.Parse("#AAED2447"), 0.0),
                            new GradientStop(Color.Parse("#0007090E"), 1.0)
                        }
                    };
                    context.DrawEllipse(brush, null, new Point(0.0, 0.0), 440.0, 230.0);
                }
                using (context.PushClip(new Rect(-340.0, -220.0, 680.0 * num2, 440.0)))
                {
                    for (int num3 = 4; num3 >= 1; num3--)
                    {
                        using (context.PushOpacity(0.035))
                        {
                            context.DrawGeometry(null, new Pen(Brush.Parse("#FF365C"), num3 * 9), Ribbon);
                        }
                    }
                    using (context.PushOpacity(0.64))
                    {
                        context.DrawGeometry(null, new Pen(Brush.Parse("#FF365C"), 2.0), Ribbon);
                    }
                    using (context.PushOpacity(0.3))
                    {
                        context.DrawGeometry(null, new Pen(Brushes.White, 0.65), Ribbon);
                    }
                }
                for (int i = 0; i < 16; i++)
                {
                    double num4 = -440 + i * 59;
                    double y = Math.Sin((double)i * 7.13) * 230.0;
                    double num5 = (1.0 - num2) * (double)((i % 2 == 0) ? (-300) : 300);
                    using (context.PushOpacity(0.55 * Math.Sin(Math.PI * progress)))
                    {
                        IBrush brush2;
                        if (i % 3 != 0)
                        {
                            brush2 = Brush.Parse("#FA3F5B");
                        }
                        else
                        {
                            IBrush white = Brushes.White;
                            brush2 = white;
                        }
                        context.DrawLine(new Pen(brush2, 1.2), new Point(num4 + num5, y), new Point(num4 + num5 + 20.0 + 40.0 * (1.0 - num2), y));
                    }
                }
            }
        }
    }

    private readonly IntroBackdrop _backdrop = new IntroBackdrop();

    private readonly StackPanel _identity;

    private readonly DispatcherTimer _timer = new DispatcherTimer
    {
        Interval = TimeSpan.FromMilliseconds(25L)
    };

    private readonly Stopwatch _clock = new Stopwatch();

    private readonly Action _finished;

    public Button Skip { get; }

    public StartupSequence(Action finished)
    {
        Name = "StartupSequence";
        IsVisible = false;
        Background = Brush.Parse("#07090E");
        ClipToBounds = true;
        _finished = finished;
        Children.Add(_backdrop);
        _identity = new StackPanel
        {
            Spacing = 10.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new ScaleTransform(1.0, 1.0)
        };
        _identity.Children.Add(new BrandMark
        {
            Width = 156.0,
            Height = 156.0,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        _identity.Children.Add(new TextBlock
        {
            Text = "NX Infinity",
            FontSize = 44.0,
            FontWeight = FontWeight.DemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        Children.Add(_identity);
        Skip = VisualTheme.LocalizedButton("Skip intro", (Action)Stop, (string?)null);
        Skip.Name = "SkipStartup";
        Skip.Classes.Add("subtle");
        Skip.HorizontalAlignment = HorizontalAlignment.Center;
        Skip.VerticalAlignment = VerticalAlignment.Bottom;
        Skip.Margin = new Thickness(0.0, 0.0, 0.0, 24.0);
        Children.Add(Skip);
        _timer.Tick += (object? _, EventArgs _) =>
        {
            SetFrame(_clock.Elapsed.TotalSeconds / 2.15);
            if (_clock.Elapsed.TotalSeconds >= 2.15)
            {
                Stop();
            }
        };
    }

    public void Begin()
    {
        IsVisible = true;
        SetFrame(0.0);
        _clock.Restart();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _clock.Stop();
        if (IsVisible)
        {
            IsVisible = false;
            _finished();
        }
    }

    internal void SetFrame(double progress)
    {
        double num = Math.Clamp(progress, 0.0, 1.0);
        double num2 = Math.Clamp((num - 0.12) / 0.3, 0.0, 1.0);
        double num3 = 1.0 - Math.Pow(1.0 - num2, 3.0);
        _identity.Opacity = num3;
        ScaleTransform scaleTransform = (ScaleTransform)_identity.RenderTransform;
        double scaleX = (((ScaleTransform)_identity.RenderTransform).ScaleY = 0.86 + 0.14 * num3);
        scaleTransform.ScaleX = scaleX;
        Opacity = 1.0 - Math.Clamp((num - 0.84) / 0.16, 0.0, 1.0);
        _backdrop.Progress = num;
        _backdrop.InvalidateVisual();
    }
}
