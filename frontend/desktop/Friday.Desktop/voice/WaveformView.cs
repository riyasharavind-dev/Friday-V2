using System;
using System.Windows;
using System.Windows.Media;

namespace Friday.Desktop.Voice;

public sealed class WaveformView : FrameworkElement
{
    private const int BarCount = 32;
    private readonly double[] _levels = new double[BarCount];

    public void SetLevels(float[] levels)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetLevels(levels)));
            return;
        }

        for (int i = 0; i < BarCount; i++)
        {
            _levels[i] = i < levels.Length
                ? Math.Clamp(levels[i], 0f, 1f)
                : 0;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        double step = ActualWidth / BarCount;
        double center = ActualHeight / 2;
        double thickness = Math.Clamp(step * 0.5, 1.2, 2.6);

        var pen = new Pen(Brushes.White, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();

        for (int i = 0; i < BarCount; i++)
        {
            double x = (i + 0.5) * step;
            double halfHeight = _levels[i] * (ActualHeight - 4) / 2;

            if (halfHeight < 1)
            {
                context.DrawEllipse(
                    Brushes.White, null,
                    new Point(x, center),
                    thickness / 2, thickness / 2);
            }
            else
            {
                context.DrawLine(
                    pen,
                    new Point(x, center - halfHeight),
                    new Point(x, center + halfHeight));
            }
        }
    }
}
