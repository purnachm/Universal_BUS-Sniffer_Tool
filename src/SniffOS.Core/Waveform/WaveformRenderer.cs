using SniffOS.Core.Acquisition;

namespace SniffOS.Core.Waveform;

public readonly record struct WaveformViewport(ulong FirstSample, double SamplesPerPixel, int PixelWidth)
{
    public ulong LastSampleExclusive => checked(FirstSample + (ulong)Math.Max(1d, Math.Ceiling(SamplesPerPixel * PixelWidth)));
}

public readonly record struct WaveformColumn(int Pixel, bool HasSamples, bool HasLow, bool HasHigh)
{
    public bool HasTransition => HasLow && HasHigh;
}

/// <summary>
/// Produces at most one column per viewport pixel. It never creates a WPF object per sample.
/// At zoomed-out levels each column is a min/max envelope; at zoomed-in levels it is a single
/// sample envelope. Gaps intentionally produce empty columns instead of invented signal values.
/// </summary>
public static class WaveformRenderer
{
    public static IReadOnlyList<WaveformColumn> Render(RawCapture capture, int channel, WaveformViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 15);
        if (viewport.PixelWidth <= 0) return Array.Empty<WaveformColumn>();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewport.SamplesPerPixel);
        if (double.IsNaN(viewport.SamplesPerPixel) || double.IsInfinity(viewport.SamplesPerPixel))
            throw new ArgumentException("SamplesPerPixel must be finite.", nameof(viewport));
        var columns = new WaveformColumn[viewport.PixelWidth];
        for (var x = 0; x < columns.Length; x++)
        {
            var start = viewport.FirstSample + (ulong)Math.Floor(x * viewport.SamplesPerPixel);
            var end = Math.Max(start + 1, viewport.FirstSample + (ulong)Math.Ceiling((x + 1) * viewport.SamplesPerPixel));
            var low = false;
            var high = false;
            var hasSamples = false;
            foreach (var chunk in capture.Chunks)
            {
                if (chunk.EndExclusive <= start) continue;
                if (chunk.FirstSampleIndex >= end) break;
                var from = (int)Math.Max(0L, (long)start - (long)chunk.FirstSampleIndex);
                var to = (int)Math.Min((long)chunk.Samples.Length, Math.Max(0L, (long)end - (long)chunk.FirstSampleIndex));
                for (var i = from; i < to; i++)
                {
                    hasSamples = true;
                    if (DigitalSample.IsHigh(chunk.Samples[i], channel)) high = true; else low = true;
                    if (low && high) break;
                }
                if (low && high) break;
            }
            columns[x] = new WaveformColumn(x, hasSamples, low, high);
        }
        return columns;
    }
}
