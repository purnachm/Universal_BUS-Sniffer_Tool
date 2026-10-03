using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SniffOS.Core.Acquisition;
using SniffOS.Core.Waveform;

namespace SniffOS.App.Views;

/// <summary>
/// A retained WPF surface that draws all digital channels directly. It asks the core renderer for
/// one min/max envelope per visible pixel and therefore does not allocate one visual per sample.
/// Mouse wheel zooms around the cursor; left-drag pans the sample viewport.
/// </summary>
public sealed class WaveformSurface : FrameworkElement
{
    public static readonly DependencyProperty CaptureProperty = DependencyProperty.Register(nameof(Capture), typeof(RawCapture), typeof(WaveformSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ChannelCountProperty = DependencyProperty.Register(nameof(ChannelCount), typeof(int), typeof(WaveformSurface), new FrameworkPropertyMetadata(16, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FirstSampleProperty = DependencyProperty.Register(nameof(FirstSample), typeof(ulong), typeof(WaveformSurface), new FrameworkPropertyMetadata(0UL, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SamplesPerPixelProperty = DependencyProperty.Register(nameof(SamplesPerPixel), typeof(double), typeof(WaveformSurface), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CursorAProperty = DependencyProperty.Register(nameof(CursorA), typeof(double), typeof(WaveformSurface), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CursorBProperty = DependencyProperty.Register(nameof(CursorB), typeof(double), typeof(WaveformSurface), new FrameworkPropertyMetadata(1000d, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool _dragging;
    private Point _dragOrigin;
    private ulong _dragFirstSample;

    public RawCapture? Capture { get => (RawCapture?)GetValue(CaptureProperty); set => SetValue(CaptureProperty, value); }
    public int ChannelCount { get => (int)GetValue(ChannelCountProperty); set => SetValue(ChannelCountProperty, value); }
    public ulong FirstSample { get => (ulong)GetValue(FirstSampleProperty); set => SetValue(FirstSampleProperty, value); }
    public double SamplesPerPixel { get => (double)GetValue(SamplesPerPixelProperty); set => SetValue(SamplesPerPixelProperty, value); }
    public double CursorA { get => (double)GetValue(CursorAProperty); set => SetValue(CursorAProperty, value); }
    public double CursorB { get => (double)GetValue(CursorBProperty); set => SetValue(CursorBProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 17, 24)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var capture = Capture;
        var width = (int)Math.Max(1d, Math.Min(8192d, Math.Ceiling(ActualWidth)));
        var count = Math.Clamp(ChannelCount, 1, 16);
        var rowHeight = 28d;
        var samplesPerPixel = SamplesPerPixel > 0 && !double.IsNaN(SamplesPerPixel) && !double.IsInfinity(SamplesPerPixel) ? SamplesPerPixel : 1;
        var viewport = new WaveformViewport(FirstSample, samplesPerPixel, width);

        for (var channel = 0; channel < count; channel++)
        {
            var top = channel * rowHeight;
            var lowY = top + rowHeight - 7;
            var highY = top + 7;
            drawingContext.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(32, 45, 61)), 1), new Point(0, top + rowHeight - 0.5), new Point(ActualWidth, top + rowHeight - 0.5));
            if (capture is null) continue;
            var columns = WaveformRenderer.Render(capture, channel, viewport);
            var signalPen = new Pen(channel % 2 == 0 ? new SolidColorBrush(Color.FromRgb(53, 199, 160)) : new SolidColorBrush(Color.FromRgb(112, 180, 255)), 1.2);
            var previousY = 0d;
            var previousX = 0d;
            var connected = false;
            for (var x = 0; x < columns.Count; x++)
            {
                var column = columns[x];
                if (!column.HasSamples)
                {
                    connected = false;
                    continue;
                }
                var y = column.HasHigh ? highY : lowY;
                var px = x + 0.5;
                if (connected) drawingContext.DrawLine(signalPen, new Point(previousX, previousY), new Point(px, y));
                if (column.HasTransition) drawingContext.DrawLine(signalPen, new Point(px, highY), new Point(px, lowY));
                previousX = px; previousY = y; connected = true;
            }
        }

        if (capture is not null)
        {
            foreach (var gap in capture.Gaps)
            {
                if (gap.StartSampleIndex < FirstSample) continue;
                var x = (gap.StartSampleIndex - FirstSample) / samplesPerPixel;
                if (x >= 0 && x <= ActualWidth)
                    drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 240, 106, 115)), null, new Rect(x, 0, Math.Max(2d, 2d / samplesPerPixel), Math.Min(ActualHeight, count * rowHeight)));
            }
        }
        DrawCursor(drawingContext, CursorA, new SolidColorBrush(Color.FromRgb(244, 184, 96)), samplesPerPixel);
        DrawCursor(drawingContext, CursorB, new SolidColorBrush(Color.FromRgb(240, 106, 115)), samplesPerPixel);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (ActualWidth <= 0) return;
        var oldScale = Math.Max(0.0001, SamplesPerPixel);
        var factor = e.Delta > 0 ? 0.8 : 1.25;
        var newScale = Math.Clamp(oldScale * factor, 0.01, 10_000_000);
        var mouse = e.GetPosition(this);
        var sampleAtMouse = FirstSample + (ulong)Math.Max(0d, mouse.X * oldScale);
        var shift = (ulong)Math.Max(0d, mouse.X * newScale);
        var proposedStart = sampleAtMouse > shift ? sampleAtMouse - shift : 0;
        SamplesPerPixel = newScale;
        FirstSample = proposedStart;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragOrigin = e.GetPosition(this);
        _dragFirstSample = FirstSample;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetPosition(this);
        var delta = (long)Math.Round((point.X - _dragOrigin.X) * Math.Max(0.0001, SamplesPerPixel));
        FirstSample = delta >= 0 && (ulong)delta < _dragFirstSample ? 0 : delta < 0 ? _dragFirstSample + (ulong)(-delta) : _dragFirstSample - (ulong)delta;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void DrawCursor(DrawingContext context, double sample, Brush brush, double samplesPerPixel)
    {
        if (sample < FirstSample) return;
        var x = (sample - FirstSample) / samplesPerPixel;
        if (x < 0 || x > ActualWidth) return;
        context.DrawLine(new Pen(brush, 1), new Point(x, 0), new Point(x, Math.Min(ActualHeight, Math.Max(28d, ChannelCount * 28d))));
    }
}
