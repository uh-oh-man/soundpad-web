using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VoiceReplay.Core;

namespace VoiceReplay.App;

public sealed class Waveform : FrameworkElement
{
    private (float Min, float Max)[] peaks = [];
    public double Duration
    {
        get; private set;
    }
    public double Start
    {
        get; set;
    }
    public double End
    {
        get; set;
    }
    public new double Cursor
    {
        get; set;
    }
    private double anchor;
    private int drag;
    private DrawingGroup? ruler;
    private (double Width, double Height, double Duration, double Offset) rulerKey;
    public event Action? SelectionChanged;
    public event Action? Seek;
    public event Action? SelectionBegan;
    public double RulerOffset
    {
        get; set;
    }
    public void Live(LiveFrame frame)
    {
        peaks = frame.Peaks;
        Duration = frame.Seconds;
        RulerOffset = -frame.Seconds;
        InvalidateVisual();
    }
    public void Load(float[] samples, int rate)
    {
        peaks = AudioFiles.Peaks(samples, 16000);
        Duration = samples.Length / (double)rate;
        Start = 0;
        End = Duration;
        Cursor = 0;
        RulerOffset = 0;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 28, 43)), null, new Rect(0, 0, width, height));
        if (Duration <= 0)
            return;
        double x1 = Math.Clamp(Start / Duration * width, 0, width), x2 = Math.Clamp(End / Duration * width, 0, width);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(65, 66, 191, 190)), null, new Rect(x1, 28, Math.Max(0, x2 - x1), Math.Max(1, height - 28)));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(92, 212, 205)), 1);
        int bins = Math.Max(1, (int)width);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
            for (int i = 0; i < bins; i++)
            {
                int a = (int)((long)i * peaks.Length / bins), b = Math.Max(a + 1, (int)((long)(i + 1) * peaks.Length / bins));
                float min = 1, max = -1;
                for (int j = a; j < Math.Min(b, peaks.Length); j++)
                {
                    min = Math.Min(min, peaks[j].Min);
                    max = Math.Max(max, peaks[j].Max);
                }
                path.BeginFigure(new Point(i, height * .55 - max * height * .35), false, false);
                path.LineTo(new Point(i, height * .55 - min * height * .35), true, false);
            }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
        var key = (width, height, Duration, RulerOffset);
        if (ruler is null || rulerKey != key)
        {
            rulerKey = key;
            ruler = new DrawingGroup();
            using (var axes = ruler.Open())
            {
                axes.DrawLine(new Pen(Brushes.DarkSlateGray, 1), new Point(0, height * .55), new Point(width, height * .55));
                double step = Math.Max(.05, Math.Pow(10, Math.Floor(Math.Log10(Duration * 75 / Math.Max(1, width)))));
                if (Duration / step * 75 > width)
                    step *= 5;
                var gridPen = new Pen(Brushes.SlateGray, .5);
                gridPen.Freeze();
                var face = new Typeface("Segoe UI");
                for (double t = 0; t <= Duration; t += step)
                {
                    double x = t / Duration * width;
                    axes.DrawLine(gridPen, new Point(x, 24), new Point(x, height));
                    axes.DrawText(new FormattedText($"{t + RulerOffset:0.##}s", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, Brushes.LightSlateGray, 1), new Point(Math.Min(x + 3, width - 28), 4));
                }
            }
            ruler.Freeze();
        }
        dc.DrawDrawing(ruler);
        foreach (double x in new[] { x1, x2 })
        {
            dc.DrawLine(new Pen(Brushes.Gold, 2), new Point(x, 27), new Point(x, height));
            dc.DrawRectangle(Brushes.Gold, null, new Rect(Math.Max(0, x - 4), 28, 8, 13));
        }
        double cursor = Cursor / Duration * width;
        dc.DrawLine(new Pen(Brushes.White, 2), new Point(cursor, 24), new Point(cursor, height));
    }
    private double Time(MouseEventArgs e) => Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth) * Duration, 0, Duration);
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration <= 0)
            return;
        SelectionBegan?.Invoke();
        double t = Time(e), tolerance = 8 / ActualWidth * Duration;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || e.GetPosition(this).Y < 27)
        {
            Cursor = t;
            drag = 3;
        }
        else if (Math.Abs(t - Start) < tolerance)
            drag = 1;
        else if (Math.Abs(t - End) < tolerance)
            drag = 2;
        else
        {
            anchor = t;
            Start = t;
            End = t;
            drag = 4;
        }
        CaptureMouse();
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || drag == 0)
            return;
        double t = Time(e);
        if (drag == 1)
            Start = Math.Min(t, End);
        else if (drag == 2)
            End = Math.Max(t, Start);
        else if (drag == 3)
        {
            Cursor = t;
        }
        else
        {
            Start = Math.Min(anchor, t);
            End = Math.Max(anchor, t);
        }
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool seek = drag == 3;
        drag = 0;
        ReleaseMouseCapture();
        if (seek)
            Seek?.Invoke();
    }
}
